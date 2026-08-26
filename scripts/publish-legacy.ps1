# Publish legacy .NET Framework 4.6 version.
# Target: Windows 7 SP1 / Windows 8 / Windows 8.1
# Output: publish\ezgetBMCIP-legacy-net46\  (framework-dependent, multi-file)
# Requires .NET Framework 4.6 on target machine.
# .NET Framework apps cannot be published as single-file. Deploy the entire output folder.

$ErrorActionPreference = "Stop"

$legacyProj = "ezgetBMCIP.Legacy\ezgetBMCIP.Legacy.csproj"
$publishDir = "publish\ezgetBMCIP-legacy-net46"
$zipPath = "publish\ezgetBMCIP-legacy-net46.zip"
$dotnet46Installer = "third_party\dotnetfx46\NDP46-KB3045557-x86-x64-AllOS-ENU.exe"

$versionTag = git describe --tags --abbrev=0 2>$null
if ([string]::IsNullOrWhiteSpace($versionTag)) {
  $versionTag = "v1.0.0"
}
$versionNumber = $versionTag.TrimStart("v")

if (Test-Path $publishDir) {
  Remove-Item $publishDir -Recurse -Force
}

Write-Host "Building ezgetBMCIP Legacy (.NET Framework 4.6)..." -ForegroundColor Cyan

dotnet publish $legacyProj -c Release `
  -p:Version=$versionNumber `
  -p:InformationalVersion=$versionTag `
  -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Remove debug symbols from publish output
Remove-Item "$publishDir\*.pdb" -ErrorAction SilentlyContinue

if (-not (Test-Path $dotnet46Installer)) {
  throw ".NET Framework 4.6 offline installer is missing: $dotnet46Installer"
}
Copy-Item $dotnet46Installer "$publishDir\NDP46-KB3045557-x86-x64-AllOS-ENU.exe" -Force

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$quickStartSource = Join-Path $repositoryRoot 'docs\legacy-quickstart.txt'
if (-not (Test-Path -LiteralPath $quickStartSource -PathType Leaf)) {
  throw "Legacy quick-start source is missing: $quickStartSource"
}
Copy-Item -LiteralPath $quickStartSource -Destination "$publishDir\使用教程.txt" -Force

# Generate README
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "git"
$psi.Arguments = "log --oneline -5"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($psi)
$gitLog = $p.StandardOutput.ReadToEnd()
$p.WaitForExit()

$readme = @"
IPMI/BMC 直连助手 Legacy (.NET Framework 4.6)
Project: ezgetBMCIP
Version: $versionTag

=== Runtime Requirement ===
.NET Framework 4.6 must be installed on the target machine.
If it is not installed, run NDP46-KB3045557-x86-x64-AllOS-ENU.exe from this folder first.
Official download page: https://www.microsoft.com/zh-cn/download/details.aspx?id=48137

Windows 7 SP1 / Windows 8 / Windows 8.1 are supported.
This is NOT a self-contained / single-file build.
Deploy the entire folder to the target machine.

=== Recent Changes ===
$gitLog
"@
[System.IO.File]::WriteAllText("$publishDir\README.txt", $readme, [System.Text.UTF8Encoding]::new($false))

if (Test-Path $zipPath) {
  Remove-Item $zipPath -Force
}
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Done: $publishDir\" -ForegroundColor Green
$exeSize = (Get-Item "$publishDir\ezgetBMCIP-legacy.exe").Length / 1KB
Write-Host "Main exe size: $exeSize KB" -ForegroundColor Green
Write-Host "Zip: $zipPath" -ForegroundColor Green
Write-Host ""
Write-Host "DEPLOY: Copy the entire '$publishDir' folder to the target machine." -ForegroundColor Yellow
Write-Host "REQUIREMENT: .NET Framework 4.6 must be installed on target." -ForegroundColor Yellow
Write-Host "OFFLINE INSTALLER: $publishDir\NDP46-KB3045557-x86-x64-AllOS-ENU.exe" -ForegroundColor Yellow
