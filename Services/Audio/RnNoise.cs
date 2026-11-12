using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Talky.Services.Audio;

/// <summary>
/// P/Invoke bindings for Xiph's RNNoise (BSD-3-Clause), a small recurrent-network noise suppressor.
/// The native rnnoise.dll ships in the YellowDogMan.RRNoise.NET package (x64/x86).
/// </summary>
internal static partial class RnNoiseNative
{
    private const string Library = "rnnoise";

    [LibraryImport(Library, EntryPoint = "rnnoise_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint Create(nint model);

    [LibraryImport(Library, EntryPoint = "rnnoise_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void Destroy(nint state);

    [LibraryImport(Library, EntryPoint = "rnnoise_get_frame_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetFrameSize();

    /// <summary>Denoises one 480-sample (10 ms @ 48 kHz) frame. Returns the voice-activity probability.</summary>
    [LibraryImport(Library, EntryPoint = "rnnoise_process_frame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial float ProcessFrame(nint state, float* output, float* input);
}

/// <summary>One RNNoise denoiser state. Not thread-safe; use from the audio processing thread only.</summary>
public sealed class NoiseSuppressor : IDisposable
{
    public const int FrameSize = 480;

    private static readonly Lazy<bool> s_available = new(Probe);

    private nint _state;

    private NoiseSuppressor(nint state) => _state = state;

    /// <summary>False on platforms without the native library (e.g. ARM64).</summary>
    public static bool IsAvailable => s_available.Value;

    public static NoiseSuppressor? TryCreate()
    {
        if (!IsAvailable)
        {
            return null;
        }

        var state = RnNoiseNative.Create(0);
        return state == 0 ? null : new NoiseSuppressor(state);
    }

    /// <summary>
    /// Denoises <paramref name="frame"/> in place (samples in [-1, 1]) and returns the voice probability (0..1).
    /// </summary>
    public unsafe float Process(Span<float> frame)
    {
        if (frame.Length != FrameSize || _state == 0)
        {
            throw new ArgumentException($"RNNoise needs exactly {FrameSize} samples.", nameof(frame));
        }

        // RNNoise works on 16-bit PCM magnitudes.
        Span<float> scaled = stackalloc float[FrameSize];
        for (int i = 0; i < FrameSize; i++)
        {
            scaled[i] = frame[i] * 32768f;
        }

        float vad;
        fixed (float* p = scaled)
        {
            vad = RnNoiseNative.ProcessFrame(_state, p, p);
        }

        const float inverse = 1f / 32768f;
        for (int i = 0; i < FrameSize; i++)
        {
            frame[i] = scaled[i] * inverse;
        }

        return vad;
    }

    public void Dispose()
    {
        var state = Interlocked.Exchange(ref _state, 0);
        if (state != 0)
        {
            RnNoiseNative.Destroy(state);
        }
    }

    private static bool Probe()
    {
        try
        {
            if (RnNoiseNative.GetFrameSize() != FrameSize)
            {
                return false;
            }

            var state = RnNoiseNative.Create(0);
            if (state == 0)
            {
                return false;
            }

            RnNoiseNative.Destroy(state);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Debug.WriteLine($"[RNNoise] Unavailable: {ex.Message}");
            return false;
        }
    }
}
