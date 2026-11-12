<#
.SYNOPSIS
    Builds a self-contained, signed Talky MSIX. (For unsigned sharing use build-portable.ps1.)

.DESCRIPTION
    Uses the winapp CLI that ships with the Microsoft.Windows.SDK.BuildTools.WinApp NuGet package.
    The package is a Release build (trimmed, ReadyToRun) with the Windows App Runtime bundled, so the
    PCs you install it on don't need anything else. Output goes to .\AppPackages.

    Windows will not sideload an unsigned MSIX that contains an app executable (0x80073D2B), so pass
    -CertPath for anything you install. Without it the package is only useful for Store submission
    (the Store signs it). Create a test certificate once with:
        winapp cert generate --manifest .\Package.appxmanifest --password <pw> --export-cer
    PCs that install the package must trust the .cer once (admin):
        Import-Certificate -FilePath .\devcert.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
    Bump Version in Package.appxmanifest before each build you hand out so it installs as an update.

.EXAMPLE
    .\build-msix.ps1
.EXAMPLE
    .\build-msix.ps1 -Arch x64, arm64
.EXAMPLE
    .\build-msix.ps1 -CertPath .\devcert.pfx -CertPassword 'your-password'
#>
param(
    [ValidateSet('x64', 'arm64', 'x86')]
    [string[]] $Arch = @('x64'),

    # Optional .pfx to sign with. Its subject must equal the Publisher in Package.appxmanifest.
    [string] $CertPath,

    [string] $CertPassword
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Talky.csproj'

# Locate winapp.exe from the WinApp build-tools package version the project references.
[xml] $csproj = Get-Content $project
$winAppVersion = ($csproj.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Microsoft.Windows.SDK.BuildTools.WinApp' }).Version
$winapp = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools.winapp\$winAppVersion\tools\win-x64\winapp.exe"
if (-not (Test-Path $winapp)) {
    dotnet restore $project -p:Platform=x64 | Out-Null
}
if (-not (Test-Path $winapp)) {
    throw "winapp.exe not found at $winapp"
}

[xml] $manifest = Get-Content (Join-Path $PSScriptRoot 'Package.appxmanifest')
$version = $manifest.Package.Identity.Version
$outputDir = Join-Path $PSScriptRoot 'AppPackages'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$fileName = if ($Arch.Count -gt 1) { "Talky_${version}_$($Arch -join '_').msixbundle" } else { "Talky_${version}_$($Arch[0]).msix" }
$output = Join-Path $outputDir $fileName

$winappArgs = @('package', $project, '-c', 'Release', '--self-contained', '--output', $output)
foreach ($a in $Arch) {
    $winappArgs += @('--arch', $a)
}

if ($CertPath) {
    $winappArgs += @('--cert', (Resolve-Path $CertPath).Path)
    if ($CertPassword) {
        $winappArgs += @('--cert-password', $CertPassword)
    }
}
else {
    Write-Warning "No -CertPath: building an UNSIGNED package. Windows can't sideload it; it's only for Store submission."
    $winappArgs += '--no-sign'
}

& $winapp @winappArgs
if ($LASTEXITCODE -ne 0) {
    throw "winapp package failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Built $output"
if ($CertPath) {
    Write-Host "Install it by double-clicking, or:  Add-AppxPackage `"$output`""
}
