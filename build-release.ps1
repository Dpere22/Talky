<#
.SYNOPSIS
    Builds Talky's installer and update packages with Velopack, and optionally publishes them to GitHub Releases.

.DESCRIPTION
    Per architecture (channels win-x64 / win-arm64):
      1. dotnet publish: framework-dependent .NET (the shared .NET 10 Desktop Runtime, which Setup installs when
         it's missing) with the Windows App SDK bundled (only the components Talky references).
      2. Downloads the latest published release of that channel, so vpk can build a small delta package.
      3. vpk pack -> AppPackages\Releases:
           Talky-win-<arch>-Setup.exe          one-click per-user installer (no admin unless .NET must be installed)
           Talky-win-<arch>-Portable.zip       self-updating portable copy (needs .NET 10 installed)
           Talky-<ver>-win-<arch>-full/delta.nupkg, releases.win-<arch>.json, assets.win-<arch>.json  (update feed)
      4. With -Publish: uploads everything to the GitHub release v<version>. Both architectures share one
         release; installed copies find their channel's updates there automatically.

    The version comes from <Version> in Talky.csproj. Bump it (and Identity Version in Package.appxmanifest)
    before every release. Normally releases are published by CI (.github/workflows/release.yml) when a
    v<version> tag is pushed; -Publish is for doing the same by hand.

.EXAMPLE
    .\build-release.ps1                      # both architectures, local only
.EXAMPLE
    .\build-release.ps1 -Arch x64
.EXAMPLE
    .\build-release.ps1 -Publish -ReleaseNotes .\notes.md     # token from $env:GITHUB_TOKEN or `gh auth token`
#>
param(
    [ValidateSet('x64', 'arm64')]
    [string[]] $Arch = @('x64', 'arm64'),

    # Upload to GitHub Releases and publish the release (friends' copies update from it).
    [switch] $Publish,

    # Markdown file shown as the release notes.
    [string] $ReleaseNotes,

    [string] $Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'

$repoUrl = 'https://github.com/Dpere22/Talky'
$packId = 'Talky'
$root = $PSScriptRoot
$project = Join-Path $root 'Talky.csproj'
$releasesDir = Join-Path $root 'AppPackages\Releases'

[xml] $csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) {
    throw 'No <Version> in Talky.csproj'
}

function Invoke-Native {
    param([string] $What, [scriptblock] $Command)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE"
    }
}

if (-not $Token) {
    # Optional for building (only avoids GitHub API rate limits), required for -Publish.
    $Token = (gh auth token 2>$null)
}
if ($Publish -and -not $Token) {
    throw 'Publishing needs a GitHub token: set GITHUB_TOKEN or sign in with `gh auth login`.'
}
if ($Publish -and -not $env:CI -and -not (git branch -r --contains HEAD)) {
    throw 'Push this commit first: the release tag is created on it on GitHub.'
}
if ($ReleaseNotes) {
    $ReleaseNotes = (Resolve-Path $ReleaseNotes).Path
}

Push-Location $root
try {
    Invoke-Native 'dotnet tool restore' { dotnet tool restore }
    New-Item -ItemType Directory -Force $releasesDir | Out-Null

    foreach ($a in $Arch) {
        $rid = "win-$a"
        $channel = $rid
        $platform = if ($a -eq 'arm64') { 'ARM64' } else { 'x64' }
        $publishDir = Join-Path $root "bin\release\$a\publish"

        Write-Host "`n=== Talky $version ($rid) ===" -ForegroundColor Cyan
        if (Test-Path $publishDir) {
            Remove-Item $publishDir -Recurse -Force
        }

        # Separate bin/obj folders so this unpackaged build never mixes with MSIX / F5 build state.
        Invoke-Native 'dotnet publish' {
            dotnet publish $project -c Release -r $rid `
                -p:Platform=$platform `
                -p:WindowsPackageType=None `
                -p:WindowsAppSDKSelfContained=true `
                -p:SelfContained=false `
                -p:PublishTrimmed=false `
                -p:EnableWinAppRunSupport=false `
                -p:IntermediateOutputPath="obj\release\$a\" `
                -p:OutputPath="bin\release\$a\" `
                -p:PublishDir="$publishDir"
        }

        # Start the channel from what's published (GitHub is the source of truth): leftovers from an earlier local
        # build of the same version would make vpk refuse to pack.
        Get-ChildItem $releasesDir -File |
            Where-Object { $_.Name -like "*-$channel-*" -or $_.Name -like "*.$channel.json" -or $_.Name -eq "RELEASES-$channel" } |
            Remove-Item -Force

        # The previous release lets vpk create a delta package; there is none before the first release.
        $downloadArgs = @('download', 'github', '--repoUrl', $repoUrl, '--channel', $channel, '--outputDir', $releasesDir)
        if ($Token) {
            $downloadArgs += @('--token', $Token)
        }
        dotnet vpk @downloadArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "No previous $channel release downloaded; this release will have no delta package."
        }

        $packArgs = @(
            'pack',
            '--packId', $packId,
            '--packVersion', $version,
            '--packTitle', 'Talky',
            '--packAuthors', 'Dpere22',
            '--packDir', $publishDir,
            '--mainExe', 'Talky.exe',
            '--icon', (Join-Path $root 'Assets\AppIcon.ico'),
            '--channel', $channel,
            '--runtime', $rid,
            # Shared .NET runtime: Setup installs the .NET 10 Desktop Runtime for this architecture if it's missing.
            '--framework', "net10.0-$a-desktop",
            '--outputDir', $releasesDir
        )
        if ($ReleaseNotes) {
            $packArgs += @('--releaseNotes', $ReleaseNotes)
        }
        Invoke-Native 'vpk pack' { dotnet vpk @packArgs }

        if ($Publish) {
            Invoke-Native 'vpk upload' {
                dotnet vpk upload github `
                    --repoUrl $repoUrl `
                    --token $Token `
                    --channel $channel `
                    --outputDir $releasesDir `
                    --tag "v$version" `
                    --releaseName "Talky $version" `
                    --targetCommitish (git rev-parse HEAD) `
                    --merge `
                    --publish
            }
        }
    }
}
finally {
    Pop-Location
}

Write-Host "`nPackages are in $releasesDir"
if ($Publish) {
    Write-Host "Published $repoUrl/releases/tag/v$version"
}
else {
    Write-Host 'Friends install with Talky-win-x64-Setup.exe (or -arm64). Run with -Publish to release it.'
}
