[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $TargetSha,

    [string] $RequestedTag,

    [string] $OutputFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $output = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return $output
}

function ConvertTo-ReleaseTag {
    param([Parameter(Mandatory)][string] $Tag)

    if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$') {
        throw "Release tag '$Tag' must look like v1.2.3 or v1.2.3.4."
    }

    [pscustomobject]@{
        Tag = $Tag
        Version = [Version]$Matches.version
    }
}

$resolvedTarget = @(Invoke-Git -Arguments @('rev-parse', '--verify', "$TargetSha^{commit}"))[0].Trim()
$allReleaseTags = @(
    Invoke-Git -Arguments @('tag', '--list', 'v*') |
        ForEach-Object {
            if ($_ -match '^v(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$') {
                [pscustomobject]@{
                    Tag = $_
                    Version = [Version]$Matches.version
                }
            }
        }
)

$selected = $null
$createTag = $false
if (-not [string]::IsNullOrWhiteSpace($RequestedTag)) {
    $selected = ConvertTo-ReleaseTag -Tag $RequestedTag.Trim()
    $existing = @($allReleaseTags | Where-Object Tag -EQ $selected.Tag)
    if ($existing.Count -gt 0) {
        $existingTarget = @(Invoke-Git -Arguments @('rev-list', '-n', '1', "refs/tags/$($selected.Tag)"))[0].Trim()
        if ($existingTarget -ne $resolvedTarget) {
            throw "Release tag '$($selected.Tag)' already points to $existingTarget, not $resolvedTarget."
        }
    }
    else {
        $createTag = $true
    }
}
else {
    $tagsAtTarget = @(
        Invoke-Git -Arguments @('tag', '--points-at', $resolvedTarget) |
            ForEach-Object {
                if ($_ -match '^v(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$') {
                    [pscustomobject]@{
                        Tag = $_
                        Version = [Version]$Matches.version
                    }
                }
            } |
            Sort-Object Version -Descending
    )

    if ($tagsAtTarget.Count -gt 0) {
        $selected = $tagsAtTarget[0]
    }
    else {
        $latest = @($allReleaseTags | Sort-Object Version -Descending | Select-Object -First 1)
        if ($latest.Count -eq 0) {
            $nextVersion = [Version]'0.1.0'
        }
        elseif ($latest[0].Version.Revision -ge 0) {
            $current = $latest[0].Version
            $nextVersion = [Version]::new($current.Major, $current.Minor, $current.Build, $current.Revision + 1)
        }
        else {
            $current = $latest[0].Version
            $nextVersion = [Version]::new($current.Major, $current.Minor, $current.Build + 1)
        }

        $selected = [pscustomobject]@{
            Tag = "v$nextVersion"
            Version = $nextVersion
        }
        $createTag = $true
    }
}

$result = [pscustomobject]@{
    TargetSha = $resolvedTarget
    Tag = $selected.Tag
    Version = $selected.Version.ToString()
    CreateTag = $createTag
}

if (-not [string]::IsNullOrWhiteSpace($OutputFile)) {
    @(
        "target_sha=$($result.TargetSha)"
        "tag=$($result.Tag)"
        "version=$($result.Version)"
        "create_tag=$($result.CreateTag.ToString().ToLowerInvariant())"
    ) | Add-Content -LiteralPath $OutputFile -Encoding utf8
}

$result
