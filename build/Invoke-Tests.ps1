[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
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
