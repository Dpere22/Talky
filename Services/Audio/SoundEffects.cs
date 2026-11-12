using System.Buffers.Binary;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Talky.Services.Audio;

public interface ISoundEffects
{
    void StartRingtone();

    void StartRingback();

    void StopRinging();

    void PlayJoin();

    void PlayLeave();

    void PlayMessage();
}

/// <summary>Small synthesized UI sounds, so no audio assets need to ship with the app.</summary>
public sealed class SoundEffects : ISoundEffects, IDisposable
{
    private const int Rate = 48000;

    private readonly MediaPlayer _loop = new() { IsLoopingEnabled = true, AudioCategory = MediaPlayerAudioCategory.Alerts, Volume = 0.8 };
    private readonly MediaPlayer _oneShot = new() { AudioCategory = MediaPlayerAudioCategory.SoundEffects, Volume = 0.6 };
    private readonly Lazy<byte[]> _ringtone = new(BuildRingtone);
    private readonly Lazy<byte[]> _ringback = new(BuildRingback);
    private readonly Lazy<byte[]> _join = new(() => Render(0.45, (659.25, 0.0, 0.22), (987.77, 0.11, 0.3)));
    private readonly Lazy<byte[]> _leave = new(() => Render(0.45, (987.77, 0.0, 0.22), (659.25, 0.11, 0.3)));
    private readonly Lazy<byte[]> _message = new(() => Render(0.25, (1318.5, 0.0, 0.18), (1760.0, 0.05, 0.16)));

    public void StartRingtone() => PlayLoop(_ringtone.Value);

    public void StartRingback() => PlayLoop(_ringback.Value);

    public void StopRinging()
    {
        try
        {
            _loop.Pause();
            _loop.Source = null;
        }
        catch (Exception)
        {
        }
    }

    public void PlayJoin() => PlayOnce(_join.Value);

    public void PlayLeave() => PlayOnce(_leave.Value);

    public void PlayMessage() => PlayOnce(_message.Value);

    public void Dispose()
    {
        _loop.Dispose();
        _oneShot.Dispose();
    }

    private void PlayLoop(byte[] wav)
    {
        try
        {
            _loop.Source = MediaSource.CreateFromStream(new MemoryStream(wav).AsRandomAccessStream(), "audio/wav");
            _loop.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Sound] {ex.Message}");
        }
    }

    private void PlayOnce(byte[] wav)
    {
        try
        {
            _oneShot.Source = MediaSource.CreateFromStream(new MemoryStream(wav).AsRandomAccessStream(), "audio/wav");
            _oneShot.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Sound] {ex.Message}");
        }
    }

    /// <summary>Bright four-note arpeggio, repeating every 2 s.</summary>
    private static byte[] BuildRingtone() =>
        Render(2.0, (659.25, 0.0, 0.5), (830.61, 0.13, 0.5), (987.77, 0.26, 0.5), (1318.51, 0.39, 0.7));

    /// <summary>Soft two-note pulse while waiting for the other side, every 2.4 s.</summary>
    private static byte[] BuildRingback() =>
        Render(2.4, (440.0, 0.0, 0.45), (554.37, 0.0, 0.45), (440.0, 0.5, 0.45), (554.37, 0.5, 0.45));

    /// <summary>Renders bell-like notes (frequency, start, decay seconds) into a 16-bit mono WAV.</summary>
    private static byte[] Render(double lengthSeconds, params (double Frequency, double Start, double Decay)[] notes)
    {
        int samples = (int)(lengthSeconds * Rate);
        var mix = new float[samples];
        foreach (var (frequency, start, decay) in notes)
        {
            int offset = (int)(start * Rate);
            int count = Math.Min(samples - offset, (int)(decay * 4 * Rate));
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / Rate;
                double attack = Math.Min(1.0, t / 0.004);
                double envelope = attack * Math.Exp(-t / decay * 3);
                double tone = Math.Sin(2 * Math.PI * frequency * t) + 0.25 * Math.Sin(4 * Math.PI * frequency * t);
                mix[offset + i] += (float)(tone * envelope * 0.22);
            }
        }

        var wav = new byte[44 + samples * 2];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + samples * 2);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], Rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples * 2);
        for (int i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)(Math.Clamp(mix[i], -1f, 1f) * short.MaxValue));
        }

        return wav;
    }
}
