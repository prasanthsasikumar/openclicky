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

    [Fact]
    public async Task ARefusedFreeVoiceSaysTheStatus()
    {
        HttpClients.UseHandler(new DelegatingTestHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)402) { Content = new StringContent("{\"error\":\"limit\"}") })));
        try
        {
            var player = new AnswerVoicePlayer(new SilentPlayer(), () => "https://backend.test/tts") { CurrentVoice = () => SpeechVoiceChoice.Account };
            var error = await Assert.ThrowsAsync<BackendHttpException>(() => player.FetchAudioAsync("hi"));
            Assert.Equal(402, error.Status);
            Assert.Equal(BackendHttpException.ServiceKind.Voice, error.Service);
        }
        finally
        {
            HttpClients.Reset();
        }
    }
}
