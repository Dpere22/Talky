<#
.SYNOPSIS
    Builds an unsigned, portable Talky (zip of a self-contained folder) for sharing with friends.

.DESCRIPTION
    No installer and no certificate: friends extract the zip anywhere and run Talky.exe.
    The build is a trimmed Release publish with .NET and the Windows App Runtime bundled, so
    nothing else needs to be installed. Data lives in %LOCALAPPDATA%\Talky.

    To update, replace the folder contents (keep the same folder path so Windows Firewall keeps
    its rule). Windows SmartScreen may warn on first run of a downloaded zip:
    "More info" > "Run anyway".

    Output: .\AppPackages\Talky_<version>_<arch>_portable.zip

.EXAMPLE
    .\build-portable.ps1
.EXAMPLE
    .\build-portable.ps1 -Arch arm64
#>
param(
    [ValidateSet('x64', 'arm64', 'x86')]
    [string] $Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Talky.csproj'
[xml] $csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$outputDir = Join-Path $PSScriptRoot 'AppPackages'
$publishDir = Join-Path $outputDir "Talky_${version}_${Arch}_portable"
$zip = "$publishDir.zip"

# Separate bin/obj folders so this unpackaged build never mixes with MSIX / F5 build state.
$platform = if ($Arch -eq 'arm64') { 'ARM64' } else { $Arch }
dotnet publish $project -c Release -r "win-$Arch" `
    -p:Platform=$platform `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:SelfContained=true `
    -p:EnableWinAppRunSupport=false `
    -p:IntermediateOutputPath="obj\portable\$Arch\" `
    -p:OutputPath="bin\portable\$Arch\" `
    -p:PublishDir="$publishDir\"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host "Built $zip"
Write-Host "Friends: extract it anywhere and run Talky.exe (allow it through the firewall on private networks)."
