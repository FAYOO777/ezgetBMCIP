$folder = "publish/$(Get-Date -Format 'yyyy-M-d-H-m')"
New-Item -ItemType Directory -Path $folder -Force | Out-Null

dotnet publish -c Release -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=false `
  -p:EnableCompressionInSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=false `
  -o $folder

Move-Item "$folder/ezgetBMCIP.exe" "$folder/ezgetBMCIP-lite.exe"
Remove-Item "$folder/*.pdb" -ErrorAction SilentlyContinue

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "git"
$psi.Arguments = "log --oneline -5"
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($psi)
$output = $p.StandardOutput.ReadToEnd()
$p.WaitForExit()
[System.IO.File]::WriteAllText("$folder\README.txt", $output, [System.Text.UTF8Encoding]::new($false))

Write-Host "Done: $folder/ezgetBMCIP-lite.exe" -ForegroundColor Green
