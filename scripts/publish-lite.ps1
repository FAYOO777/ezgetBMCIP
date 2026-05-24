# Publish lite (framework-dependent) version
# Output: publish\ezgetBMCIP-lite.exe  (~2 MB)
# Requires .NET Desktop Runtime 10 x64 on target machine.

$ErrorActionPreference = "Stop"

Write-Host "Building lite version..." -ForegroundColor Cyan

dotnet publish -c Release -r win-x64 `
  -p:PublishSingleFile=false `
  -p:SelfContained=false `
  -p:PublishReadyToRun=false `
  -o publish\lite

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Rename for clarity
Copy-Item publish\lite\ezgetBMCIP.exe publish\ezgetBMCIP-lite.exe -Force

Write-Host ""
Write-Host "Done: publish\ezgetBMCIP-lite.exe" -ForegroundColor Green
Write-Host "Size: $((Get-Item publish\ezgetBMCIP-lite.exe).Length / 1KB) KB" -ForegroundColor Green
Write-Host ""
Write-Host "WARNING: lite version requires .NET Desktop Runtime 10 x64 on target machine." -ForegroundColor Yellow
Write-Host "Download: https://dotnet.microsoft.com/en-us/download/dotnet/10.0" -ForegroundColor Yellow
