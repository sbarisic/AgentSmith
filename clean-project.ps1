#Requires -Version 5.1
<#
.SYNOPSIS
Preview or remove disposable AgentSmith build output and old publish snapshots.
.DESCRIPTION
Run without switches to preview. Add -Apply to permanently delete the listed items.
Close builds, tests, Visual Studio and project apps before applying cleanup.
Preserves source, Git history, current published apps, sandbox, and AppData.
Sizes are logical file sizes; actual free space can differ.
.PARAMETER IncludeTestAudio
Also delete WAV fixtures/captures under the three named acceptance-output folders.
Historical audio evidence will be lost; reports and other files are retained.
.PARAMETER IncludeBonsaiModels
Also delete bonsai/models. Local Bonsai inference requires downloading models again.
Keeps the Bonsai checkout, tools, environment, configuration and Git history.
.EXAMPLE
.\clean-project.ps1
.EXAMPLE
.\clean-project.ps1 -Apply
.EXAMPLE
.\clean-project.ps1 -IncludeTestAudio -IncludeBonsaiModels
.EXAMPLE
.\clean-project.ps1 -Apply -IncludeTestAudio -IncludeBonsaiModels
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [switch]$Apply,
    [switch]$IncludeTestAudio,
    [switch]$IncludeBonsaiModels
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')
$rootPrefix = $projectRoot + [IO.Path]::DirectorySeparatorChar
$comparison = [StringComparison]::OrdinalIgnoreCase

function Assert-LocalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($rootPrefix, $comparison)) {
        throw "Target is outside this project (or is the project root): $full"
    }
    # Check every existing ancestor, including ancestors above the project root.
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing junction/symlink/reparse-point path: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Measure-SafeTarget([string]$Path) {
    Assert-LocalPath $Path
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    [long]$bytes = 0
    while ($pending.Count -gt 0) {
        $item = Get-Item -LiteralPath $pending.Pop() -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing target containing a junction/symlink: $($item.FullName)"
        }
        if ($item.Name -eq '.git') { throw "Refusing target containing Git metadata: $($item.FullName)" }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) {
                $pending.Push($child.FullName)
            }
        } else { $bytes += $item.Length }
    }
    return $bytes
}

if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'AgentSmith.slnx') -PathType Leaf)) {
    throw 'Keep this script in the AgentSmith repository root.'
}
Assert-LocalPath (Join-Path $projectRoot 'AgentSmith.slnx')
$gitRoot = & git -C $projectRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($gitRoot).TrimEnd('\', '/') -ne $projectRoot) {
    throw 'Cannot verify the AgentSmith Git root.'
}
$tracked = @(& git -C $projectRoot -c core.quotepath=false ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read tracked files; nothing was deleted.' }

$candidates = New-Object 'System.Collections.Generic.List[object]'
function Add-Candidate([string]$RelativePath, [string]$Reason) {
    $path = [IO.Path]::GetFullPath((Join-Path $projectRoot $RelativePath))
    Assert-LocalPath $path
    if (-not (Test-Path -LiteralPath $path)) { return }
    $relative = $path.Substring($rootPrefix.Length).Replace('\', '/')
    foreach ($entry in $tracked) {
        if ($entry.Equals($relative, $comparison) -or $entry.StartsWith($relative + '/', $comparison)) {
            throw "Refusing target containing tracked source: $relative"
        }
    }
    $bytes = Measure-SafeTarget $path
    $candidates.Add([pscustomobject]@{ Path = $path; Relative = $relative; Bytes = $bytes; Reason = $Reason })
}

# Only bin/obj/TestResults next to tracked project files, never arbitrary folders named bin.
$projectDirs = @($tracked | Where-Object { $_ -match '^(src/|tests/|AgentSmithLite/).*\.csproj$' } |
    ForEach-Object { Split-Path -Parent $_ } | Sort-Object -Unique)
foreach ($dir in $projectDirs) {
    foreach ($name in @('bin', 'obj', 'TestResults')) {
        Add-Candidate (Join-Path $dir $name) 'Rebuildable build/test output'
    }
}
Add-Candidate '.vs' 'Visual Studio cache'
Add-Candidate 'TestResults' 'Test output'

# Explicit old snapshots observed during the disk audit on 2026-11-09.
# Keep publish/win-x64 (the installed smith command), AgentSmithLite,
# audio-cleanup-win-x64, and both recorder publish directories.
$oldPublishes = @(
    'before-context-fix-20261029-201504', 'context-fix-staging',
    'green-theme', 'resume-clipboard-fix', 'resume-readable', 'resume-summary',
    'unified-modes', 'white-text', 'workflow-colors', 'workflow-modes', 'workflow-render-fix'
)
foreach ($name in $oldPublishes) {
    Add-Candidate "artifacts/publish/$name" 'Old publish snapshot'
}

if ($IncludeTestAudio) {
    foreach ($folder in @('recording-analysis-acceptance', 'capture-acceptance', 'audio-service-acceptance')) {
        $audioRoot = Join-Path $projectRoot "artifacts/$folder"
        if (-not (Test-Path -LiteralPath $audioRoot)) { continue }
        # Validate before recursive enumeration, so links are never followed.
        $null = Measure-SafeTarget $audioRoot
        foreach ($file in Get-ChildItem -LiteralPath $audioRoot -File -Recurse -Force -Filter '*.wav') {
            Add-Candidate $file.FullName.Substring($rootPrefix.Length) 'Test audio (explicit opt-in)'
        }
    }
}
if ($IncludeBonsaiModels) { Add-Candidate 'bonsai/models' 'Downloaded models (explicit opt-in)' }

$candidates | Sort-Object Bytes -Descending |
    Select-Object @{n='MiB';e={[math]::Round($_.Bytes / 1MB, 1)}}, Relative, Reason |
    Format-Table -AutoSize | Out-Host
[long]$total = 0
foreach ($candidate in $candidates) { $total += $candidate.Bytes }
Write-Host ('Selected: {0} targets, {1:N2} GiB ({2:N0} bytes).' -f $candidates.Count, ($total / 1GB), $total)
if (-not $Apply) {
    Write-Host 'Preview only. Run again with -Apply (and the same optional switches) to delete.'
    return
}

# Do not terminate applications. Refuse cleanup if an executable in this checkout is running.
$running = @(Get-Process | Where-Object {
    $exe = $_.Path
    $exe -and $exe.StartsWith($rootPrefix, $comparison)
})
if ($running.Count -gt 0 -and -not $WhatIfPreference) {
    throw ('Close these project applications before cleanup: ' + (($running | ForEach-Object { $_.ProcessName }) -join ', '))
}

[long]$removedBytes = 0
$failures = 0
foreach ($candidate in $candidates) {
    if ($PSCmdlet.ShouldProcess($candidate.Path, 'Permanently delete ' + $candidate.Reason)) {
        try {
            # Recheck absolute boundary and the whole tree immediately before removal.
            $currentBytes = Measure-SafeTarget $candidate.Path
            Remove-Item -LiteralPath $candidate.Path -Recurse -Force -ErrorAction Stop
            $removedBytes += $currentBytes
        } catch {
            $failures++
            Write-Warning "Could not fully remove $($candidate.Relative): $($_.Exception.Message)"
        }
    }
}
Write-Host ('Removed complete targets totaling {0:N2} GiB. Failed targets: {1}.' -f ($removedBytes / 1GB), $failures)
if ($failures -gt 0) { throw 'Some targets could not be fully removed. Close applications and rerun.' }
