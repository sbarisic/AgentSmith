param([Parameter(Mandatory)][string]$Smith, [Parameter(Mandatory)][string]$Data,
    [Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Output,
    [string]$AnalysisId = 'b937ab6644ac4687a02852473e20ca53')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Output -Force | Out-Null
# Caller stops the owned idle service before this script; saved evidence must remain usable.
$status = (& $Smith service status --data-dir $Data --json | ConvertFrom-Json).payload
if ($status.available) { throw 'Start the offline portion with the service stopped.' }
& $Smith audio analysis read $AnalysisId transcript --data-dir $Data --json > "$Output/offline-read.ndjson"
if ($LASTEXITCODE -ne 0) { throw 'Offline evidence read failed.' }
& $Smith audio analysis export $AnalysisId "$Output/export" --data-dir $Data --json > "$Output/offline-export.ndjson"
if ($LASTEXITCODE -ne 0) { throw 'Offline export failed.' }
$status = (& $Smith service start --data-dir $Data --json | ConvertFrom-Json).payload
if (!$status.available) { throw 'Published service did not start.' }
$info = [Diagnostics.ProcessStartInfo]::new($Smith)
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
foreach ($argument in @('audio','analyze','file',$Source,'--data-dir',$Data,'--json')) { $info.ArgumentList.Add($argument) }
$client = [Diagnostics.Process]::Start($info)
$stdout = $client.StandardOutput.ReadToEndAsync(); $stderr = $client.StandardError.ReadToEndAsync()
$timer = [Diagnostics.Stopwatch]::StartNew(); $helperId = $null
$peaks = @{ clientBytes=0L; serviceBytes=0L; helperBytes=0L; aggregateBytes=0L }
while (!$client.HasExited) {
    if (!$helperId -and $timer.Elapsed.TotalSeconds -gt 3) {
        $live = (& $Smith service status --data-dir $Data --json | ConvertFrom-Json).payload
        $helperId = $live.helperProcessId
    }
    $aggregate = 0L
    foreach ($entry in @(@('clientBytes',$client.Id),@('serviceBytes',$status.processId),@('helperBytes',$helperId))) {
        if (!$entry[1]) { continue }
        $observed = Get-Process -Id $entry[1] -ErrorAction SilentlyContinue
        if ($observed) { $bytes = $observed.PrivateMemorySize64; $peaks[$entry[0]] = [Math]::Max($peaks[$entry[0]],$bytes); $aggregate += $bytes }
    }
    $peaks.aggregateBytes = [Math]::Max($peaks.aggregateBytes,$aggregate)
    Start-Sleep -Milliseconds 250
}
$text = $stdout.GetAwaiter().GetResult(); $errors = $stderr.GetAwaiter().GetResult()
[IO.File]::WriteAllText("$Output/vbr-summary.ndjson",$text + $errors)
if ($client.ExitCode -ne 0) { throw "Analysis failed: $text $errors" }
$job = (($text.Trim() -split "`n")[-1] | ConvertFrom-Json).payload.job
if ($job.state -ne 'Completed' -or $job.processedSeconds -ne 380) { throw 'Incomplete VBR coverage.' }
$peaks.seconds = $timer.Elapsed.TotalSeconds; $peaks.analysisId = $job.id
$measured = (& $Smith service status --data-dir $Data --json | ConvertFrom-Json).payload
$peaks.helperJobPeakCommittedBytes = $measured.helperPeakCommittedBytes
$peaks.helperLauncherBytes = $peaks.helperBytes; $peaks.Remove('helperBytes')
$peaks.Remove('aggregateBytes')
$peaks.note = 'The helper PID is the venv launcher. Use the Job Object peak for its full process tree; peaks are not simultaneous aggregate private memory.'
$peaks | ConvertTo-Json | Set-Content "$Output/metrics.json"
& $Smith audio analysis resume $job.id --data-dir $Data --json > "$Output/cached-resume.ndjson"
if ($LASTEXITCODE -ne 0) { throw 'Fresh resume failed.' }
@("/audio analysis attach $($job.id)", '/exit') | & $Smith chat --data-dir $Data --workspace (Get-Location).Path --delegation manual --json > "$Output/chat-attach.ndjson"
if ($LASTEXITCODE -ne 0 -or !(Select-String -LiteralPath "$Output/chat-attach.ndjson" -SimpleMatch 'Recording evidence attached')) { throw 'Fresh chat attachment failed.' }
& $Smith service status --data-dir $Data --json > "$Output/service-final.json"
$peaks | ConvertTo-Json
