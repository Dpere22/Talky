# Talky

Peer-to-peer text chat, voice calls and screen sharing for your local network — a Discord-style
experience that is completely Windows-native and needs no servers, accounts or internet.

- **Friends are addresses.** Add someone by their LAN IP (or PC name), optionally with a port, and give them a nickname.
- **1:1 chats** with delivery receipts, typing indicators and offline queueing (messages go out when your friend comes back online).
- **Voice calls from the chat**, Discord DM style: mute, deafen, per-call volume, speaking indicators.
- **Noise suppression** with [RNNoise](https://github.com/xiph/rnnoise) (open source), on by default and toggleable in-call.
- **High-resolution screen sharing** of any window or monitor, at native resolution by default — on a LAN there's bandwidth to spare. Full-screen viewing for the receiver.
- Fluent design: Mica, light/dark/system theme, Windows notifications for messages and incoming calls.

## Getting started

Talky runs on **Windows 11, version 24H2 or later** (x64 or ARM64; noise suppression is x64 only).

1. Open `Talky.slnx` in Visual Studio (Windows App SDK workload) and run, or `dotnet build -p:Platform=x64`.
2. On first launch Windows Firewall asks whether Talky may communicate — allow **private networks**.
3. Your address (e.g. `192.168.1.20`) is shown on the welcome screen and in the sidebar — click it to copy.
4. Click **Add friend**, enter their address and a nickname. They appear online as soon as their Talky is running.
   If someone adds you first, they show up in your list with an "Add to friends" prompt.

Talky uses port **47820** (TCP for chat/screen sharing, UDP for voice). You can change it in Settings; a friend
on a non-default port is added as `address:port`.

## Installing

Download **`Talky-win-x64-Setup.exe`** (or `-arm64` for ARM PCs) from the
[latest release](https://github.com/Dpere22/Talky/releases/latest) and run it. Talky installs just for you (no admin),
adds Start menu and desktop shortcuts and starts. If the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) isn't installed yet, Setup installs it first.

- Talky isn't code-signed yet, so Windows SmartScreen may warn: choose **More info → Run anyway**.
- Allow Talky through Windows Firewall on **private networks** when asked.
- Talky updates itself from GitHub Releases: when an update is ready you'll see **Restart now** (or it installs the next time you open Talky).
- Uninstall from **Settings → Apps**. Your chats and friends stay in `%APPDATA%\Talky`.

## Releasing

1. Bump `<Version>` in `Talky.csproj` and `Identity Version` in `Package.appxmanifest`. Optionally add `release-notes/v<version>.md`.
2. Commit, push, then `git tag v<version>` and `git push origin v<version>`.

The [Release workflow](.github/workflows/release.yml) builds both architectures with
[Velopack](https://velopack.io) and publishes the GitHub release. To build locally: `.\build-release.ps1`
(output in `AppPackages\Releases`). `-Publish` uploads the release from your own machine instead of CI.

For a signed MSIX instead, see `build-msix.ps1` (needs a signing certificate, because Windows won't
sideload an unsigned MSIX that contains an app).

## How it works

See [CLAUDE.md](CLAUDE.md) for the architecture, wire protocol and conventions.

| Concern | Implementation |
| --- | --- |
| UI | WinUI 3 / Windows App SDK, MVVM (CommunityToolkit.Mvvm), DI |
| Transport | One TCP connection per friend (framed, prioritised), UDP for voice |
| Audio | Windows AudioGraph (WASAPI) → RNNoise → Opus (Concentus) → jitter buffer |
| Screen | Windows.Graphics.Capture → change-detected tiles → WIC JPEG (4:4:4) |
| Storage | JSON in `%APPDATA%\Talky` (installed) or the package data folder (MSIX) |
| Install / updates | Velopack Setup + delta updates from GitHub Releases; shared .NET 10 runtime, Windows App SDK bundled (only the parts Talky uses) |

## Open-source components

RNNoise (Xiph.Org, BSD-3-Clause) · Concentus/Opus (BSD-3-Clause) · Windows App SDK, WinUI, Windows Community Toolkit (MIT) · Velopack (MIT).
