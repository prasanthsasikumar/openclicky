// Ports BuddyAudioConversionSupport.swift: the audio helpers the transcription providers share —
// captured audio as 16-bit mono PCM at the rate a provider wants, a WAV file around PCM16, and the
// silence gate the upload engines check before they send a take.

using System.Buffers.Binary;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

/// <summary>
/// Captured audio → PCM16 mono little-endian at <c>targetSampleRate</c> (AVAudioConverter on the Mac).
/// One converter per session: it resamples continuously across frames (linear interpolation, the
/// position carried from one frame to the next), so the joins between frames do not click. Not
/// thread-safe; a session calls it from the capture thread only.
/// </summary>
public sealed class BuddyPCM16AudioConverter(int targetSampleRate)
{
    public int TargetSampleRate { get; } = targetSampleRate;

    private int currentInputSampleRate;
    /// <summary>Position of the next output sample, in input samples, relative to the start of the next frame (can be negative: between the previous frame's last sample and this frame's first).</summary>
    private double nextInputPosition;
    private float previousFrameLastSample;
    private bool hasPreviousSample;

    /// <summary>The frame as PCM16 bytes at the target rate; null when nothing came out.</summary>
    public byte[]? ConvertToPcm16(AudioFrame frame)
    {
        if (frame.SampleRate == TargetSampleRate && frame.Pcm16 is { Length: > 0 } pcm16)
        {
            if (frame.Samples.Length > 0)
            {
                previousFrameLastSample = frame.Samples[^1];
                hasPreviousSample = true;
            }
            return pcm16;
        }
        var samples = ConvertSamples(frame.Samples, frame.SampleRate);
        return samples.Length == 0 ? null : FloatToPcm16(samples);
    }

    /// <summary>The frame's float samples resampled to the target rate.</summary>
    public float[] ConvertSamples(float[] input, int inputSampleRate)
    {
        if (input.Length == 0 || inputSampleRate <= 0) return [];
        if (inputSampleRate != currentInputSampleRate)
        {
            currentInputSampleRate = inputSampleRate;
            nextInputPosition = 0;
            hasPreviousSample = false;
        }
        if (inputSampleRate == TargetSampleRate)
        {
            previousFrameLastSample = input[^1];
            hasPreviousSample = true;
            return (float[])input.Clone();
        }
        var step = inputSampleRate / (double)TargetSampleRate;
        var output = new List<float>((int)(input.Length / step) + 2);
        var position = nextInputPosition;
        while (position <= input.Length - 1)
        {
            float value;
            if (position < 0)
            {
                // Between the previous frame's last sample (at -1) and this frame's first.
                var fraction = (float)(position + 1);
                var before = hasPreviousSample ? previousFrameLastSample : input[0];
                value = before + (input[0] - before) * fraction;
            }
            else
            {
                var index = (int)position;
                var fraction = (float)(position - index);
                var next = index + 1 < input.Length ? input[index + 1] : input[index];
                value = input[index] + (next - input[index]) * fraction;
            }
            output.Add(value);
            position += step;
        }
        nextInputPosition = position - input.Length;
        previousFrameLastSample = input[^1];
        hasPreviousSample = true;
        return output.ToArray();
    }

    /// <summary>Float samples in [-1, 1] → PCM16 little-endian, clipped (never wrapped).</summary>
    public static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var clamped = Math.Clamp(samples[index], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), (short)(clamped * short.MaxValue));
        }
        return bytes;
    }

    /// <summary>PCM16 little-endian → float samples (÷ 32768).</summary>
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(index * 2, 2)) / 32768f;
        return samples;
    }
}

public static class BuddyWAVFileBuilder
{
    /// <summary>A canonical 44-byte-header WAV around PCM data.</summary>
    public static byte[] BuildWavData(ReadOnlySpan<byte> pcm16AudioData, int sampleRate, int channelCount = 1, int bitsPerSample = 16)
    {
        var byteRate = sampleRate * channelCount * bitsPerSample / 8;
        var blockAlign = channelCount * bitsPerSample / 8;
        var dataChunkSize = pcm16AudioData.Length;
        var wav = new byte[44 + dataChunkSize];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataChunkSize));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataChunkSize);
        pcm16AudioData.CopyTo(span[44..]);
        return wav;
    }
}

/// <summary>
/// The silence check the upload engines share before they send a take. A take whose loudest
/// sample never rises above the room's noise floor is not uploaded: the models answer silence with
/// their own prompt or a made-up phrase, and a microphone held by another engine records only zeros.
/// </summary>
public static class PCM16SilenceGate
{
    /// <summary>Loudest sample magnitude (0…32767) below which the whole take counts as silence.</summary>
    public const short SilentTakePeakMagnitude = 400;

    /// <summary>The loudest sample magnitude in 16-bit mono PCM (short.MinValue counts as short.MaxValue).</summary>
    public static short PeakMagnitude(ReadOnlySpan<byte> pcm16)
    {
        short loudestMagnitude = 0;
        for (var offset = 0; offset + 1 < pcm16.Length; offset += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(offset, 2));
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > loudestMagnitude) loudestMagnitude = magnitude;
        }
        return loudestMagnitude;
    }

    /// <summary>Whether a take whose loudest sample was <paramref name="peakMagnitude"/> should be treated as silence.</summary>
    public static bool IsSilent(short peakMagnitude) => peakMagnitude < SilentTakePeakMagnitude;
}
