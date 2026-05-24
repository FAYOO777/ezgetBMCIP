# Publish full (self-contained) version
# Output: publish\ezgetBMCIP-full.exe  (~65 MB)
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
Copy-Item publish\full\ezgetBMCIP.exe publish\ezgetBMCIP-full.exe -Force

Write-Host ""
Write-Host "Done: publish\ezgetBMCIP-full.exe" -ForegroundColor Green
Write-Host "Size: $((Get-Item publish\ezgetBMCIP-full.exe).Length / 1MB) MB" -ForegroundColor Green
