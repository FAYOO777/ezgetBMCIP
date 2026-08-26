$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repositoryRoot 'scripts\ReleaseContract.ps1')

function Assert-Condition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,
        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,
        [Parameter(Mandatory)]
        [string]$Message
    )

    try {
        & $Action
    }
    catch {
        return
    }

    throw $Message
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ezgetBMCIP-release-contract-" + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

    $quickStartSource = Join-Path $repositoryRoot 'docs\legacy-quickstart.txt'
    Assert-Condition (Test-Path -LiteralPath $quickStartSource -PathType Leaf) 'The shared Legacy quick-start source is missing.'
    $quickStartText = [System.IO.File]::ReadAllText($quickStartSource)
    foreach ($requiredText in @('使用风险告知', '网络修改告知', '只恢复所选本机网卡', '不会主动还原 BMC', 'Alt+L')) {
        Assert-Condition ($quickStartText.Contains($requiredText)) "The shared Legacy quick-start omits: $requiredText"
    }
    foreach ($consumer in @(
        (Join-Path $repositoryRoot 'scripts\publish-legacy.ps1'),
        (Join-Path $repositoryRoot '.github\workflows\release.yml')
    )) {
        Assert-Condition ([System.IO.File]::ReadAllText($consumer).Contains('legacy-quickstart.txt')) "The shared Legacy quick-start is not consumed by $consumer"
    }

    $workflowText = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot '.github\workflows\release.yml'))
    foreach ($requiredWorkflowText in @(
        'fetch-depth: 0',
        'ReleaseContract.ps1',
        'dotnet run --project tests/ezgetBMCIP.SmokeTests/ezgetBMCIP.SmokeTests.csproj -c Release --no-restore',
        'draft: true',
        'versions-history/',
        'Unable to download required R2 versions.json',
        'aws s3api head-object',
        'Publish GitHub release'
    )) {
        Assert-Condition ($workflowText.Contains($requiredWorkflowText)) "Release workflow omits required safety gate: $requiredWorkflowText"
    }
    Assert-Condition (-not $workflowText.Contains('2>$null')) 'Release workflow must not hide R2 manifest download failures.'
    Assert-Condition (-not $workflowText.Contains("'[]' | Out-File")) 'Release workflow must not initialize an empty R2 manifest during release.'

    $formal = Resolve-ReleaseTag -Tag 'v1.3.6'
    Assert-Condition (-not $formal.IsPrerelease) 'A pure vX.Y.Z tag must be formal.'
    Assert-Condition ($formal.Version -eq '1.3.6') 'Formal version parsing was incorrect.'

    $prerelease = Resolve-ReleaseTag -Tag 'v1.3.6-test.2'
    Assert-Condition ($prerelease.IsPrerelease) 'A test tag must be prerelease.'
    Assert-Condition ($prerelease.Channel -eq 'test') 'Prerelease channel parsing was incorrect.'

    foreach ($invalidTag in @('v1.3', 'v1.3.6-alpha.1', 'v1.3.6-test.0', 'release-v1.3.6')) {
        Assert-Throws { Resolve-ReleaseTag -Tag $invalidTag } "Invalid tag '$invalidTag' was accepted."
    }

    $commit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    Assert-ReleaseCommitMatchesMain -TagCommit $commit -MainCommit $commit.ToUpperInvariant()
    Assert-Throws {
        Assert-ReleaseCommitMatchesMain -TagCommit $commit -MainCommit 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    } 'A release tag from a different commit was accepted.'

    $missingManifest = Join-Path $temporaryRoot 'missing.json'
    Assert-Throws { Read-R2VersionManifest -Path $missingManifest } 'A missing R2 manifest was accepted.'

    $invalidManifest = Join-Path $temporaryRoot 'invalid.json'
    [System.IO.File]::WriteAllText($invalidManifest, '{"version":"v1.0.0"}')
    Assert-Throws { Read-R2VersionManifest -Path $invalidManifest } 'A non-array R2 manifest was accepted.'

    $duplicateManifest = Join-Path $temporaryRoot 'duplicate.json'
    [System.IO.File]::WriteAllText($duplicateManifest, '[{"version":"v1.0.0"},{"version":"v1.0.0"}]')
    Assert-Throws { Read-R2VersionManifest -Path $duplicateManifest } 'A duplicate version entry was accepted.'

    $sourceManifest = Join-Path $temporaryRoot 'source.json'
    [System.IO.File]::WriteAllText($sourceManifest, @'
[
  {"version":"v1.3.5","date":"2026-07-20","prerelease":false,"assets":[]},
  {"version":"v1.2.0","date":"2026-05-27","prerelease":false,"assets":[]}
]
'@)
    $existing = @(Read-R2VersionManifest -Path $sourceManifest)
    $merged = @(New-R2VersionManifest -ExistingEntries $existing -ReleaseInfo $prerelease -ReleaseDate '2026-08-26' -PublicDomain 'dl.example.test' -FullSize 100 -LiteSize 200 -LegacySize 300)
    Assert-Condition ($merged.Count -eq 3) 'The merged manifest did not preserve historical versions.'
    Assert-Condition ($merged[0].version -eq 'v1.3.6-test.2') 'The new release was not placed first.'
    Assert-Condition ($merged[0].prerelease -eq $true) 'The test release was not marked prerelease.'
    Assert-Condition (($merged | Where-Object { $_.version -eq 'v1.3.5' }).Count -eq 1) 'An existing historical version was lost.'

    $mergedAgain = @(New-R2VersionManifest -ExistingEntries $merged -ReleaseInfo $prerelease -ReleaseDate '2026-08-27' -PublicDomain 'dl.example.test' -FullSize 101 -LiteSize 201 -LegacySize 301)
    Assert-Condition ($mergedAgain.Count -eq 3) 'Replacing the same tag was not idempotent.'
    Assert-Condition ($mergedAgain[0].date -eq '2026-08-27') 'The replacement tag did not update its release metadata.'
    Assert-Condition ($mergedAgain[0].assets[0].size -eq 101) 'The replacement tag did not update asset metadata.'

    $savedManifest = Join-Path $temporaryRoot 'saved.json'
    Save-R2VersionManifest -Entries $mergedAgain -Path $savedManifest
    $roundTripped = @(Read-R2VersionManifest -Path $savedManifest)
    Assert-Condition ($roundTripped.Count -eq 3) 'The saved manifest could not be read back without losing entries.'

    Write-Host 'Release contract tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
