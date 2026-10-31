// Ports SpeechVoicesTests.swift: what the picker offers, which voice Automatic and a missing key end
// up with, each vendor's request, Amazon's request signing against AWS's own test vector, and
// Kokoro's speakers. No network. (Kokoro's WAV and automatic-download rules are in the Kokoro tests.)

using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class SpeechVoicesTests
{
    private static OpenClickyShellSettings Settings(Action<OpenClickyShellSettings>? edit = null)
    {
        var settings = new OpenClickyShellSettings();
        edit?.Invoke(settings);
        return settings;
    }

    private static JsonObject Body(HttpRequestMessage request) =>
        JsonNode.Parse(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult())!.AsObject();

    [Fact]
    public void OlderStoredValuesMeanAutomatic()
    {
        Assert.Equal(SpeechVoiceChoice.Automatic, SpeechVoiceChoices.FromStored(null));
        Assert.Equal(SpeechVoiceChoice.Automatic, SpeechVoiceChoices.FromStored("cloud"));
        Assert.Equal(SpeechVoiceChoice.Mac, SpeechVoiceChoices.FromStored("mac"));
        Assert.Equal(SpeechVoiceChoice.Kokoro, SpeechVoiceChoices.FromStored("kokoro"));
        Assert.Null(SpeechVoiceChoice.Automatic.StoredValue());
        Assert.Equal("polly", SpeechVoiceChoice.Polly.StoredValue());
        Assert.Equal("openai", SpeechVoiceChoice.OpenAI.StoredValue());
        Assert.Equal("This PC", SpeechVoiceChoice.Mac.Title());
    }

    [Fact]
    public void AutomaticPrefersOwnKeysThenTheFreeVoiceThenOfflineVoices()
    {
        static SpeechVoiceChoice Resolve(OpenClickyShellSettings shell, bool account = false, bool kokoro = false) =>
            SpeechVoiceChoices.Resolve(shell, account, kokoro, "en");
        Assert.Equal(SpeechVoiceChoice.Mac, Resolve(Settings()));
        Assert.Equal(SpeechVoiceChoice.Kokoro, Resolve(Settings(), kokoro: true));
        // The free voice comes before a downloaded Kokoro: an ElevenLabs grant pays for it now (#23).
        Assert.Equal(SpeechVoiceChoice.Account, Resolve(Settings(), account: true, kokoro: true));
        Assert.Equal(SpeechVoiceChoice.Account, Resolve(Settings(), account: true));
        Assert.Equal(SpeechVoiceChoice.Sarvam, Resolve(Settings(s => s.SarvamKey = "s"), account: true));
        Assert.Equal(SpeechVoiceChoice.Sarvam, Resolve(Settings(s => { s.GeminiApiKey = "g"; s.SarvamKey = "s"; })));
        Assert.Equal(SpeechVoiceChoice.Gemini, Resolve(Settings(s => s.GeminiApiKey = "g"), account: true));
        Assert.Equal(SpeechVoiceChoice.ElevenLabs, Resolve(Settings(s => { s.ElevenLabsApiKey = "e"; s.SarvamKey = "s"; })));
        Assert.Equal(SpeechVoiceChoice.OpenAI, Resolve(Settings(s => { s.OpenaiApiKey = "o"; s.ElevenLabsApiKey = "e"; })));
        Assert.Equal(SpeechVoiceChoice.Polly, Resolve(Settings(s => s.AmazonPollyCredentials = "AKIA:secret")));
    }

    [Fact]
    public void APickedVoiceThatCannotSpeakFallsBackToTheFreeVoiceThenThisPC()
    {
        var pickedGoogle = Settings(s => s.SpeechVoice = "google");
        Assert.Equal(SpeechVoiceChoice.Account, SpeechVoiceChoices.Resolve(pickedGoogle, true, false, "en"));
        Assert.Equal(SpeechVoiceChoice.Mac, SpeechVoiceChoices.Resolve(pickedGoogle, false, false, "en"));
        var pickedKokoro = Settings(s => s.SpeechVoice = "kokoro");
        // Kokoro still downloading: the account's voice, not this PC's robotic one (2026-10-28).
        Assert.Equal(SpeechVoiceChoice.Account, SpeechVoiceChoices.Resolve(pickedKokoro, true, false, "en"));
        Assert.Equal(SpeechVoiceChoice.Mac, SpeechVoiceChoices.Resolve(pickedKokoro, false, false, "en"));
        Assert.Equal(SpeechVoiceChoice.Kokoro, SpeechVoiceChoices.Resolve(pickedKokoro, false, true, "en"));
        // Kokoro doesn't speak Tamil; Sarvam does.
        Assert.Equal(SpeechVoiceChoice.Mac, SpeechVoiceChoices.Resolve(pickedKokoro, false, true, "ta"));
        var pickedSarvam = Settings(s => { s.SpeechVoice = "sarvam"; s.SarvamKey = "s"; });
        Assert.Equal(SpeechVoiceChoice.Sarvam, SpeechVoiceChoices.Resolve(pickedSarvam, false, false, "ta"));
    }

    [Fact]
    public void KokoroInALanguageItDoesntSpeakCarriesOnAsAutomatic()
    {
        var kokoro = Settings(s => s.SpeechVoice = "kokoro");
        Assert.Equal(SpeechVoiceChoice.Account, SpeechVoiceChoices.Resolve(kokoro, true, true, "ta"));
    }

    [Fact]
    public void ThePickerOffersTheVoicesThereAreKeysForAndKeepsThePickedOne()
    {
        var bare = SpeechVoiceChoices.Offered(Settings(), false, "en");
        Assert.Equal([SpeechVoiceChoice.Automatic, SpeechVoiceChoice.Kokoro, SpeechVoiceChoice.Mac], bare);
        var withKeys = SpeechVoiceChoices.Offered(Settings(s => { s.ElevenLabsApiKey = "e"; s.GoogleCloudApiKey = "g"; }), true, "en");
        Assert.Equal([SpeechVoiceChoice.Automatic, SpeechVoiceChoice.Account, SpeechVoiceChoice.ElevenLabs, SpeechVoiceChoice.Google, SpeechVoiceChoice.Kokoro, SpeechVoiceChoice.Mac], withKeys);
        var pickedWithoutKey = SpeechVoiceChoices.Offered(Settings(s => s.SpeechVoice = "polly"), false, "en");
        Assert.Contains(SpeechVoiceChoice.Polly, pickedWithoutKey);
    }

    [Fact]
    public void AmazonCredentialsAreOneLineWithAnOptionalRegion()
    {
        Assert.Equal(new AWSCredentials("AKIA123", "abc/def+ghi", "us-east-1"), AWSCredentials.TryParse("AKIA123:abc/def+ghi"));
        Assert.Equal("ap-south-1", AWSCredentials.TryParse(" AKIA123 : secret : ap-south-1\n")?.Region);
        Assert.Null(AWSCredentials.TryParse("AKIA123"));
        Assert.Null(AWSCredentials.TryParse(null));
        Assert.DoesNotContain("secret", AWSCredentials.TryParse("AKIA:secret")!.ToString());
    }

    /// <summary>AWS's Signature Version 4 test suite, "get-vanilla".</summary>
    [Fact]
    public void SigningMatchesAWSsOwnTestVector()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        var credentials = new AWSCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1");
        AWSSignatureV4.Sign(request, credentials, "service", DateTimeOffset.FromUnixTimeSeconds(1_440_938_160));
        Assert.Equal("20150830T123600Z", request.HeaderValue("X-Amz-Date"));
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            request.HeaderValue("Authorization"));
    }

    [Fact]
    public void PollyAsksForANeuralVoiceInTheRegionAndSignsForPolly()
    {
        var credentials = new AWSCredentials("AKIA1", "s", "eu-west-1");
        var request = CloudVoiceRequests.PollySpeech(credentials, "hi", "Joanna");
        Assert.Equal("https://polly.eu-west-1.amazonaws.com/v1/speech", request.RequestUri!.AbsoluteUri);
        var body = Body(request);
        Assert.Equal("neural", (string?)body["Engine"]);
        Assert.Equal("Joanna", (string?)body["VoiceId"]);
        Assert.Contains("/eu-west-1/polly/aws4_request, SignedHeaders=content-type;host;x-amz-date,", request.HeaderValue("Authorization"));
        Assert.Equal("Kajal", CloudVoiceRequests.PollyVoice("hi"));
        Assert.Null(CloudVoiceRequests.PollyVoice("ta"));
    }

    [Fact]
    public void ElevenLabsAndGoogleRequestsCarryTheKeyInAHeader()
    {
        var eleven = CloudVoiceRequests.ElevenLabsSpeech("sk_1", "hi");
        Assert.Equal($"/v1/text-to-speech/{CloudVoiceRequests.ElevenLabsVoiceId}", eleven.RequestUri!.AbsolutePath);
        Assert.Equal("?output_format=mp3_44100_128", eleven.RequestUri.Query);
        Assert.Equal("sk_1", eleven.HeaderValue("xi-api-key"));
        Assert.Equal("application/json", eleven.HeaderValue("Content-Type"));
        var google = CloudVoiceRequests.GoogleSpeech("AIza1", "hi", "hi");
        Assert.Equal("AIza1", google.HeaderValue("X-Goog-Api-Key"));
        Assert.Equal("", google.RequestUri!.Query);
        Assert.Equal("hi-IN", (string?)Body(google)["voice"]!["languageCode"]);
        var audio = Encoding.UTF8.GetBytes("mp3");
        var answer = Encoding.UTF8.GetBytes(new JsonObject { ["audioContent"] = Convert.ToBase64String(audio) }.ToJsonString());
        Assert.Equal(audio, CloudVoiceRequests.DecodeGoogleSpeech(answer));
        Assert.Throws<CloudVoiceException>(() => CloudVoiceRequests.DecodeGoogleSpeech("{}"u8.ToArray()));
    }

    /// <summary>The account's free voice (/tts, relayed to ElevenLabs) and an own ElevenLabs key ask for the
    /// same model and settings, from one body builder.</summary>
    [Fact]
    public void TheFreeVoiceAndAnOwnElevenLabsKeySendTheSameBody()
    {
        var body = JsonNode.Parse(CloudVoiceRequests.ElevenLabsSpeechBody("hi"))!.AsObject();
        Assert.Equal("hi", (string?)body["text"]);
        Assert.Equal("eleven_flash_v2_5", (string?)body["model_id"]);
        Assert.Equal(0.5, (double)body["voice_settings"]!["stability"]!);
        Assert.Equal(0.75, (double)body["voice_settings"]!["similarity_boost"]!);
        var ownKeyBody = Body(CloudVoiceRequests.ElevenLabsSpeech("sk_1", "hi"));
        Assert.True(JsonNode.DeepEquals(body, ownKeyBody));
    }

    [Fact]
    public void GeminiAudioComesBackAsWAVWhateverItWasSentAs()
    {
        static byte[] Answer(string mimeType, byte[] audio) => Encoding.UTF8.GetBytes(new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mimeType, ["data"] = Convert.ToBase64String(audio) } }),
                },
            }),
        }.ToJsonString());
        var wav = Encoding.UTF8.GetBytes("RIFF....WAVE");
        Assert.Equal(wav, CloudVoiceRequests.DecodeGeminiSpeech(Answer("audio/wav", wav)));
        var pcm = Enumerable.Repeat((byte)1, 480).ToArray();
        var wrapped = CloudVoiceRequests.DecodeGeminiSpeech(Answer("audio/L16;codec=pcm;rate=24000", pcm));
        Assert.Equal("RIFF"u8.ToArray(), wrapped[..4]);
        Assert.Equal(pcm.Length + 44, wrapped.Length);
        Assert.Equal(24_000, BitConverter.ToInt32(wrapped, 24));
        var request = CloudVoiceRequests.GeminiSpeech("g", "hi");
        Assert.Equal("g", request.HeaderValue("x-goog-api-key"));
        Assert.EndsWith("/models/gemini-3.8-flash-tts:generateContent", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void KokoroSpeaksTheReplyLanguageWithThePickedSpeakerWhenItFits()
    {
        Assert.Equal("Heart", KokoroVoice.SpeakerFor("en", null)?.Name);
        Assert.Equal("George", KokoroVoice.SpeakerFor("en", 26)?.Name);
        // A British speaker picked for English doesn't carry over to Hindi.
        Assert.Equal("hi", KokoroVoice.SpeakerFor("hi", 26)?.LanguageCode);
        Assert.Null(KokoroVoice.SpeakerFor("ta", null));
    }

    [Fact]
    public async Task AMissingKeySaysWhereToAddIt()
    {
        var error = await Assert.ThrowsAsync<CloudVoiceException>(() =>
            CloudVoiceSpeaker.AudioAsync("hi", SpeechVoiceChoice.ElevenLabs, Settings(), "en"));
        Assert.Equal("ElevenLabs needs a key under Settings → Keys & Account.", error.Message);
    }
}
