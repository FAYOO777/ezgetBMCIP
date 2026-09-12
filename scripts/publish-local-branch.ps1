param(
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string]$Version,

  [string]$OutputRoot = 'publish\local-branch'
)

# Creates a local, versioned release directory only. It never tags, pushes,
# uploads, changes an online index, or overwrites an existing local release.
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionPropsPath = Join-Path $repositoryRoot 'Version.props'
if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
  throw "Version source is missing: $versionPropsPath"
}

[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw
$declaredVersion = [string]$versionProps.Project.PropertyGroup.EzGetBmcIpVersion
if ([string]::IsNullOrWhiteSpace($declaredVersion)) {
  throw "EzGetBmcIpVersion is missing from $versionPropsPath"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
  $Version = $declaredVersion
}
elseif ($Version -ne $declaredVersion) {
  throw "Requested local release version $Version does not match source version $declaredVersion. Update Version.props first."
}

if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
  $OutputRoot = Join-Path $repositoryRoot $OutputRoot
}

$releaseDir = Join-Path $OutputRoot ('v' + $Version)
if (Test-Path -LiteralPath $releaseDir) {
  throw "Refusing to overwrite existing local release: $releaseDir"
}

$stagingDir = Join-Path $OutputRoot ('.v' + $Version + '.build-' + [guid]::NewGuid().ToString('N'))
$modernDir = Join-Path $stagingDir 'win10-11-x64-full'
$legacyDir = Join-Path $stagingDir 'win7-8-8.1-legacy-net46'
$modernZip = Join-Path $stagingDir ('ezgetBMCIP-v' + $Version + '-win10-11-x64-full.zip')
$legacyZip = Join-Path $stagingDir ('ezgetBMCIP-v' + $Version + '-win7-8-8.1-legacy-net46.zip')
$releaseNotes = Join-Path $repositoryRoot ('docs\release-notes-' + $Version + '.md')
$legacyGuide = Join-Path $repositoryRoot 'docs\legacy-quickstart.txt'
$dotnet46Installer = Join-Path $repositoryRoot 'third_party\dotnetfx46\NDP46-KB3045557-x86-x64-AllOS-ENU.exe'
$informationalVersion = 'v' + $Version

foreach ($required in @($releaseNotes, $legacyGuide, $dotnet46Installer)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
    throw "Required release asset is missing: $required"
  }
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDir | Out-Null

try {
  Write-Host 'Publishing Win10/11 x64 Full...' -ForegroundColor Cyan
  dotnet publish (Join-Path $repositoryRoot 'ezgetBMCIP.csproj') -c Release -r win-x64 `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    "-p:Version=$Version" `
    "-p:InformationalVersion=$informationalVersion" `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $modernDir
  if ($LASTEXITCODE -ne 0) { throw "Win10/11 Full publish failed with exit code $LASTEXITCODE" }
  Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $modernDir '发行说明.md')
  Compress-Archive -Path (Join-Path $modernDir '*') -DestinationPath $modernZip

  Write-Host 'Publishing Win7/8/8.1 Legacy...' -ForegroundColor Cyan
  dotnet publish (Join-Path $repositoryRoot 'ezgetBMCIP.Legacy\ezgetBMCIP.Legacy.csproj') -c Release `
    "-p:Version=$Version" `
    "-p:InformationalVersion=$informationalVersion" `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $legacyDir
  if ($LASTEXITCODE -ne 0) { throw "Legacy publish failed with exit code $LASTEXITCODE" }
  Copy-Item -LiteralPath $dotnet46Installer -Destination (Join-Path $legacyDir 'NDP46-KB3045557-x86-x64-AllOS-ENU.exe')
  Copy-Item -LiteralPath $legacyGuide -Destination (Join-Path $legacyDir '使用教程.txt')
  Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $legacyDir '发行说明.md')
  Compress-Archive -Path (Join-Path $legacyDir '*') -DestinationPath $legacyZip

  $hashLines = @(
    ((Get-FileHash -LiteralPath $modernZip -Algorithm SHA256).Hash + '  ' + (Split-Path -Leaf $modernZip))
    ((Get-FileHash -LiteralPath $legacyZip -Algorithm SHA256).Hash + '  ' + (Split-Path -Leaf $legacyZip))
  )
  [System.IO.File]::WriteAllLines((Join-Path $stagingDir 'SHA256SUMS.txt'), $hashLines, [System.Text.UTF8Encoding]::new($false))

  Move-Item -LiteralPath $stagingDir -Destination $releaseDir

  Write-Host ''
  Write-Host "Local branch release created: $releaseDir" -ForegroundColor Green
  Get-ChildItem -LiteralPath $releaseDir -File | Select-Object Name, Length
}
catch {
  if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
  }
  throw
}
