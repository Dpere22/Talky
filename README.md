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

## Sharing it with friends

```powershell
.\build-portable.ps1          # -> AppPackages\Talky_<version>_x64_portable.zip
```

Send the zip. Friends extract it anywhere and run `Talky.exe` — no installer, certificate or runtime needed.
If Windows SmartScreen warns on first run, choose **More info → Run anyway**, and allow Talky through the
firewall on private networks. To update, replace the files in the same folder; chats and friends are kept
(they live in `%LOCALAPPDATA%\Talky`).

For a signed, installable MSIX instead, see `build-msix.ps1` (needs a signing certificate — Windows won't
sideload an unsigned MSIX that contains an app).

## How it works

See [CLAUDE.md](CLAUDE.md) for the architecture, wire protocol and conventions.

| Concern | Implementation |
| --- | --- |
| UI | WinUI 3 / Windows App SDK, MVVM (CommunityToolkit.Mvvm), DI |
| Transport | One TCP connection per friend (framed, prioritised), UDP for voice |
| Audio | Windows AudioGraph (WASAPI) → RNNoise → Opus (Concentus) → jitter buffer |
| Screen | Windows.Graphics.Capture → change-detected tiles → WIC JPEG (4:4:4) |
| Storage | JSON in the app's local data folder |

## Open-source components

RNNoise (Xiph.Org, BSD-3-Clause) · Concentus/Opus (BSD-3-Clause) · Windows App SDK, WinUI, Windows Community Toolkit (MIT).
