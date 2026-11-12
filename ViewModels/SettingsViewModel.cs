using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Talky.Models;
using Talky.Services.Audio;
using Talky.Services.Calls;
using Talky.Services.Networking;
using Talky.Services.Storage;
using Talky.Services.UI;
using Windows.ApplicationModel.DataTransfer;

namespace Talky.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IAudioDeviceService _devices;
    private readonly IVoiceEngine _engine;
    private readonly ICallService _call;
    private readonly IPeerNetwork _network;
    private readonly IWindowService _window;
    private readonly DispatcherQueueTimer _meterTimer;
    private bool _loading = true;
    private int _micTestSession;

    public SettingsViewModel(
        ISettingsService settings,
        IAudioDeviceService devices,
        IVoiceEngine engine,
        ICallService call,
        IPeerNetwork network,
        IWindowService window)
    {
        _settings = settings;
        _devices = devices;
        _engine = engine;
        _call = call;
        _network = network;
        _window = window;

        var s = settings.Current;
        DisplayName = s.DisplayName;
        Port = s.Port;
        NoiseSuppression = s.NoiseSuppression;
        SystemVoiceProcessing = s.SystemVoiceProcessing;
        OutputVolumePercent = Math.Round(s.OutputVolume * 100);
        InputGainPercent = Math.Round(s.InputGain * 100);
        ResolutionIndex = (int)s.ShareResolution;
        FrameRateIndex = Array.IndexOf(FrameRates, s.ShareFrameRate) is var i and >= 0 ? i : 0;
        QualityIndex = (int)s.ShareQuality;
        ThemeIndex = (int)s.Theme;
        ShowNotifications = s.ShowNotifications;
        LocalAddresses = NetworkInfo.GetLocalIPv4Addresses()
            .Select(a => network.ListenPort == NetworkDefaults.Port ? a : $"{a}:{network.ListenPort}")
            .ToArray();

        _meterTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _meterTimer.Interval = TimeSpan.FromMilliseconds(50);
        _meterTimer.Tick += (_, _) => UpdateMeter();
        _loading = false;
    }

    public static int[] FrameRates { get; } = [60, 30, 15];

    public ObservableCollection<AudioDevice> InputDevices { get; } = [];

    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];

    public IReadOnlyList<string> LocalAddresses { get; }

    public string LocalAddressesText => LocalAddresses.Count == 0 ? "Not connected to a network" : string.Join("   ·   ", LocalAddresses);

    public bool NoiseSuppressionAvailable => _engine.NoiseSuppressionAvailable;

    public string NoiseSuppressionDescription => NoiseSuppressionAvailable
        ? "RNNoise removes keyboard clicks, fans and background hum from your mic."
        : "RNNoise isn't available for this processor architecture.";

    public bool IsListening => _network.IsListening;

    public string? ListenError => _network.ListenError;

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortNeedsRestart))]
    public partial double Port { get; set; }

    public bool PortNeedsRestart => (int)Port != _network.ListenPort;

    [ObservableProperty]
    public partial AudioDevice? SelectedInputDevice { get; set; }

    [ObservableProperty]
    public partial AudioDevice? SelectedOutputDevice { get; set; }

    [ObservableProperty]
    public partial bool NoiseSuppression { get; set; }

    [ObservableProperty]
    public partial bool SystemVoiceProcessing { get; set; }

    [ObservableProperty]
    public partial double OutputVolumePercent { get; set; }

    [ObservableProperty]
    public partial double InputGainPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicTestButtonText))]
    public partial bool IsMicTesting { get; private set; }

    [ObservableProperty]
    public partial double MicLevel { get; private set; }

    [ObservableProperty]
    public partial string? MicTestMessage { get; private set; }

    public string MicTestButtonText => IsMicTesting ? "Stop testing" : "Let's check";

    [ObservableProperty]
    public partial int ResolutionIndex { get; set; }

    [ObservableProperty]
    public partial int FrameRateIndex { get; set; }

    [ObservableProperty]
    public partial int QualityIndex { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowNotifications { get; set; }

    public async Task LoadDevicesAsync()
    {
        var inputs = await _devices.GetInputDevicesAsync();
        var outputs = await _devices.GetOutputDevicesAsync();

        _loading = true;
        try
        {
            InputDevices.Clear();
            foreach (var device in inputs)
            {
                InputDevices.Add(device);
            }

            OutputDevices.Clear();
            foreach (var device in outputs)
            {
                OutputDevices.Add(device);
            }

            SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Id == _settings.Current.InputDeviceId) ?? InputDevices.FirstOrDefault();
            SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == _settings.Current.OutputDeviceId) ?? OutputDevices.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleMicTestAsync()
    {
        if (IsMicTesting)
        {
            StopMicTest();
            return;
        }

        await StartMicTestAsync();
    }

    private async Task StartMicTestAsync()
    {
        if (_call.Current is not null)
        {
            MicTestMessage = "Finish your call to run a mic test.";
            return;
        }

        _meterTimer.Stop();
        _engine.NoiseSuppressionEnabled = NoiseSuppression;
        _engine.InputGain = (float)(InputGainPercent / 100);
        _engine.IsMuted = false;
        _engine.IsDeafened = false;
        var result = await _engine.StartAsync(VoiceMode.MicTest, null);
        _micTestSession = result.Session;
        MicTestMessage = result.Message;
        IsMicTesting = result.Success && result.CaptureAvailable;
        if (IsMicTesting)
        {
            _meterTimer.Start();
        }
        else if (result.Success)
        {
            // Audio started but there is no mic to test.
            StopEngine();
        }
    }

    private void StopEngine()
    {
        int session = _micTestSession;
        _micTestSession = 0;
        if (session != 0)
        {
            _ = Task.Run(() => _engine.Stop(session));
        }
    }

    [RelayCommand]
    private void CopyAddresses()
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, LocalAddresses));
        Clipboard.SetContent(package);
    }

    public void Dispose()
    {
        StopMicTest();
    }

    partial void OnDisplayNameChanged(string value)
    {
        var name = value.Trim();
        if (!_loading && name.Length > 0)
        {
            _settings.Update(s => s.DisplayName = name.Length > 64 ? name[..64] : name);
        }
    }

    partial void OnPortChanged(double value)
    {
        if (!_loading && !double.IsNaN(value) && value is >= 1024 and <= 65535)
        {
            _settings.Update(s => s.Port = (int)value);
        }
    }

    partial void OnSelectedInputDeviceChanged(AudioDevice? value)
    {
        if (!_loading && value is not null)
        {
            _settings.Update(s => s.InputDeviceId = value.Id);
            _ = RestartMicTestAsync();
        }
    }

    partial void OnSelectedOutputDeviceChanged(AudioDevice? value)
    {
        if (!_loading && value is not null)
        {
            _settings.Update(s => s.OutputDeviceId = value.Id);
            _ = RestartMicTestAsync();
        }
    }

    partial void OnNoiseSuppressionChanged(bool value)
    {
        if (!_loading)
        {
            // Goes through the call service so an ongoing call picks it up immediately.
            _call.NoiseSuppressionEnabled = value;
        }
    }

    partial void OnSystemVoiceProcessingChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.SystemVoiceProcessing = value);
            _ = RestartMicTestAsync();
        }
    }

    partial void OnOutputVolumePercentChanged(double value)
    {
        if (!_loading)
        {
            _call.OutputVolume = value / 100;
        }
    }

    partial void OnInputGainPercentChanged(double value)
    {
        if (!_loading)
        {
            _engine.InputGain = (float)(value / 100);
            _settings.Update(s => s.InputGain = value / 100);
        }
    }

    partial void OnResolutionIndexChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            _settings.Update(s => s.ShareResolution = (ScreenShareResolution)value);
        }
    }

    partial void OnFrameRateIndexChanged(int value)
    {
        if (!_loading && value >= 0 && value < FrameRates.Length)
        {
            _settings.Update(s => s.ShareFrameRate = FrameRates[value]);
        }
    }

    partial void OnQualityIndexChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            _settings.Update(s => s.ShareQuality = (ScreenShareQuality)value);
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            var theme = (AppTheme)value;
            _settings.Update(s => s.Theme = theme);
            _window.ApplyTheme(theme);
        }
    }

    partial void OnShowNotificationsChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.ShowNotifications = value);
        }
    }

    private async Task RestartMicTestAsync()
    {
        if (IsMicTesting)
        {
            // StartAsync replaces the running session, so no explicit stop (which could race) is needed.
            await StartMicTestAsync();
        }
    }

    private void StopMicTest()
    {
        _meterTimer.Stop();
        MicLevel = 0;
        IsMicTesting = false;
        StopEngine();
    }

    private void UpdateMeter()
    {
        if (_engine.Mode != VoiceMode.MicTest || !_engine.IsRunning)
        {
            // A call took over the audio engine.
            _micTestSession = 0;
            StopMicTest();
            return;
        }

        MicLevel = _engine.InputLevel * 100;
    }
}
