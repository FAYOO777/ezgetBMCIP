# Publish full (self-contained) version
# Output: publish\ezgetBMCIP-full.zip  (~65 MB)
# No .NET runtime required on target machine.

$ErrorActionPreference = "Stop"

Write-Host "Building full version..." -ForegroundColor Cyan

$versionTag = git describe --tags --abbrev=0 2>$null
if ([string]::IsNullOrWhiteSpace($versionTag)) {
  $versionTag = "v1.0.0"
}
$versionNumber = $versionTag.TrimStart("v")

dotnet publish -c Release -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:Version=$versionNumber `
  -p:InformationalVersion=$versionTag `
  -o publish\full

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Rename for clarity
$outputExe = "publish\ezgetBMCIP-full.exe"
$outputZip = "publish\ezgetBMCIP-full.zip"
Copy-Item publish\full\ezgetBMCIP.exe $outputExe -Force
Compress-Archive -Path $outputExe -DestinationPath $outputZip -Force

Write-Host ""
Write-Host "Done: $outputZip" -ForegroundColor Green
Write-Host "Exe: $outputExe" -ForegroundColor Green
Write-Host "Zip size: $((Get-Item $outputZip).Length / 1MB) MB" -ForegroundColor Green
