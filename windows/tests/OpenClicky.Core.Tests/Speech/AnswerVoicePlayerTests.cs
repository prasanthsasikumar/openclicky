// AnswerVoicePlayer: a malformed /tts URL falls back to the hosted backend, and the account's free
// voice is one POST to /tts with the ElevenLabs body (through a fake handler, no network).

using System.Net;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

[Collection("HttpClients.Shared")]
public class AnswerVoicePlayerTests
{
    private sealed class SilentPlayer : IAudioPlayer
    {
        public Task PlayToCompletionAsync(byte[] audio, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayPcm16ToCompletionAsync(byte[] pcm16, int sampleRate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsPlaying => false;
        public void StopPlayback() { }
    }

    [Fact]
    public void AMalformedProxyUrlFallsBackToTheHostedBackend()
    {
        Assert.Equal("https://example.test/tts", AnswerVoicePlayer.ResolvedProxyUrl("https://example.test/tts").AbsoluteUri);
        Assert.Equal($"{OpenClickyConfiguration.HostedBackendUrl}/tts", AnswerVoicePlayer.ResolvedProxyUrl("not a url").AbsoluteUri);
        Assert.Equal($"{OpenClickyConfiguration.HostedBackendUrl}/tts", AnswerVoicePlayer.ResolvedProxyUrl("").AbsoluteUri);
    }

    [Fact]
    public async Task TheFreeVoiceIsOnePostToTtsWithTheElevenLabsBody()
    {
        HttpRequestMessage? sent = null;
        string? sentBody = null;
        HttpClients.UseHandler(new DelegatingTestHandler(async (request, token) =>
        {
            sent = request;
            sentBody = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9]) };
        }));
        try
        {
            var player = new AnswerVoicePlayer(new SilentPlayer(), () => "https://backend.test/tts") { CurrentVoice = () => SpeechVoiceChoice.Account };
            var audio = await player.FetchAudioAsync("hello there");
            Assert.Equal([9, 9], audio);
            Assert.Equal(HttpMethod.Post, sent!.Method);
            Assert.Equal("https://backend.test/tts", sent.RequestUri!.AbsoluteUri);
            Assert.Equal("audio/mpeg", sent.HeaderValue("Accept"));
            Assert.Equal("application/json", sent.HeaderValue("Content-Type"));
            var body = JsonNode.Parse(sentBody!)!;
            Assert.Equal("hello there", (string?)body["text"]);
            Assert.Equal(CloudVoiceRequests.ElevenLabsModel, (string?)body["model_id"]);
        }
        finally
        {
            HttpClients.Reset();
        }
    }

    /// <summary>This PC's voice, faked: it "renders" a fixed sound.</summary>
    private sealed class FakeSystemVoice : ISystemVoice
    {
        public static readonly byte[] Sound = [7, 7, 7];
        public IReadOnlyList<SystemVoiceInfo> Voices(string? languageCode = null) => [new("aria", "Microsoft Aria", "en-US", "Female")];
        public SystemVoiceInfo? CurrentVoice(string languageCode, string? pickedVoiceId = null) => Voices(languageCode)[0];
        public double DefaultSpeakingRate => 1;
        public Task<byte[]> RenderWavAsync(string text, string languageCode, string? pickedVoiceId = null, double? speakingRate = null,
                                           CancellationToken cancellationToken = default) => Task.FromResult(Sound);
    }

    /// <summary>A refused free voice (402: the allowance is spent) never leaves the answer unspoken: the local voice says
    /// it instead, Kokoro when downloaded, else this PC's (#23).</summary>
    [Fact]
    public async Task ARefusedFreeVoiceIsSaidByTheLocalVoice()
    {
        HttpClients.UseHandler(new DelegatingTestHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)402) { Content = new StringContent("{\"error\":\"limit\"}") })));
        try
        {
            var services = new SpeechVoiceServices(() => false, (_, _, _) => throw new InvalidOperationException("no kokoro"), new FakeSystemVoice());
            var player = new AnswerVoicePlayer(new SilentPlayer(), () => "https://backend.test/tts", services) { CurrentVoice = () => SpeechVoiceChoice.Account };
            Assert.Equal(FakeSystemVoice.Sound, await player.FetchAudioAsync("hi"));
        }
        finally
        {
            HttpClients.Reset();
        }
    }
}
