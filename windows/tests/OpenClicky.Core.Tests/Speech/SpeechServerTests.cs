// The person's own speech server (SpeechServer): its settings, the two OpenAI audio requests, what comes back and what a
// failure says, which listed models are offered first, and where it sits among the voices and the dictation engines.
// No network: a fake server answers.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Companion;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class SpeechServerTests
{
    private static OpenClickyShellSettings Settings(Action<OpenClickyShellSettings>? edit = null)
    {
        var settings = new OpenClickyShellSettings();
        edit?.Invoke(settings);
        return settings;
    }

    private static readonly SpeechServerSettings Server = new("http://ai-pc:11434", null, "gemma4:e2b", "kokoro", "af_heart");

    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (Asked) Asked.Add((request, body));
            return answer(request);
        }
    }

    [Fact]
    public void ItIsOffUntilItsAddressIsSetAndEachHalfUntilItsModelIs()
    {
        Assert.Null(SpeechServer.From(Settings()));
        Assert.Null(SpeechServer.From(Settings(s => s.SpeechServerUrl = "not an address")));
        var server = SpeechServer.From(Settings(s =>
        {
            s.SpeechServerUrl = " http://ai-pc:8000/v1/ ";
            s.SpeechServerHearingModel = " whisper ";
            s.SpeechServerVoice = "  ";
        }));
        Assert.NotNull(server);
        Assert.Equal("http://ai-pc:8000", server.BaseUrl);
        Assert.Equal("whisper", server.HearingModel);
        Assert.Null(server.VoiceModel);
        Assert.Null(server.Voice);
        Assert.True(SpeechServer.Hears(Settings(s => { s.SpeechServerUrl = "http://localhost:11434"; s.SpeechServerHearingModel = "gemma4:e2b"; })));
        Assert.False(SpeechServer.Speaks(Settings(s => { s.SpeechServerUrl = "http://localhost:11434"; s.SpeechServerHearingModel = "gemma4:e2b"; })));
        Assert.False(SpeechServer.Hears(Settings(s => s.SpeechServerHearingModel = "gemma4:e2b")));
    }

    [Fact]
    public async Task HearingSendsTheTakeAsAWavWithItsModelLanguageAndPrompt()
    {
        var wav = BuddyWAVFileBuilder.BuildWavData(new byte[3200], 16_000);
        using var request = SpeechServer.TranscriptionRequest(Server with { Key = "secret" }, "gemma4:e2b", wav, "ur", "Expect: Clicky", "B");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://ai-pc:11434/v1/audio/transcriptions", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
        Assert.Equal("multipart/form-data; boundary=B", request.Content!.Headers.GetValues("Content-Type").Single());
        var body = Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync());
        Assert.Contains("name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav", body);
        Assert.Contains("name=\"model\"\r\n\r\ngemma4:e2b\r\n", body);
        Assert.Contains("name=\"language\"\r\n\r\nur\r\n", body);
        Assert.Contains("name=\"prompt\"\r\n\r\nExpect: Clicky\r\n", body);
        Assert.Contains("name=\"response_format\"\r\n\r\njson\r\n", body);
        Assert.EndsWith("--B--\r\n", body);
        // No language (detect it) and no key: neither is sent.
        using var plain = SpeechServer.TranscriptionRequest(Server, "whisper", wav, null, null, "B");
        Assert.Null(plain.Headers.Authorization);
        Assert.DoesNotContain("name=\"language\"", Encoding.UTF8.GetString(await plain.Content!.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task SpeakingAsksForWavInTheVoiceWhenOneIsSet()
    {
        using var request = SpeechServer.SpeechRequest(Server, "kokoro", "Hello there.");
        Assert.Equal("http://ai-pc:11434/v1/audio/speech", request.RequestUri!.ToString());
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
        Assert.Equal("kokoro", (string?)body["model"]);
        Assert.Equal("Hello there.", (string?)body["input"]);
        Assert.Equal("wav", (string?)body["response_format"]);
        Assert.Equal("af_heart", (string?)body["voice"]);
        // No voice: the server's own default speaks (Ollama's Phonon takes "default", Kokoro-FastAPI its first voice).
        using var noVoice = SpeechServer.SpeechRequest(Server with { Voice = null }, "phonon", "Hi.");
        Assert.Null(JsonNode.Parse(await noVoice.Content!.ReadAsStringAsync())!["voice"]);
    }

    [Fact]
    public async Task TheServersWordsAndAudioComeBack()
    {
        var fake = new FakeServer(request => request.RequestUri!.AbsolutePath.EndsWith("transcriptions", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\" How does the heart pump blood? \"}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([82, 73, 70, 70]) });
        var client = new HttpClient(fake);
        Assert.Equal("How does the heart pump blood?", await SpeechServer.TranscribeAsync(Server, [1, 2], "en", null, client: client));
        Assert.Equal([82, 73, 70, 70], await SpeechServer.SpeakAsync(Server, "Hi.", client: client));
        Assert.Equal(2, fake.Asked.Count);
    }

    [Fact]
    public async Task AWavWhoseHeaderCountsOnlyTheFirstSentenceIsPlayedWhole()
    {
        static uint SizeAt(byte[] wav, int at) => BitConverter.ToUInt32(wav, at);
        static byte[] Sentence(byte sample, int bytes) => BuddyWAVFileBuilder.BuildWavData(Enumerable.Repeat(sample, bytes).ToArray(), 22_050);
        // Speaches + Piper: one whole WAV per sentence, back to back.
        var whole = Sentence(1, 1000).Concat(Sentence(2, 2000)).Concat(Sentence(3, 600)).ToArray();
        var joined = SpeechServer.WithWholeSoundData(whole);
        Assert.Equal(44 + 3600, joined.Length);
        Assert.Equal(3600u, SizeAt(joined, 40));
        Assert.Equal(36u + 3600u, SizeAt(joined, 4));
        Assert.Equal(Enumerable.Repeat((byte)1, 1000).Concat(Enumerable.Repeat((byte)2, 2000)).Concat(Enumerable.Repeat((byte)3, 600)), joined[44..]);
        var client = new HttpClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(whole) }));
        Assert.Equal(3600u, SizeAt(await SpeechServer.SpeakAsync(Server, "Three sentences.", client: client), 40));
        // Sentences in different formats aren't joined.
        var mixed = Sentence(1, 1000).Concat(BuddyWAVFileBuilder.BuildWavData(new byte[100], 24_000)).ToArray();
        Assert.Same(mixed, SpeechServer.WithWholeSoundData(mixed));

        // A streamed header that doesn't know its length gets what came, in whole samples.
        var streamed = Sentence(4, 1000).Concat(new byte[] { 4, 4, 4 }).ToArray();
        BitConverter.TryWriteBytes(streamed.AsSpan(40, 4), uint.MaxValue);
        Assert.Equal(1002u, SizeAt(SpeechServer.WithWholeSoundData(streamed), 40));

        // A right header, or a LIST chunk after the sound, is left alone.
        var right = BuddyWAVFileBuilder.BuildWavData(new byte[2000], 24_000);
        Assert.Same(right, SpeechServer.WithWholeSoundData(right));
        var list = right.Concat(Encoding.ASCII.GetBytes("LIST")).Concat(BitConverter.GetBytes(4u)).Concat(Encoding.ASCII.GetBytes("INFO")).ToArray();
        Assert.Same(list, SpeechServer.WithWholeSoundData(list));
        byte[] notAWav = [1, 2, 3];
        Assert.Same(notAWav, SpeechServer.WithWholeSoundData(notAWav));
    }

    [Fact]
    public async Task AFailureSaysWhatTheServerSaid()
    {
        var client = new HttpClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"model 'kokoro' not found\"}"),
        }));
        var heard = await Assert.ThrowsAsync<TranscriptionProviderException>(() => SpeechServer.TranscribeAsync(Server, [1], null, null, client: client));
        Assert.Contains("400", heard.Message);
        Assert.Contains("not found", heard.Message);
        var spoken = await Assert.ThrowsAsync<CloudVoiceException>(() => SpeechServer.SpeakAsync(Server, "Hi.", client: client));
        Assert.Equal(400, spoken.Status);
        Assert.Equal(SpeechServer.VendorName, spoken.Vendor);
    }

    [Fact]
    public async Task NothingAnsweringSaysSoInWords()
    {
        var client = new HttpClient(new FakeServer(_ => throw new HttpRequestException("refused")));
        var error = await Assert.ThrowsAsync<TranscriptionProviderException>(() => SpeechServer.TranscribeAsync(Server, [1], null, null, client: client));
        Assert.Equal("Nothing answered at http://ai-pc:11434. Is the speech server running?", error.Message);
    }

    [Fact]
    public void TheModelsThatLookRightComeFirstAndNoneIsHidden()
    {
        string[] served = ["gemma4:26b", "kokoro", "whisper-large-v3-turbo", "qwen3.5:9b", "gemma4:e2b-it-qat", "speaches-ai/Kokoro-82M-v1.0-ONNX", "phonon"];
        Assert.Equal(["whisper-large-v3-turbo", "gemma4:e2b-it-qat", "gemma4:26b", "kokoro", "qwen3.5:9b", "speaches-ai/Kokoro-82M-v1.0-ONNX", "phonon"],
            SpeechServer.Ordered(served, forHearing: true));
        Assert.Equal(["kokoro", "speaches-ai/Kokoro-82M-v1.0-ONNX", "phonon", "gemma4:26b", "whisper-large-v3-turbo", "qwen3.5:9b", "gemma4:e2b-it-qat"],
            SpeechServer.Ordered(served, forHearing: false));
        Assert.True(SpeechServer.LooksLikeItHears("deepdml/faster-whisper-large-v3-turbo-ct2"));
        Assert.True(SpeechServer.LooksLikeItSpeaks("qwen3-tts:1.7b"));
        Assert.False(SpeechServer.LooksLikeItSpeaks("gemma3:27b"));
    }

    [Fact]
    public void ASpeechServerThatSpeaksIsAutomaticsFirstVoice()
    {
        static SpeechVoiceChoice Resolve(OpenClickyShellSettings shell) => SpeechVoiceChoices.Resolve(shell, hasAccount: true, kokoroInstalled: true, "en");
        var withVoice = Settings(s => { s.SpeechServerUrl = "http://ai-pc:8000"; s.SpeechServerVoiceModel = "kokoro"; s.OpenaiApiKey = "k"; });
        Assert.Equal(SpeechVoiceChoice.Server, Resolve(withVoice));
        Assert.Contains(SpeechVoiceChoice.Server, SpeechVoiceChoices.Offered(withVoice, hasAccount: false, "en"));
        Assert.Equal("Your speech server", SpeechVoiceChoice.Server.Title());
        Assert.Equal(SpeechVoiceChoice.Server, SpeechVoiceChoices.FromStored("server"));
        // Hearing only: it doesn't speak, so it isn't offered as a voice, and a picked one hands over like a missing key.
        var hearingOnly = Settings(s => { s.SpeechServerUrl = "http://ai-pc:8000"; s.SpeechServerHearingModel = "whisper"; });
        Assert.DoesNotContain(SpeechVoiceChoice.Server, SpeechVoiceChoices.Offered(hearingOnly, hasAccount: false, "en"));
        hearingOnly.SpeechVoice = "server";
        Assert.Equal(SpeechVoiceChoice.Account, Resolve(hearingOnly));
    }

    [Fact]
    public void DictationCanBeHeardByTheServer()
    {
        Assert.Equal(DictationEngineChoice.Server, DictationEngineChoices.FromRawValue("server"));
        Assert.Equal("Your speech server", DictationEngineChoice.Server.SettingsTitle());
        Assert.Equal("Audio goes to your server", DictationEngineChoice.Server.AudioDestinationTag());
        Assert.Contains("ollama", DictationEngineChoice.Server.SearchKeywords());
        Assert.False(DictationEngineChoice.Server.IsOnDevice());
        Assert.Equal("set up speech server ›", IslandVoiceDestination.BlockedPillLabel(DictationEngineChoice.Server, SpeechServer.NeedsServerSentence));
        Assert.Equal(DictationSettingsPage.Clicky, IslandVoiceDestination.SetupPage(DictationEngineChoice.Server, SpeechServer.NeedsServerSentence));
        Assert.Equal("open your models", IslandVoiceDestination.ButtonTitle(DictationEngineChoice.Server, SpeechServer.NeedsServerSentence));
    }

    [Fact]
    public void TheProviderIsReadyOnlyWithAHearingModel()
    {
        var shell = Settings(s => s.SpeechServerUrl = "http://ai-pc:8000");
        var provider = new SpeechServerTranscriptionProvider(() => "en", () => shell);
        Assert.False(provider.IsConfigured);
        Assert.NotNull(provider.UnavailableExplanation);
        shell.SpeechServerHearingModel = "whisper";
        Assert.True(provider.IsConfigured);
        Assert.Null(provider.UnavailableExplanation);
        Assert.False(provider.RequiresSpeechRecognitionPermission);
    }

    [Fact]
    public void TheSpeechServerRowIsUnderYourModels()
    {
        var group = SettingsLayout.Groups.Single(g => g.Title == "Your models");
        Assert.Contains("models.speech", group.ItemIds);
    }
}
