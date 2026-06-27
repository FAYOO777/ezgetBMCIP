# Publish lite (framework-dependent) version
# Output: publish\ezgetBMCIP-lite.zip
# Requires .NET Desktop Runtime 8 x64 on target machine.

$ErrorActionPreference = "Stop"

Write-Host "Building lite version..." -ForegroundColor Cyan

$publishDir = "publish\lite"
$outputExe = "publish\ezgetBMCIP-lite.exe"
$outputZip = "publish\ezgetBMCIP-lite.zip"

$versionTag = git describe --tags --abbrev=0 2>$null
if ([string]::IsNullOrWhiteSpace($versionTag)) {
  $versionTag = "v1.0.0"
}
$versionNumber = $versionTag.TrimStart("v")

if (Test-Path $publishDir) {
  Remove-Item $publishDir -Recurse -Force
}

dotnet publish -c Release -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=false `
  -p:PublishReadyToRun=false `
  -p:EnableCompressionInSingleFile=false `
  -p:Version=$versionNumber `
  -p:InformationalVersion=$versionTag `
  -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$runtimeFiles = Get-ChildItem $publishDir -File | Where-Object {
  $_.Name -notin @("ezgetBMCIP.exe", "ezgetBMCIP.pdb")
}

if ($runtimeFiles.Count -gt 0) {
  $names = ($runtimeFiles | ForEach-Object Name) -join ", "
  throw "Lite publish is not a single-file app. Unexpected files: $names"
}

$exeText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes("$publishDir\ezgetBMCIP.exe"))
if ($exeText.Contains('"includedFrameworks"')) {
  throw "Lite publish contains includedFrameworks. Clean/rebuild without reusing self-contained build output."
}
if (-not $exeText.Contains('"frameworks"')) {
  throw "Lite publish does not contain framework-dependent runtime config."
}

# Rename for clarity
Copy-Item "$publishDir\ezgetBMCIP.exe" $outputExe -Force
Compress-Archive -Path $outputExe -DestinationPath $outputZip -Force

Write-Host ""
Write-Host "Done: $outputZip" -ForegroundColor Green
Write-Host "Exe: $outputExe" -ForegroundColor Green
Write-Host "Zip size: $((Get-Item $outputZip).Length / 1KB) KB" -ForegroundColor Green
Write-Host ""
Write-Host "WARNING: lite version requires .NET Desktop Runtime 8 x64 on target machine." -ForegroundColor Yellow
Write-Host "Download: https://dotnet.microsoft.com/en-us/download/dotnet/8.0/runtime" -ForegroundColor Yellow
