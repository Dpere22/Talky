using Concentus;
using Concentus.Enums;

namespace Talky.Services.Audio;

public static class VoiceFormat
{
    public const int SampleRate = 48000;

    /// <summary>20 ms Opus frames.</summary>
    public const int FrameSamples = 960;

    public const int MaxOpusPacket = 1275;
}

/// <summary>Mono 48 kHz Opus encoder tuned for LAN: high bitrate, in-band FEC for Wi-Fi hiccups.</summary>
public sealed class OpusVoiceEncoder
{
    private readonly IOpusEncoder _encoder;

    public OpusVoiceEncoder(int bitrate = 96_000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(VoiceFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.Complexity = 8;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.UseVBR = true;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;
        _encoder.UseDTX = false;
    }

    public int Encode(ReadOnlySpan<float> pcm, Span<byte> output) =>
        _encoder.Encode(pcm, VoiceFormat.FrameSamples, output, output.Length);
}

public sealed class OpusVoiceDecoder
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(VoiceFormat.SampleRate, 1);

    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm) =>
        _decoder.Decode(packet, pcm, VoiceFormat.FrameSamples, false);

    /// <summary>Recovers a lost frame from the in-band FEC data carried by the following packet.</summary>
    public int DecodeFec(ReadOnlySpan<byte> nextPacket, Span<float> pcm) =>
        _decoder.Decode(nextPacket, pcm, VoiceFormat.FrameSamples, true);

    /// <summary>Packet-loss concealment for a frame with no data at all.</summary>
    public int Conceal(Span<float> pcm) =>
        _decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, VoiceFormat.FrameSamples, false);

    public void Reset() => _decoder.ResetState();
}
