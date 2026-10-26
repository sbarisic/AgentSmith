param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repo 'artifacts\publish\AgentSmithLite\win-x86'
$archive = Join-Path $repo 'artifacts\AgentSmithLite-win-x86.zip'

# Run from this directory so the Lite SDK pin is used, not the full application's pin.
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        & dotnet build 'tests\AgentSmithLite.Tests.csproj' -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Lite test build failed.' }
        & '.\tests\bin\Release\net6.0\win-x86\AgentSmithLite.Tests.exe'
        if ($LASTEXITCODE -ne 0) { throw 'Lite acceptance tests failed.' }
    }
    & dotnet publish 'src\AgentSmithLite.csproj' -c Release -r win-x86 --self-contained true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Lite publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $publish 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $publish 'AgentSmith-LICENSE.txt') -Force
    & (Join-Path $publish 'AgentSmithLite.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'Published x86 executable failed its startup check.' }
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force
    Write-Output "Portable folder: $publish"
    Write-Output "ZIP: $archive"
} finally {
    Pop-Location
}
