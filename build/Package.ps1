[CmdletBinding()]
param(
    [string] $Version = '0.1.0',
    [string] $PackageVersion,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts\package',
    [string] $PackageName = 'ArcForges.EgressController',
    [string] $Publisher = 'CN=ArcForges',
    [string] $Repository = 'deku2026/EgressController',
    [string] $CertificatePath,
    [string] $CertificatePassword = $env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $SkipMsix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-MsixVersion {
    param([Parameter(Mandatory)] [string] $Value)

    $withoutPrefix = $Value.Trim().TrimStart([char[]]'vV')
    $numeric = ($withoutPrefix -split '[-+]')[0]
    if ($numeric -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "Version '$Value' must contain three or four numeric components."
    }

    $parts = [Collections.Generic.List[int]]::new()
    foreach ($part in $numeric.Split('.')) {
        $parsed = 0
        if (-not [int]::TryParse($part, [ref]$parsed) -or $parsed -lt 0 -or $parsed -gt 65535) {
            throw "MSIX version component '$part' must be between 0 and 65535."
        }
        $parts.Add($parsed)
    }
    while ($parts.Count -lt 4) {
        $parts.Add(0)
    }

    return ($parts -join '.')
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory)] [string] $Name)

    $fromPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $roots = [Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $roots.Add((Join-Path $env:ProgramFiles 'Windows Kits\10\bin'))
    }

    $candidates = foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -Filter $Name -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq 'x64' }
        }
    }

    $selected = $candidates |
        Sort-Object -Property @{ Expression = {
            try { [version]$_.Directory.Parent.Name } catch { [version]'0.0' }
        }; Descending = $true } |
        Select-Object -First 1
    if ($null -eq $selected) {
        throw "$Name was not found. Install a Windows 10/11 SDK."
    }

    return $selected.FullName
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}

$relativeOutput = [IO.Path]::GetRelativePath($repositoryRoot, $outputRoot)
if ($relativeOutput -eq '..' -or $relativeOutput.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
    throw 'OutputDirectory must be inside the repository.'
}
if ($outputRoot -eq $repositoryRoot) {
    throw 'OutputDirectory cannot be the repository root.'
}

$semanticVersion = $Version.Trim().TrimStart([char[]]'vV')
if ([string]::IsNullOrWhiteSpace($semanticVersion)) {
    throw 'Version cannot be empty.'
}
$resolvedPackageVersion = if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    ConvertTo-MsixVersion $Version
}
else {
    ConvertTo-MsixVersion $PackageVersion
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$publishDirectory = Join-Path $outputRoot 'publish'
$stagingDirectory = Join-Path $outputRoot 'msix-staging'
$applicationProject = Join-Path $repositoryRoot 'src\EgressController.App\EgressController.App.csproj'
$portableZip = Join-Path $outputRoot 'EgressController-win-x64.zip'

Push-Location $repositoryRoot
try {
    $publishArguments = @(
        'publish', $applicationProject,
        '--configuration', $Configuration,
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '-p:PublishAot=true',
        "-p:Version=$semanticVersion",
        "-p:AssemblyVersion=$resolvedPackageVersion",
        "-p:FileVersion=$resolvedPackageVersion",
        "-p:InformationalVersion=$semanticVersion"
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'NativeAOT publish failed.'
    }

    # NativeAOT emits native PDBs that contain build-machine paths and are not required at
    # runtime. Keep release artifacts portable and free of machine-specific debug metadata.
    Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File |
        Remove-Item -Force

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableZip -CompressionLevel Optimal

    if (-not $SkipMsix) {
        New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $publishDirectory -Force |
            Copy-Item -Destination $stagingDirectory -Recurse -Force

        $stageAssets = Join-Path $stagingDirectory 'Assets'
        New-Item -ItemType Directory -Path $stageAssets -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'packaging\Assets') -File |
            Copy-Item -Destination $stageAssets -Force

        $escapedName = [Security.SecurityElement]::Escape($PackageName)
        $escapedPublisher = [Security.SecurityElement]::Escape($Publisher)
        $manifest = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'packaging\AppxManifest.xml.in'))
        $manifest = $manifest.Replace('@@PACKAGE_NAME@@', $escapedName)
        $manifest = $manifest.Replace('@@PUBLISHER@@', $escapedPublisher)
        $manifest = $manifest.Replace('@@PACKAGE_VERSION@@', $resolvedPackageVersion)
        Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'AppxManifest.xml') -Content $manifest

        $isSigned = -not [string]::IsNullOrWhiteSpace($CertificatePath)
        $msixName = if ($isSigned) { 'EgressController-x64.msix' } else { 'EgressController-x64.unsigned.msix' }
        $msixPath = Join-Path $outputRoot $msixName
        $makeAppx = Find-WindowsSdkTool 'MakeAppx.exe'
        & $makeAppx pack /d $stagingDirectory /p $msixPath /o
        if ($LASTEXITCODE -ne 0) {
            throw 'MakeAppx failed.'
        }

        if ($isSigned) {
            $resolvedCertificatePath = [IO.Path]::GetFullPath($CertificatePath)
            if (-not (Test-Path -LiteralPath $resolvedCertificatePath -PathType Leaf)) {
                throw "Signing certificate was not found: $resolvedCertificatePath"
            }

            $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $resolvedCertificatePath,
                $CertificatePassword,
                $flags)
            try {
                if (-not [string]::Equals($certificate.Subject, $Publisher, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Certificate subject '$($certificate.Subject)' does not match Publisher '$Publisher'."
                }
            }
            finally {
                $certificate.Dispose()
            }

            $signTool = Find-WindowsSdkTool 'SignTool.exe'
            $signArguments = @('sign', '/fd', 'SHA256', '/f', $resolvedCertificatePath)
            if (-not [string]::IsNullOrEmpty($CertificatePassword)) {
                $signArguments += @('/p', $CertificatePassword)
            }
            if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
                $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
            }
            $signArguments += $msixPath
            & $signTool @signArguments
            if ($LASTEXITCODE -ne 0) {
                throw 'SignTool failed.'
            }

            $downloadBase = "https://github.com/$Repository/releases/latest/download"
            $appInstaller = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'packaging\EgressController.appinstaller.in'))
            $appInstaller = $appInstaller.Replace('@@PACKAGE_NAME@@', $escapedName)
            $appInstaller = $appInstaller.Replace('@@PUBLISHER@@', $escapedPublisher)
            $appInstaller = $appInstaller.Replace('@@PACKAGE_VERSION@@', $resolvedPackageVersion)
            $appInstaller = $appInstaller.Replace('@@APPINSTALLER_URI@@', "$downloadBase/EgressController.appinstaller")
            $appInstaller = $appInstaller.Replace('@@MSIX_URI@@', "$downloadBase/EgressController-x64.msix")
            Write-Utf8NoBom -Path (Join-Path $outputRoot 'EgressController.appinstaller') -Content $appInstaller
        }
    }
}
finally {
    Pop-Location
}

foreach ($temporaryDirectory in @($publishDirectory, $stagingDirectory)) {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

$releaseFiles = Get-ChildItem -LiteralPath $outputRoot -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name
$checksums = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
Write-Utf8NoBom -Path (Join-Path $outputRoot 'SHA256SUMS.txt') -Content (($checksums -join "`n") + "`n")

Write-Host "Artifacts written to $outputRoot"
Get-ChildItem -LiteralPath $outputRoot -File | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)"
}
