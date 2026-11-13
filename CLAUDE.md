# Talky — project guide for Claude

Windows-native, LAN-only, peer-to-peer Discord-style app: 1:1 text chat, voice calls (started from the
chat), and high-resolution screen sharing. No servers or accounts — a friend is an IP/hostname + port
with a nickname. Group chats are intentionally out of scope for now.

## Stack
- **Windows 11 24H2 (build 26100) or later, x64 and ARM64 only** — no Windows 10, no x86. The floor is set in three
  places that must agree: `TargetPlatformMinVersion` (csproj), `MinVersion` (manifest), `App.MinimumWindowsBuild`
  (startup check, because unpackaged builds ignore the manifest). Use 26100 APIs directly, no `ApiInformation` checks.
- WinUI 3 / Windows App SDK 2.6, .NET 10, single-project MSIX (`EnableMsixTooling`), C# 14.
- MVVM with CommunityToolkit.Mvvm (**partial properties**, `[RelayCommand]`), Microsoft.Extensions.DependencyInjection.
- CommunityToolkit.WinUI.Controls.SettingsControls (SettingsCard/SettingsExpander) for the settings page.
- Concentus (managed Opus), RNNoise native `rnnoise.dll` from `YellowDogMan.RRNoise.NET` (`IncludeAssets="native"`; x64 only — ARM64 reports noise suppression unavailable).
- Package versions are pinned in `Talky.csproj` (the template used floating `*`).
- **Windows App SDK components, not the metapackage**: `Foundation` + `WinUI` + `Runtime` (all 2.6.5, keep identical). The Community Toolkit pulls in `Microsoft.WindowsAppSDK`, so it and the unused components (AI, ML, `Microsoft.Windows.AI.MachineLearning`, Search, Widgets, DWrite) are referenced with `ExcludeAssets="all"` — ~50 MB of ONNX Runtime/DirectML etc. otherwise. Using a new Windows App SDK API? Check which component ships it before removing an exclusion.
- Velopack (`Velopack` package + `vpk` local tool in `dotnet-tools.json`, same version) for install/updates.

## Layout
- `Program.cs` — `Main` (XAML-generated Main disabled via `DISABLE_XAML_GENERATED_MAIN`): `VelopackApp.Build().Run()` must run first (install/update/uninstall hooks, applies a downloaded update), then `XamlGeneratedProgram.XamlGeneratedMain()`.
- `App.xaml.cs` — composition root (`ConfigureServices`), startup order matters: load settings/friends → start network/chat/call services → create window → notifications.
- `Models/` — POCOs persisted as JSON (`Friend`, `ChatMessage`, `AppSettings`) + `CallInfo` record snapshot.
- `Protocol/` — wire formats: `PacketType`, JSON payload records + source-generated `ProtocolJsonContext`, UDP `VoicePacket`.
- `Services/` (UI-agnostic; raise events on background threads):
  - `Storage/` — `AppPaths` (packaged LocalFolder or `%APPDATA%\Talky`, `--profile`; never `%LOCALAPPDATA%\Talky`, that's the Velopack install folder, which Setup/uninstall wipe — 1.0 data there is migrated once), settings, friends, `MessageStore` (one JSON file per conversation, debounced writes).
  - `Networking/` — `PeerNetworkService` (TCP listener + reconnect loop, one `PeerConnection` per friend), `VoiceTransport` (UDP), `NetworkInfo`.
  - `Chat/ChatService` — store-and-forward with acks/dedupe; pending messages resend on reconnect.
  - `Calls/CallService` — call state machine (invite/accept/decline/end/state, glare + busy handling, ring timeout), wires voice + screen share.
  - `Audio/` — `VoiceEngine` (AudioGraph, 48 kHz mono, 10 ms quanta; processing thread does gain → RNNoise → VAD → Opus), `JitterBuffer`, `OpusCodec`, `RnNoise` (LibraryImport, cdecl), `SoundEffects` (synthesized WAVs).
  - `ScreenShare/` — `ScreenCaptureSession` (Windows.Graphics.Capture + picker), `TileCodec` (tile-delta JPEG via WIC), `ScreenShareService` (sender loop + `IRemoteScreen` receiver).
  - `Updates/UpdateService` — Velopack `UpdateManager` + `GithubSource` (check 15 s after start, then every 6 h; download in background; `PrepareRestart` = `WaitExitThenApplyUpdates` then close the window normally). Unsupported (no-op) unless installed by Velopack. `TALKY_UPDATE_SOURCE=<folder of vpk output>` tests an update locally.
  - `UI/` — dispatcher, window (theme, full screen, flash), navigation (enum targets → pages), dialogs, app notifications.
- `ViewModels/` — `ShellViewModel` (friends list, selection→navigation), `ConversationViewModel` (one per friend, cached), `CallViewModel` (singleton; also drives the sidebar mute/deafen), `SettingsViewModel` (transient), `UpdatesViewModel` (singleton; shell "Update ready" banner, hidden during calls, + Settings card), `FriendEditorViewModel`.
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
- `KeyboardAccelerators` on a container (e.g. `ShellPage`'s Escape) show an auto-generated key-hint tooltip over
  every child without its own tooltip — set `KeyboardAcceleratorPlacementMode="Hidden"` on that container.

## Build / run / test
- Build: `dotnet build -p:Platform=x64` (also ARM64). Run packaged with F5 in VS or `dotnet run -p:Platform=x64` (WinApp tooling registers a debug identity).
- Two instances on one PC: pass `--profile <name>` (or `TALKY_PROFILE`) for separate data, give each a different port in Settings (or pre-seed `profiles/<name>/settings.json`), and add each other as `127.0.0.1:<port>`.
  An unpackaged build works well for this: `bin\release\x64\publish\Talky.exe` from `.\build-release.ps1 -Arch x64`, or `dotnet build -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:EnableWinAppRunSupport=false` (do that in a copy of the repo to keep `obj/` clean). Unpackaged runs register a toast AUMID under `HKCU\Software\Classes\AppUserModelId`.
- Windows Firewall prompts the first time Talky listens; friends can't reach you until it's allowed on private networks.

## Distribution
- **GitHub Releases + Velopack (current choice): `.\build-release.ps1 [-Arch x64,arm64] [-Publish] [-ReleaseNotes notes.md]`**, normally run by CI (`.github/workflows/release.yml`) on a pushed `v<Version>` tag (the tag must equal csproj `<Version>`; notes from `release-notes/v<ver>.md` if present).
  - Publish is **framework-dependent .NET** (`SelfContained=false`, so no trimming; `PublishTrimmed=false` is passed) with the **Windows App SDK self-contained** (`WindowsAppSDKSelfContained=true`). `vpk pack --framework net10.0-<arch>-desktop` makes Setup install the .NET 10 Desktop Runtime when missing (that step may need admin). Builds into `bin\release` / `obj\release`.
  - Channels `win-x64` / `win-arm64` in one release `v<ver>`; the installed app updates from its own channel. Before packing, the script deletes that channel's local files in `AppPackages\Releases` and `vpk download`s the latest published release, so deltas are built against GitHub (vpk refuses to repack an existing version).
  - Outputs per arch: `Talky-win-<arch>-Setup.exe` (per-user install to `%LOCALAPPDATA%\Talky\current`, no admin, Desktop + Start menu shortcuts, AUMID `velopack.Talky`), `-Portable.zip`, full/delta `.nupkg`, `releases.win-<arch>.json`.
  - x64 1.0.0 (2026-11-12): publish ~100 MB, full package/Setup ~38/45 MB; a code-only update delta is KBs to a few MB. ReadyToRun is on except for the big WinRT projections (`PublishReadyToRunExclude`, they'd grow +12 MB).
  - Unsigned for now: SmartScreen warns on Setup.exe (More info → Run anyway), and Smart App Control blocks it outright. Signing later: `vpk pack --azureTrustedSignFile` or `--signParams`.
  - Verified on this PC: install, launch, update 1.0.0→1.0.1 via the banner, uninstall (removes the toast AUMID, keeps `%APPDATA%\Talky`). CI workflow not yet run.
- **Unsigned MSIX does not work for this app**: `Add-AppxPackage -AllowUnsigned` (publisher with `OID.2.25.311729368913984317654407730594956997722=1`) rejects any package with an app executable — `0x80073D2B "an unsigned package cannot include Executable activations"` (verified, even without the COM server extension). Don't retry this route.
- **Signed MSIX (future): `.\build-msix.ps1 -CertPath .\devcert.pfx -CertPassword <pw> [-Arch x64,arm64]`**, which wraps the `winapp.exe` bundled in the `Microsoft.Windows.SDK.BuildTools.WinApp` NuGet package (`winapp package … --self-contained`, i.e. `dotnet publish`: trimmed + ReadyToRun, ~63 MB with the runtime bundled).
  - Test cert once: `winapp cert generate --manifest .\Package.appxmanifest --password <pw> --export-cer` (subject must equal manifest `Publisher`, currently `CN=dpuu2`). Installing PCs trust the `.cer` once: `Import-Certificate -FilePath devcert.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople` (admin).
  - Bump `Identity Version` in `Package.appxmanifest` (keep csproj `<Version>` in sync) for each release, or installs over an older version fail.
  - A dev-mode registration (F5 / `dotnet run`) with the same identity blocks installing the MSIX on that PC; removing it deletes that install's local data.
- `Assets\AppIcon.ico` is loaded at runtime (title bar), so it's `CopyToOutputDirectory` — unpackaged builds need it next to the exe. `vpk pack --icon` uses it for Setup/shortcuts too.
