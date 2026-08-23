[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolver = Join-Path $PSScriptRoot 'Resolve-ReleaseVersion.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "EgressController.ReleaseVersionTests\$([Guid]::NewGuid().ToString('N'))"

function Invoke-Git {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & git @Arguments *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed."
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)] $Expected,
        [Parameter(Mandatory)] $Actual,
        [Parameter(Mandatory)][string] $Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Push-Location $root
    Invoke-Git -Arguments @('init', '--quiet')
    Invoke-Git -Arguments @('config', 'user.name', 'release-test')
    Invoke-Git -Arguments @('config', 'user.email', 'release-test@example.invalid')

    Invoke-Git -Arguments @('commit', '--allow-empty', '--quiet', '-m', 'initial')
    $firstSha = (& git rev-parse HEAD).Trim()

    $initial = & $resolver -TargetSha $firstSha
    Assert-Equal 'v0.1.0' $initial.Tag 'A repository without release tags should start at v0.1.0.'
    Assert-Equal $true $initial.CreateTag 'The initial release tag should be created.'

    Invoke-Git -Arguments @('tag', '-a', 'v0.1.1', '-m', 'v0.1.1', $firstSha)

    Invoke-Git -Arguments @('commit', '--allow-empty', '--quiet', '-m', 'second')
    $secondSha = (& git rev-parse HEAD).Trim()

    $next = & $resolver -TargetSha $secondSha
    Assert-Equal 'v0.1.2' $next.Tag 'The next patch tag was not selected.'
    Assert-Equal '0.1.2' $next.Version 'The package version was not normalized.'
    Assert-Equal $true $next.CreateTag 'A new commit should require a tag.'

    $requested = & $resolver -TargetSha $secondSha -RequestedTag v2.0.0
    Assert-Equal 'v2.0.0' $requested.Tag 'The requested tag was not preserved.'
    Assert-Equal $true $requested.CreateTag 'A missing requested tag should be created.'

    $invalidDetected = $false
    try {
        & $resolver -TargetSha $secondSha -RequestedTag release-2.0.0 *> $null
    }
    catch {
        $invalidDetected = $true
    }
    Assert-Equal $true $invalidDetected 'An invalid release tag must be rejected.'

    Invoke-Git -Arguments @('tag', '-a', 'v0.1.2', '-m', 'v0.1.2', $secondSha)
    $existing = & $resolver -TargetSha $secondSha
    Assert-Equal 'v0.1.2' $existing.Tag 'An existing tag on the target commit should be reused.'
    Assert-Equal $false $existing.CreateTag 'An existing target tag must not be recreated.'

    $conflictDetected = $false
    try {
        & $resolver -TargetSha $secondSha -RequestedTag v0.1.1 *> $null
    }
    catch {
        $conflictDetected = $true
    }
    Assert-Equal $true $conflictDetected 'A tag pointing to another commit must be rejected.'

    Invoke-Git -Arguments @('tag', '-a', 'v1.2.3.4', '-m', 'v1.2.3.4', $secondSha)
    Invoke-Git -Arguments @('commit', '--allow-empty', '--quiet', '-m', 'third')
    $thirdSha = (& git rev-parse HEAD).Trim()
    $fourPart = & $resolver -TargetSha $thirdSha
    Assert-Equal 'v1.2.3.5' $fourPart.Tag 'A four-part version should increment its revision.'

    $outputFile = Join-Path $root 'github-output.txt'
    $null = & $resolver -TargetSha $thirdSha -OutputFile $outputFile
    $outputs = Get-Content -LiteralPath $outputFile
    Assert-Equal $true ($outputs -contains "target_sha=$thirdSha") 'The target SHA output is missing.'
    Assert-Equal $true ($outputs -contains 'tag=v1.2.3.5') 'The tag output is missing.'
    Assert-Equal $true ($outputs -contains 'version=1.2.3.5') 'The version output is missing.'
    Assert-Equal $true ($outputs -contains 'create_tag=true') 'The create_tag output is missing.'

    Write-Host 'Release version resolver tests passed.'
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
