// The Parakeet helper's request and reply (ParakeetEngine.Request/Reply in ParakeetEngine.swift).
// The engine itself runs in the helper process (OpenClicky.Windows/Speech/SherpaHelperMain.cs).

using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace OpenClicky.Core.Speech;

/// <param name="Pcm16Base64">16 kHz mono 16-bit little-endian PCM, base64. Empty: only load the model.</param>
public sealed record ParakeetEngineRequest(
    [property: JsonPropertyName("pcm16Base64")] string Pcm16Base64,
    [property: JsonPropertyName("modelFolder")] string ModelFolder);

public sealed record ParakeetEngineReply([property: JsonPropertyName("text")] string Text);

public static class ParakeetEngine
{
    public const int SampleRate = 16_000;

    /// <summary>PCM16 little-endian → float samples (÷ 32768).</summary>
    public static float[] FloatSamples(ReadOnlySpan<byte> pcm16)
    {
        var samples = new float[pcm16.Length / 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(index * 2, 2)) / 32768f;
        return samples;
    }
}

public sealed class ParakeetException(string message) : Exception(message)
{
    public const string NotInstalledMessage = "the offline speech model isn't downloaded yet.";
    public const string ModelNotLoadedMessage = "the offline speech model didn't load.";
    public const string UnreadableAudioMessage = "the audio couldn't be read.";
}
