namespace Talky.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum ScreenShareResolution
{
    Source,
    P1440,
    P1080,
    P720,
}

public enum ScreenShareQuality
{
    /// <summary>Near-lossless 4:4:4 JPEG tiles. Best on wired gigabit LAN.</summary>
    Maximum,

    /// <summary>Lower quality / 4:2:0 for Wi-Fi.</summary>
    Balanced,
}

public sealed class AppSettings
{
    /// <summary>Stable identity of this installation, sent in the Hello handshake.</summary>
    public Guid PeerId { get; set; } = Guid.NewGuid();

    public string DisplayName { get; set; } = Environment.UserName;

    public int Port { get; set; } = NetworkDefaults.Port;

    public string? InputDeviceId { get; set; }

    public string? OutputDeviceId { get; set; }

    /// <summary>RNNoise noise suppression; on by default.</summary>
    public bool NoiseSuppression { get; set; } = true;

    /// <summary>
    /// Use Windows' communications signal processing (echo cancellation, AGC). Off by default because
    /// it also makes Windows duck other apps' audio during calls.
    /// </summary>
    public bool SystemVoiceProcessing { get; set; }

    public double OutputVolume { get; set; } = 1.0;

    public double InputGain { get; set; } = 1.0;

    public ScreenShareResolution ShareResolution { get; set; } = ScreenShareResolution.Source;

    public int ShareFrameRate { get; set; } = 60;

    public ScreenShareQuality ShareQuality { get; set; } = ScreenShareQuality.Maximum;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool ShowNotifications { get; set; } = true;
}
