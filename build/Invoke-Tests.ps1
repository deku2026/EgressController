[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
# Defense in depth: production TUN/process adapters refuse execution in this test run.
$env:EGRESS_MOCK_ONLY = '1'
Get-ChildItem Env:EGRESS_LIVE_* | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
Write-Host 'Mock-only tests: real core, TUN and process termination are disabled.'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Filter '*.csproj' -Recurse |
    Sort-Object FullName

if ($projects.Count -eq 0) {
    throw 'No test projects were found.'
}

Push-Location $repositoryRoot
try {
    foreach ($project in $projects) {
        Write-Host "Running $($project.BaseName)"
        $arguments = @(
            'run',
            '--project', $project.FullName,
            '--configuration', $Configuration
        )
        if ($NoBuild) {
            $arguments += '--no-build'
        }

        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Test project failed: $($project.FullName)"
        }
    }
}
finally {
    Pop-Location
}
