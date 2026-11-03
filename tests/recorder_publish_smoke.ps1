param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Data
)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($Package)
$dataRoot = [IO.Path]::GetFullPath($Data)
if (!(Test-Path -LiteralPath (Join-Path $dataRoot 'recorder-ui-fixture.json'))) { throw 'Generate disposable data with prepare_recorder_ui_fixture.py first.' }
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $packageRoot 'AgentSmith.Recorder.exe'))
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
foreach ($argument in @('--data-dir', $dataRoot, '--smoke-test')) { $info.ArgumentList.Add($argument) }
$client = [Diagnostics.Process]::Start($info)
if (!$client.WaitForExit(30000)) { $client.Kill(); throw 'Headless desktop check timed out.' }
if ($client.ExitCode -ne 0) { throw "Headless desktop check failed: $($client.ExitCode)" }
$result = Get-Content -LiteralPath (Join-Path $dataRoot 'desktop-smoke.json') -Raw | ConvertFrom-Json
if (!$result.passed -or $result.windowShown) { throw 'Invalid desktop smoke result.' }
$smith = Join-Path $packageRoot 'smith.exe'
try {
    $status = (& $smith service start --data-dir $dataRoot --json | ConvertFrom-Json).payload
    if ($LASTEXITCODE -ne 0 -or !$status.available) { throw 'Matching packaged service failed to start.' }
    $recordings = (& $smith audio capture list 1 --data-dir $dataRoot --json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Published CLI could not read retained paired recordings.' }
    $captureId = (Get-ChildItem -LiteralPath (Join-Path $dataRoot 'audio/recordings') -Directory | Select-Object -First 1).Name
    & $smith audio capture export $captureId (Join-Path $dataRoot ('raw-export-' + [Guid]::NewGuid().ToString('N'))) --data-dir $dataRoot --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Paired raw export failed.' }
    $result | Add-Member -NotePropertyName serviceProtocol -NotePropertyValue $status.protocol
    $result | Add-Member -NotePropertyName pairedExport -NotePropertyValue $true
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $result | Add-Member -NotePropertyName elevated -NotePropertyValue $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'package-smoke.json')
    $result | ConvertTo-Json
} finally {
    & $smith service stop --data-dir $dataRoot --json | Out-Null
}
