# Builds the plugin into out\ContinuousLowRes and a zip next to it. Requires the .NET 9 SDK.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (Test-Path out) { Remove-Item out -Recurse -Force }
dotnet build ContinuousLowRes\ContinuousLowRes.csproj -c Release -o out\ContinuousLowRes
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Compress-Archive -Path out\ContinuousLowRes -DestinationPath out\ContinuousLowRes.zip -Force
Write-Host "Built: out\ContinuousLowRes  (copy to [Agent]\Plugins\ContinuousLowRes and restart Agent)"
