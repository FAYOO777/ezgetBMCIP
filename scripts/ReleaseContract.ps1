function Resolve-ReleaseTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Tag
    )

    $pattern = '^v(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<channel>test|beta|rc)\.(?<sequence>[1-9]\d*))?$'
    $match = [regex]::Match($Tag, $pattern)
    if (-not $match.Success) {
        throw "Invalid release tag '$Tag'. Use vX.Y.Z for a formal release or vX.Y.Z-(test|beta|rc).N for a prerelease."
    }

    $channel = if ($match.Groups['channel'].Success) { $match.Groups['channel'].Value } else { '' }
    [pscustomobject]@{
        Tag          = $Tag
        Version      = "{0}.{1}.{2}" -f $match.Groups['major'].Value, $match.Groups['minor'].Value, $match.Groups['patch'].Value
        IsPrerelease = -not [string]::IsNullOrWhiteSpace($channel)
        Channel      = $channel
    }
}

function Assert-ReleaseCommitMatchesMain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TagCommit,
        [Parameter(Mandatory)]
        [string]$MainCommit
    )

    $tagValue = $TagCommit.Trim()
    $mainValue = $MainCommit.Trim()
    if ([string]::IsNullOrWhiteSpace($tagValue) -or [string]::IsNullOrWhiteSpace($mainValue)) {
        throw 'Both the release-tag commit and origin/main commit are required.'
    }

    if (-not [string]::Equals($tagValue, $mainValue, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release tag commit $tagValue does not match origin/main commit $mainValue."
    }
}

function Assert-R2VersionManifestEntries {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()]
        [object[]]$Entries
    )

    $seenVersions = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($entry in $Entries) {
        if ($null -eq $entry) {
            throw 'versions.json contains a null entry.'
        }

        $version = [string]$entry.version
        if ([string]::IsNullOrWhiteSpace($version)) {
            throw 'Every versions.json entry must contain a non-empty version.'
        }

        if (-not $seenVersions.Add($version)) {
            throw "versions.json contains duplicate version '$version'."
        }
    }
}

function Read-R2VersionManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "R2 versions.json was not downloaded: $Path"
    }

    $json = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "R2 versions.json is empty: $Path"
    }

    if (-not $json.TrimStart().StartsWith('[')) {
        throw "R2 versions.json must contain a JSON array: $Path"
    }

    try {
        $parsed = ConvertFrom-Json -InputObject $json -ErrorAction Stop
    }
    catch {
        throw "R2 versions.json is not valid JSON: $Path. $($_.Exception.Message)"
    }

    $entries = if ($json.Trim() -match '^\[\s*\]$') { [object[]]@() } else { [object[]]@($parsed) }
    Assert-R2VersionManifestEntries -Entries $entries
    return $entries
}

function New-R2VersionManifest {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()]
        [object[]]$ExistingEntries,
        [Parameter(Mandatory)]
        [psobject]$ReleaseInfo,
        [Parameter(Mandatory)]
        [string]$ReleaseDate,
        [Parameter(Mandatory)]
        [string]$PublicDomain,
        [Parameter(Mandatory)]
        [Int64]$FullSize,
        [Parameter(Mandatory)]
        [Int64]$LiteSize,
        [Parameter(Mandatory)]
        [Int64]$LegacySize
    )

    Assert-R2VersionManifestEntries -Entries $ExistingEntries
    if ([string]::IsNullOrWhiteSpace($PublicDomain)) {
        throw 'R2 public domain is required.'
    }

    $tag = $ReleaseInfo.Tag
    $entry = [pscustomobject][ordered]@{
        version    = $tag
        date       = $ReleaseDate
        prerelease = [bool]$ReleaseInfo.IsPrerelease
        assets     = @(
            [pscustomobject][ordered]@{
                name = 'ezgetBMCIP-full.zip'
                url  = "https://$PublicDomain/$tag/ezgetBMCIP-full.zip"
                size = $FullSize
                desc = '完整打包，解压后运行，无需额外环境'
            },
            [pscustomobject][ordered]@{
                name = 'ezgetBMCIP-lite.zip'
                url  = "https://$PublicDomain/$tag/ezgetBMCIP-lite.zip"
                size = $LiteSize
                desc = '体积小，解压后运行，需要系统装有 .NET Desktop Runtime 8.0'
            },
            [pscustomobject][ordered]@{
                name = 'ezgetBMCIP-legacy-net46.zip'
                url  = "https://$PublicDomain/$tag/ezgetBMCIP-legacy-net46.zip"
                size = $LegacySize
                desc = '兼容 Windows 7 SP1 / Windows 8 / Windows 8.1，压缩包内含 .NET Framework 4.6 离线安装包，解压后运行'
            }
        )
    }

    $merged = New-Object System.Collections.ArrayList
    [void]$merged.Add($entry)
    foreach ($existing in $ExistingEntries) {
        if ($existing.version -ne $tag) {
            [void]$merged.Add($existing)
        }
    }

    $result = [object[]]$merged.ToArray()
    Assert-R2VersionManifestEntries -Entries $result
    return $result
}

function Save-R2VersionManifest {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()]
        [object[]]$Entries,
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-R2VersionManifestEntries -Entries $Entries
    $directory = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($directory)) {
        throw "Manifest output directory is unavailable: $Path"
    }

    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $json = ConvertTo-Json -InputObject ([object[]]$Entries) -Depth 6
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $encoding)
}
