# Talky — project guide for Claude

Windows-native, LAN-only, peer-to-peer Discord-style app: 1:1 text chat, voice calls (started from the
chat), and high-resolution screen sharing. No servers or accounts — a friend is an IP/hostname + port
with a nickname. Group chats are intentionally out of scope for now.

## Stack
- **Windows 11 24H2 (build 26100) or later, x64 and ARM64 only** — no Windows 10, no x86. The floor is set in three
  places that must agree: `TargetPlatformMinVersion` (csproj), `MinVersion` (manifest), `App.MinimumWindowsBuild`
  (startup check, because the unpackaged portable build ignores the manifest). Use 26100 APIs directly, no `ApiInformation` checks.
- WinUI 3 / Windows App SDK 2.6, .NET 10, single-project MSIX (`EnableMsixTooling`), C# 14.
- MVVM with CommunityToolkit.Mvvm (**partial properties**, `[RelayCommand]`), Microsoft.Extensions.DependencyInjection.
- CommunityToolkit.WinUI.Controls.SettingsControls (SettingsCard/SettingsExpander) for the settings page.
- Concentus (managed Opus), RNNoise native `rnnoise.dll` from `YellowDogMan.RRNoise.NET` (`IncludeAssets="native"`; x64 only — ARM64 reports noise suppression unavailable).
- Package versions are pinned in `Talky.csproj` (the template used floating `*`).

## Layout
- `App.xaml.cs` — composition root (`ConfigureServices`), startup order matters: load settings/friends → start network/chat/call services → create window → notifications.
- `Models/` — POCOs persisted as JSON (`Friend`, `ChatMessage`, `AppSettings`) + `CallInfo` record snapshot.
- `Protocol/` — wire formats: `PacketType`, JSON payload records + source-generated `ProtocolJsonContext`, UDP `VoicePacket`.
- `Services/` (UI-agnostic; raise events on background threads):
  - `Storage/` — `AppPaths` (packaged LocalFolder or `%LOCALAPPDATA%\Talky`, `--profile`), settings, friends, `MessageStore` (one JSON file per conversation, debounced writes).
  - `Networking/` — `PeerNetworkService` (TCP listener + reconnect loop, one `PeerConnection` per friend), `VoiceTransport` (UDP), `NetworkInfo`.
  - `Chat/ChatService` — store-and-forward with acks/dedupe; pending messages resend on reconnect.
  - `Calls/CallService` — call state machine (invite/accept/decline/end/state, glare + busy handling, ring timeout), wires voice + screen share.
  - `Audio/` — `VoiceEngine` (AudioGraph, 48 kHz mono, 10 ms quanta; processing thread does gain → RNNoise → VAD → Opus), `JitterBuffer`, `OpusCodec`, `RnNoise` (LibraryImport, cdecl), `SoundEffects` (synthesized WAVs).
  - `ScreenShare/` — `ScreenCaptureSession` (Windows.Graphics.Capture + picker), `TileCodec` (tile-delta JPEG via WIC), `ScreenShareService` (sender loop + `IRemoteScreen` receiver).
  - `UI/` — dispatcher, window (theme, full screen, flash), navigation (enum targets → pages), dialogs, app notifications.
- `ViewModels/` — `ShellViewModel` (friends list, selection→navigation), `ConversationViewModel` (one per friend, cached), `CallViewModel` (singleton; also drives the sidebar mute/deafen), `SettingsViewModel` (transient), `FriendEditorViewModel`.
- `Views/` — `ShellPage` (sidebar + content frame + incoming-call card + theater overlay), `ChatPage` (header, `CallPanel`, messages, composer), `SettingsPage`, `WelcomePage`; `Views/Controls/` for `AvatarView`, `CallPanel`, `RemoteScreenView`, `SlashedGlyph`, `TintedButton`, `FriendEditorDialog`, `MessageTemplateSelector`.
- `Interop/` — raw-vtable COM helpers (`IMemoryBufferByteAccess`, `IBufferByteAccess`) and D3D11 device creation. No `[ComImport]` (trim-safe).

## Networking protocol (keep both peers compatible; bump `NetworkDefaults.ProtocolVersion` on breaking changes)
- Default port **47820**: TCP for control/chat/screen frames, UDP (same number) for voice.
- TCP frame: `u32 LE length | u8 PacketType | payload`. Both sides send `Hello` first. Ping every 5 s, 20 s receive timeout.
- Both peers dial each other; duplicate connections are resolved by keeping the one opened by the **smaller PeerId** (identical decision on both sides — don't change one side only).
- Inbound peers match friends by PeerId, then by address (only friends without a known PeerId); unknown peers become `IsPending` friends.
- Writer has two queues: control packets always jump ahead of bulk screen frames. Packet handlers run inline on the read loop (payload memory is pooled and only valid during the call) — this is also the back-pressure for screen sharing.
- Voice UDP: `'T' 0x01 | token u32 (from CallId) | seq u32 | flags | opus`. 20 ms frames, 96 kbps, in-band FEC; 60 ms jitter target.
- Screen frames: 128 px change grid, horizontal runs merged into strips, parallel WIC JPEG (4:4:4 at "Maximum"), motion frames at lower quality then refined once static. Sender awaits each write (never more than one frame in flight).
- Capture uses `DirtyRegionMode.ReportOnly` (dirty rects accumulated across dropped frames → `CapturedFrame.Changed`): only tiles touching them are compared and only those areas rescaled. Reports can lag one frame (GDI windows), so the encoder only trusts its own previous-frame copy, and a full pass runs every 2 s and once when the screen goes quiet (`RefreshLastFrame`). `MinUpdateInterval` (1/fps − 1.5 ms; exactly 1/fps undershoots) paces capture at the source. None of this changes the wire format.

## Conventions
- Services never touch XAML; view models marshal service events with `IUiDispatcher.Post`.
- `[ObservableProperty] public partial T Prop { get; set; }` — no initializers on partial properties; initialize in the constructor.
- Prefer x:Bind + static functions in `Helpers/Ui.cs` over converters (implicit bool→Visibility is fine).
- Never block the UI thread on audio teardown: `VoiceEngine.StartAsync` returns a `Session`; stop with `Stop(session)` (fire-and-forget) so a late stop can't kill a newer call/mic test.

## WinUI gotchas hit in this repo
- WinUI 3 `Style` has no `Resources` → lightweight styling must be on the element (see `TintedButton`).
- `CaptionTextBlockStyle` sets `TextWrapping="Wrap"`; add `TextWrapping="NoWrap"` when using `TextTrimming`.
- In UserControls whose roots are DependencyProperties (e.g. `CallPanel.Call`), use `Mode=OneWay` even for commands — OneTime can capture null.
- ListView items are announced via `ToString()` — keep the overrides in `FriendViewModel`/`MessageViewModel`.
- `Buffer` is ambiguous when `Windows.Storage.Streams` is imported — use `System.Buffer`.
- `XamlRoot` is null in the `Window` constructor; use `GetDpiForWindow` for initial sizing.

## Build / run / test
- Build: `dotnet build -p:Platform=x64` (also ARM64). Run packaged with F5 in VS or `dotnet run -p:Platform=x64` (WinApp tooling registers a debug identity).
- Two instances on one PC: pass `--profile <name>` (or `TALKY_PROFILE`) for separate data, give each a different port in Settings (or pre-seed `profiles/<name>/settings.json`), and add each other as `127.0.0.1:<port>`.
  An unpackaged self-contained build works well for this: `dotnet build -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:EnableWinAppRunSupport=false` (do it in a copy of the repo to keep `obj/` clean). Unpackaged runs register a toast AUMID under `HKCU\Software\Classes\AppUserModelId`.
- Windows Firewall prompts the first time Talky listens; friends can't reach you until it's allowed on private networks.

## Distribution
- **Unsigned, for friends (current choice): `.\build-portable.ps1 [-Arch x64|arm64]`** → `AppPackages\Talky_<ver>_<arch>_portable.zip`. Unpackaged (`WindowsPackageType=None`), self-contained .NET + Windows App Runtime, trimmed Release; builds into `bin\portable` / `obj\portable` so it never mixes with MSIX/F5 state. Friends extract and run `Talky.exe`; data in `%LOCALAPPDATA%\Talky`.
- **Unsigned MSIX does not work for this app**: `Add-AppxPackage -AllowUnsigned` (publisher with `OID.2.25.311729368913984317654407730594956997722=1`) rejects any package with an app executable — `0x80073D2B "an unsigned package cannot include Executable activations"` (verified, even without the COM server extension). Don't retry this route.
- **Signed MSIX (future): `.\build-msix.ps1 -CertPath .\devcert.pfx -CertPassword <pw> [-Arch x64,arm64]`**, which wraps the `winapp.exe` bundled in the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package (`winapp package … --self-contained`, i.e. `dotnet publish`: trimmed + ReadyToRun, ~63 MB with the runtime bundled).
  - Test cert once: `winapp cert generate --manifest .\Package.appxmanifest --password <pw> --export-cer` (subject must equal manifest `Publisher`, currently `CN=dpuu2`). Installing PCs trust the `.cer` once: `Import-Certificate -FilePath devcert.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople` (admin).
  - Bump `Identity Version` in `Package.appxmanifest` (keep csproj `<Version>` in sync) for each release, or installs over an older version fail.
  - A dev-mode registration (F5 / `dotnet run`) with the same identity blocks installing the MSIX on that PC; removing it deletes that install's local data.
- `Assets\AppIcon.ico` is loaded at runtime (title bar), so it's `CopyToOutputDirectory` — portable builds need it next to the exe.
