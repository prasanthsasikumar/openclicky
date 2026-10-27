// Kokoro's half of SpeechVoicesTests.swift: its speakers, the automatic download's voice decision,
// and the WAV the helper sends back.

using System.Buffers.Binary;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class KokoroVoiceTests
{
    [Fact]
    public void AnAutomaticDownloadMakesKokoroTheVoiceOnlyWhereNobodyPickedOne()
    {
        Assert.Equal("kokoro", KokoroVoice.VoiceAfterAutomaticDownload(null, "en"));
        Assert.Equal("kokoro", KokoroVoice.VoiceAfterAutomaticDownload("cloud", "en"));
        Assert.Equal("kokoro", KokoroVoice.VoiceAfterAutomaticDownload("automatic", "en"));
        Assert.Equal("openai", KokoroVoice.VoiceAfterAutomaticDownload("openai", "en"));
        Assert.Equal("mac", KokoroVoice.VoiceAfterAutomaticDownload("mac", "en"));
        Assert.Null(KokoroVoice.VoiceAfterAutomaticDownload(null, "ta"));
    }

    [Fact]
    public void KokoroSpeaksTheReplyLanguageWithThePickedSpeakerWhenItFits()
    {
        Assert.Equal("Heart", KokoroVoice.SpeakerFor("en", null)?.Name);
        Assert.Equal("George", KokoroVoice.SpeakerFor("en", 26)?.Name);
        // A British speaker picked for English doesn't carry over to Hindi.
        Assert.Equal("hi", KokoroVoice.SpeakerFor("hi", 26)?.LanguageCode);
        Assert.Null(KokoroVoice.SpeakerFor("ta", null));
        Assert.Equal("en-us", KokoroVoice.LanguageHint("en"));
        Assert.Equal("pt-br", KokoroVoice.LanguageHint("pt"));
        Assert.Equal("fr", KokoroVoice.LanguageHint("fr"));
    }

    [Fact]
    public void KokoroSamplesBecomeA16BitWav()
    {
        float[] samples = [0, 0.5f, -0.5f, 2];
        var wav = KokoroEngine.Wav(samples, 24_000);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(44 + samples.Length * 2, wav.Length);
        Assert.Equal(24_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        // Out-of-range samples are clipped, not wrapped.
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(wav.Length - 2)));
    }

    [Fact]
    public void TheModelIsPinnedAsOnTheMac()
    {
        Assert.Equal("https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2", KokoroVoice.ModelDownload.Url.ToString());
        Assert.Equal("60703707bc05ccd48d60e1eb723f38b07d6e474795a0b6a588d7220400e0f7a4", KokoroVoice.ModelDownload.Sha256);
        Assert.Equal(349_886_476, KokoroVoice.ModelDownload.Bytes);
        Assert.Equal("--openclicky-kokoro-helper", KokoroVoice.HelperFlag);
    }

    [Fact]
    public void AFolderWithoutTheModelFilesIsNotInstalled()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.False(KokoroVoice.IsInstalledIn(folder));
            foreach (var name in new[] { "model.onnx", "voices.bin", "tokens.txt" }) File.WriteAllText(Path.Combine(folder, name), "");
            Assert.False(KokoroVoice.IsInstalledIn(folder));
            Directory.CreateDirectory(Path.Combine(folder, "espeak-ng-data"));
            Assert.True(KokoroVoice.IsInstalledIn(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
