using System.Diagnostics;
using Talky.Interop;
using Talky.Services.Storage;
using Talky.Services.UI;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace Talky.Services.Audio;

public enum VoiceMode
{
    /// <summary>Mic → RNNoise → Opus → network; network → jitter buffer → speakers.</summary>
    Call,

    /// <summary>Mic → RNNoise → speakers, so users can hear what others will hear.</summary>
    MicTest,
}

/// <param name="Session">Identifies this start; pass it to <see cref="IVoiceEngine.Stop(int)"/>.</param>
public sealed record VoiceStartResult(bool Success, bool CaptureAvailable, string? Message, int Session = 0);

/// <summary>Receives one encoded 20 ms Opus frame. Invoked on the audio processing thread.</summary>
public delegate void EncodedVoiceHandler(ReadOnlySpan<byte> opus, bool speaking);

public interface IVoiceEngine
{
    bool IsRunning { get; }

    VoiceMode Mode { get; }

    bool NoiseSuppressionAvailable { get; }

    bool NoiseSuppressionEnabled { get; set; }

    bool IsMuted { get; set; }

    bool IsDeafened { get; set; }

    /// <summary>Playback gain, 0..2.</summary>
    float OutputVolume { get; set; }

    /// <summary>Microphone gain, 0..2.</summary>
    float InputGain { get; set; }

    /// <summary>Post-processing microphone level, 0..1 (for meters).</summary>
    float InputLevel { get; }

    bool IsLocalSpeaking { get; }

    bool IsRemoteSpeaking { get; }

    /// <summary>Raised (background thread) when the audio device failed and could not be recovered.</summary>
    event EventHandler<string>? DeviceFailed;

    Task<VoiceStartResult> StartAsync(VoiceMode mode, EncodedVoiceHandler? onEncoded);

    /// <summary>Stops whatever is running.</summary>
    void Stop();

    /// <summary>
    /// Stops only if <paramref name="session"/> is still the running session, so a late stop can never
    /// tear down a newer call or mic test.
    /// </summary>
    void Stop(int session);

    /// <summary>Feeds a received Opus packet. Thread-safe.</summary>
    void PushRemotePacket(uint sequence, ReadOnlySpan<byte> opus, bool speaking);
}

/// <summary>AudioGraph-based voice pipeline (WASAPI under the hood, 48 kHz mono, 10 ms quanta).</summary>
public sealed class VoiceEngine : IVoiceEngine, IDisposable
{
    private const int SampleRate = VoiceFormat.SampleRate;
    private static readonly long SpeakingHangover = Stopwatch.Frequency * 300 / 1000;

    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly FloatRingBuffer _captureRing = new(SampleRate);
    private readonly FloatRingBuffer _playbackRing = new(SampleRate);
    private readonly JitterBuffer _jitter = new();
    private readonly AutoResetEvent _captureSignal = new(false);
    private readonly OpusVoiceDecoder _decoder = new();

    private AudioGraph? _graph;
    private AudioDeviceInputNode? _micNode;
    private AudioFrameOutputNode? _captureNode;
    private AudioFrameInputNode? _playbackNode;
    private AudioDeviceOutputNode? _speakerNode;
    private Thread? _processingThread;
    private volatile bool _running;
    private volatile EncodedVoiceHandler? _onEncoded;
    private long _localSpeakingUntil;
    private long _remoteSpeakingUntil;
    private float _inputLevel;
    private int _generation;

    public VoiceEngine(ISettingsService settings, IUiDispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        NoiseSuppressionEnabled = settings.Current.NoiseSuppression;
        OutputVolume = (float)settings.Current.OutputVolume;
        InputGain = (float)settings.Current.InputGain;
    }

    public event EventHandler<string>? DeviceFailed;

    public bool IsRunning => _running;

    public VoiceMode Mode { get; private set; }

    public bool NoiseSuppressionAvailable => NoiseSuppressor.IsAvailable;

    public bool NoiseSuppressionEnabled { get; set; }

    public bool IsMuted { get; set; }

    public bool IsDeafened { get; set; }

    public float OutputVolume { get; set; }

    public float InputGain { get; set; }

    public float InputLevel => _running ? Volatile.Read(ref _inputLevel) : 0;

    public bool IsLocalSpeaking => _running && !IsMuted && Stopwatch.GetTimestamp() < Volatile.Read(ref _localSpeakingUntil);

    public bool IsRemoteSpeaking => _running && Stopwatch.GetTimestamp() < Volatile.Read(ref _remoteSpeakingUntil);

    public Task<VoiceStartResult> StartAsync(VoiceMode mode, EncodedVoiceHandler? onEncoded) =>
        StartInternalAsync(mode, onEncoded, reuseSession: null);

    private async Task<VoiceStartResult> StartInternalAsync(VoiceMode mode, EncodedVoiceHandler? onEncoded, int? reuseSession)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (reuseSession is { } expected && (expected != Volatile.Read(ref _generation) || !_running))
            {
                // Device recovery raced with a stop/restart: nothing to recover any more.
                return new VoiceStartResult(false, false, null);
            }

            StopCore();
            // Create the graph from the UI thread so a first-run microphone consent prompt can appear.
            return await _dispatcher.InvokeAsync(() => StartCoreAsync(mode, onEncoded, reuseSession)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Voice] Start failed: {ex}");
            StopCore();
            return new VoiceStartResult(false, false, $"Audio could not start: {ex.Message}");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Stop()
    {
        _lifecycle.Wait();
        try
        {
            StopCore();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Stop(int session)
    {
        _lifecycle.Wait();
        try
        {
            if (session != 0 && session == Volatile.Read(ref _generation))
            {
                StopCore();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose() => Stop();

    public void PushRemotePacket(uint sequence, ReadOnlySpan<byte> opus, bool speaking)
    {
        if (!_running || Mode != VoiceMode.Call)
        {
            return;
        }

        _jitter.Push(sequence, opus);
        if (speaking)
        {
            Volatile.Write(ref _remoteSpeakingUntil, Stopwatch.GetTimestamp() + SpeakingHangover);
        }
    }

    private async Task<VoiceStartResult> StartCoreAsync(VoiceMode mode, EncodedVoiceHandler? onEncoded, int? reuseSession)
    {
        var settings = _settings.Current;
        bool systemProcessing = settings.SystemVoiceProcessing;
        var format = MonoFloatFormat();

        var graph = await CreateGraphAsync(systemProcessing, settings.OutputDeviceId, format);
        if (graph is null)
        {
            return new VoiceStartResult(false, false, "No audio output device is available.");
        }

        var outputResult = await graph.CreateDeviceOutputNodeAsync();
        if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
        {
            graph.Dispose();
            return new VoiceStartResult(false, false, $"Could not open the speakers ({outputResult.Status}).");
        }

        _speakerNode = outputResult.DeviceOutputNode;
        _playbackNode = graph.CreateFrameInputNode(format);
        _playbackNode.AddOutgoingConnection(_speakerNode);
        _playbackNode.QuantumStarted += OnPlaybackQuantumStarted;

        string? warning = null;
        var category = systemProcessing ? MediaCategory.Communications : MediaCategory.Other;
        var inputDevice = await FindDeviceAsync(settings.InputDeviceId);
        var inputResult = inputDevice is null
            ? await graph.CreateDeviceInputNodeAsync(category, format)
            : await graph.CreateDeviceInputNodeAsync(category, format, inputDevice);
        if (inputResult.Status != AudioDeviceNodeCreationStatus.Success && inputDevice is not null)
        {
            // Selected mic disappeared: fall back to the default one.
            inputResult = await graph.CreateDeviceInputNodeAsync(category, format);
        }

        if (inputResult.Status == AudioDeviceNodeCreationStatus.Success)
        {
            _micNode = inputResult.DeviceInputNode;
            _captureNode = graph.CreateFrameOutputNode(format);
            _micNode.AddOutgoingConnection(_captureNode);
            graph.QuantumStarted += OnGraphQuantumStarted;
        }
        else
        {
            warning = inputResult.Status == AudioDeviceNodeCreationStatus.AccessDenied
                ? "Microphone access is blocked. Allow Talky under Windows Settings › Privacy & security › Microphone."
                : "No microphone is available — you can listen but not talk.";
        }

        graph.UnrecoverableErrorOccurred += OnUnrecoverableError;

        _captureRing.Clear();
        _playbackRing.Clear();
        _jitter.Reset();
        _decoder.Reset();
        Volatile.Write(ref _inputLevel, 0);
        Mode = mode;
        _onEncoded = onEncoded;
        _graph = graph;
        _running = true;
        int generation = reuseSession ?? Interlocked.Increment(ref _generation);
        Volatile.Write(ref _generation, generation);

        _processingThread = new Thread(() => ProcessingLoop(generation))
        {
            IsBackground = true,
            Name = "Talky voice processing",
            Priority = ThreadPriority.Highest,
        };
        _processingThread.Start();

        graph.Start();
        return new VoiceStartResult(true, _micNode is not null, warning, generation);
    }

    private static async Task<AudioGraph?> CreateGraphAsync(bool systemProcessing, string? outputDeviceId, AudioEncodingProperties format)
    {
        var category = systemProcessing ? AudioRenderCategory.Communications : AudioRenderCategory.GameChat;
        var outputDevice = await FindDeviceAsync(outputDeviceId);

        // Preferred: chosen device, 48 kHz mono graph. Then default device. Then whatever the system offers.
        var attempts = new (DeviceInformation? Device, AudioEncodingProperties? Encoding)[]
        {
            (outputDevice, format),
            (null, format),
            (null, null),
        };

        foreach (var (device, encoding) in attempts)
        {
            var graphSettings = new AudioGraphSettings(category)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                DesiredSamplesPerQuantum = SampleRate / 100,
            };

            if (device is not null)
            {
                graphSettings.PrimaryRenderDevice = device;
            }

            if (encoding is not null)
            {
                graphSettings.EncodingProperties = encoding;
            }

            var result = await AudioGraph.CreateAsync(graphSettings);
            if (result.Status == AudioGraphCreationStatus.Success)
            {
                return result.Graph;
            }

            Debug.WriteLine($"[Voice] AudioGraph creation failed: {result.Status} ({result.ExtendedError?.Message})");
        }

        return null;
    }

    private static AudioEncodingProperties MonoFloatFormat()
    {
        var format = AudioEncodingProperties.CreatePcm(SampleRate, 1, 32);
        format.Subtype = MediaEncodingSubtypes.Float;
        return format;
    }

    private static async Task<DeviceInformation?> FindDeviceAsync(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            var device = await DeviceInformation.CreateFromIdAsync(id);
            return device.IsEnabled ? device : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void StopCore()
    {
        if (_graph is null && !_running)
        {
            return;
        }

        _running = false;
        _onEncoded = null;
        _captureSignal.Set();
        _processingThread?.Join(500);
        _processingThread = null;

        var graph = _graph;
        _graph = null;
        if (graph is not null)
        {
            graph.QuantumStarted -= OnGraphQuantumStarted;
            graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
            try
            {
                graph.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Voice] Graph stop failed: {ex.Message}");
            }
        }

        if (_playbackNode is not null)
        {
            _playbackNode.QuantumStarted -= OnPlaybackQuantumStarted;
        }

        _micNode?.Dispose();
        _captureNode?.Dispose();
        _playbackNode?.Dispose();
        _speakerNode?.Dispose();
        graph?.Dispose();
        _micNode = null;
        _captureNode = null;
        _playbackNode = null;
        _speakerNode = null;
        Volatile.Write(ref _localSpeakingUntil, 0);
        Volatile.Write(ref _remoteSpeakingUntil, 0);
    }

    private void OnUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        Debug.WriteLine($"[Voice] Unrecoverable audio error: {args.Error}");
        var mode = Mode;
        var handler = _onEncoded;
        int generation = Volatile.Read(ref _generation);

        // Typically a device was unplugged or the default device changed: rebuild the graph.
        _ = Task.Run(async () =>
        {
            await Task.Delay(750).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation) || !_running)
            {
                return;
            }

            var result = await StartInternalAsync(mode, handler, reuseSession: generation).ConfigureAwait(false);
            if (!result.Success && result.Message is not null)
            {
                DeviceFailed?.Invoke(this, result.Message ?? "The audio device stopped working.");
            }
        });
    }

    // ------------------------------------------------------------------ audio thread callbacks

    private unsafe void OnGraphQuantumStarted(AudioGraph sender, object args)
    {
        var captureNode = _captureNode;
        if (captureNode is null)
        {
            return;
        }

        using var frame = captureNode.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        byte* data = BufferInterop.GetPointer(reference, out _);
        int samples = (int)(buffer.Length / sizeof(float));
        if (samples > 0)
        {
            _captureRing.Write(new ReadOnlySpan<float>(data, samples));
            _captureSignal.Set();
        }
    }

    private unsafe void OnPlaybackQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        int required = args.RequiredSamples;
        if (required <= 0)
        {
            return;
        }

        var frame = new AudioFrame((uint)(required * sizeof(float)));
        using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            byte* data = BufferInterop.GetPointer(reference, out _);
            FillPlayback(new Span<float>(data, required));
        }

        sender.AddFrame(frame);
    }

    private void FillPlayback(Span<float> destination)
    {
        if (Mode == VoiceMode.Call)
        {
            while (_playbackRing.Count < destination.Length)
            {
                DecodeNextFrame();
            }
        }

        int read = _playbackRing.Read(destination);
        destination[read..].Clear();

        float volume = IsDeafened ? 0f : OutputVolume;
        if (volume != 1f)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = Math.Clamp(destination[i] * volume, -1f, 1f);
            }
        }
    }

    private void DecodeNextFrame()
    {
        Span<float> pcm = stackalloc float[VoiceFormat.FrameSamples];
        int decoded;
        try
        {
            decoded = _jitter.Next(out var packet, out var fecSource) switch
            {
                JitterBuffer.Result.Packet => _decoder.Decode(packet, pcm),
                JitterBuffer.Result.Lost when fecSource is not null => _decoder.DecodeFec(fecSource, pcm),
                JitterBuffer.Result.Lost => _decoder.Conceal(pcm),
                _ => 0,
            };
        }
        catch (Exception)
        {
            decoded = 0; // corrupt packet: play silence for this frame
        }

        if (decoded <= 0)
        {
            pcm.Clear();
            decoded = VoiceFormat.FrameSamples;
        }

        _playbackRing.Write(pcm[..decoded]);
    }

    // ------------------------------------------------------------------ processing thread

    private void ProcessingLoop(int generation)
    {
        var frame = new float[NoiseSuppressor.FrameSize];
        var pending = new float[VoiceFormat.FrameSamples];
        var opus = new byte[VoicePacketBudget];
        int pendingCount = 0;
        bool pendingSpeaking = false;
        var encoder = new OpusVoiceEncoder();
        NoiseSuppressor? suppressor = null;

        try
        {
            while (_running && generation == Volatile.Read(ref _generation))
            {
                _captureSignal.WaitOne(50);

                while (_captureRing.Count >= frame.Length && _running)
                {
                    _captureRing.Read(frame);

                    float gain = InputGain;
                    if (gain != 1f)
                    {
                        for (int i = 0; i < frame.Length; i++)
                        {
                            frame[i] = Math.Clamp(frame[i] * gain, -1f, 1f);
                        }
                    }

                    float voiceProbability = -1;
                    if (NoiseSuppressionEnabled && NoiseSuppressor.IsAvailable)
                    {
                        suppressor ??= NoiseSuppressor.TryCreate();
                        if (suppressor is not null)
                        {
                            voiceProbability = suppressor.Process(frame);
                        }
                    }

                    float rms = Rms(frame);
                    UpdateLevel(rms);
                    // RNNoise's VAD can reach ~0.65 on loud broadband noise, so require more than that.
                    bool speaking = voiceProbability >= 0
                        ? voiceProbability > 0.75f && rms > 0.004f
                        : rms > 0.012f;
                    if (speaking)
                    {
                        Volatile.Write(ref _localSpeakingUntil, Stopwatch.GetTimestamp() + SpeakingHangover);
                    }

                    if (Mode == VoiceMode.MicTest)
                    {
                        if (!IsMuted)
                        {
                            _playbackRing.Write(frame);
                        }

                        continue;
                    }

                    frame.CopyTo(pending, pendingCount);
                    pendingCount += frame.Length;
                    pendingSpeaking |= IsLocalSpeaking;
                    if (pendingCount < pending.Length)
                    {
                        continue;
                    }

                    pendingCount = 0;
                    var handler = _onEncoded;
                    if (handler is not null && !IsMuted)
                    {
                        int length = encoder.Encode(pending, opus);
                        if (length > 0)
                        {
                            handler(opus.AsSpan(0, length), pendingSpeaking);
                        }
                    }

                    pendingSpeaking = false;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Voice] Processing loop crashed: {ex}");
        }
        finally
        {
            suppressor?.Dispose();
        }
    }

    private const int VoicePacketBudget = VoiceFormat.MaxOpusPacket;

    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    private void UpdateLevel(float rms)
    {
        // Map -60..0 dBFS to 0..1 with fast attack / slow release.
        float db = rms > 1e-6f ? 20f * MathF.Log10(rms) : -100f;
        float target = Math.Clamp((db + 60f) / 60f, 0f, 1f);
        float current = Volatile.Read(ref _inputLevel);
        float next = target > current ? target : current + (target - current) * 0.15f;
        Volatile.Write(ref _inputLevel, next);
    }
}
