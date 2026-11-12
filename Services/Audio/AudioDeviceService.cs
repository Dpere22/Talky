using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Talky.Services.Audio;

public sealed record AudioDevice(string? Id, string Name)
{
    public override string ToString() => Name;
}

public interface IAudioDeviceService
{
    Task<IReadOnlyList<AudioDevice>> GetInputDevicesAsync();

    Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync();
}

public sealed class AudioDeviceService : IAudioDeviceService
{
    public Task<IReadOnlyList<AudioDevice>> GetInputDevicesAsync() => FindAsync(MediaDevice.GetAudioCaptureSelector());

    public Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync() => FindAsync(MediaDevice.GetAudioRenderSelector());

    private static async Task<IReadOnlyList<AudioDevice>> FindAsync(string selector)
    {
        var list = new List<AudioDevice> { new(null, "Windows default") };
        try
        {
            var devices = await DeviceInformation.FindAllAsync(selector);
            list.AddRange(devices.Where(d => d.IsEnabled).Select(d => new AudioDevice(d.Id, d.Name)).OrderBy(d => d.Name));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Device enumeration failed: {ex.Message}");
        }

        return list;
    }
}
