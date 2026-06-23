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

$quickStart = @"
ezgetBMCIP Legacy 使用教程

适用系统：
Windows 7 SP1 / Windows 8 / Windows 8.1

首次使用：
1. 如果本机还没有安装 .NET Framework 4.6，请先运行本文件夹里的：
   NDP46-KB3045557-x86-x64-AllOS-ENU.exe

2. .NET Framework 4.6 安装完成后，建议重启电脑。

3. 回到本文件夹，右键运行：
   ezgetBMCIP-legacy.exe

使用步骤：
1. 选择要连接服务器 IPMI/BMC 管理口的有线网卡。
2. 点击“开始”。
3. 按提示把网线插到服务器的 IPMI/BMC 专用管理口。
4. 等待工具分配 IP 并自动打开 BMC 管理页面。
5. BMC 操作完成后，回到工具点击“完成 / 退出”，等待网卡恢复 DHCP。

注意事项：
- 这个工具只用于笔记本和 IPMI/BMC 管理口直连，不适合接入局域网使用。
- 运行期间会临时修改所选网卡的 IPv4 配置。
- 退出时会把所选网卡恢复为 DHCP，不会恢复为原来的静态 IP 配置。
- 如果遇到问题，请点击工具左下角“日志”，把日志内容发给维护人员排查。
"@
[System.IO.File]::WriteAllText("$publishDir\使用教程.txt", $quickStart, [System.Text.UTF8Encoding]::new($true))

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
ezgetBMCIP Legacy (.NET Framework 4.6)
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
