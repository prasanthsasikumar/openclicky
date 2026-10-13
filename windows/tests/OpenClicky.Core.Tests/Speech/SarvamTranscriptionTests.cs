// Sarvam's sessions driven through a fake socket and a fake wire (the real request builders and
// decoders are the dictation port's SarvamSpeechClient, tested there): the realtime socket's
// partial/final flow, the flush on release, the fallback realtime → chunked → upload, and the upload's
// silence gate.

using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class SarvamTranscriptionTests
{
    /// <summary>A wire whose messages are readable strings: "audio:N", "event:name", "flush"; server frames are "partial:…", "final:…", "end".</summary>
    private sealed class FakeSarvamWire : ISarvamSpeechWire
    {
        public int SampleRate => 16_000;
        public List<SarvamRealtimeModel> RealtimeModelsAsked { get; } = [];
        public string? TranscribeAnswer { get; set; }

        public SarvamSocketRequest RealtimeRequest(string languageCode, IReadOnlyList<string> keyterms, string key, SarvamRealtimeModel model)
        {
            RealtimeModelsAsked.Add(model);
            return new SarvamSocketRequest(new Uri($"wss://example.invalid/realtime?model={model}&language_code={languageCode}"), new Dictionary<string, string> { ["Api-Subscription-Key"] = key });
        }

        public SarvamSocketRequest StreamingRequest(string languageCode, IReadOnlyList<string> keyterms, string key) =>
            new(new Uri("wss://example.invalid/chunked"), new Dictionary<string, string> { ["Api-Subscription-Key"] = key });

        public string RealtimeAudioMessage(byte[] pcm16) => $"audio:{pcm16.Length}";
        public string RealtimeEventMessage(string eventName) => $"event:{eventName}";
        public string StreamingAudioMessage(byte[] pcm16) => $"chunk:{pcm16.Length}";
        public string StreamingFlushMessage => "flush";

        public SarvamRealtimeMessage DecodeRealtimeMessage(string text) => text switch
        {
            _ when text.StartsWith("partial:", StringComparison.Ordinal) => new(SarvamRealtimeMessageKind.Partial, text["partial:".Length..]),
            _ when text.StartsWith("final:", StringComparison.Ordinal) => new(SarvamRealtimeMessageKind.Final, text["final:".Length..]),
            "end" => new(SarvamRealtimeMessageKind.SessionEnd),
            _ => new(SarvamRealtimeMessageKind.Other),
        };

        public SarvamStreamingMessage DecodeStreamingMessage(string text) =>
            text.StartsWith("piece:", StringComparison.Ordinal) ? new(SarvamStreamingMessageKind.Transcript, text["piece:".Length..]) : new(SarvamStreamingMessageKind.Other);

        public Task<string> TranscribeAsync(string key, byte[] wav, string languageCode, IReadOnlyList<string> keyterms, CancellationToken cancellationToken = default) =>
            Task.FromResult(TranscribeAnswer ?? $"{wav.Length - 44} bytes");

        public Task<byte[]> SpeechAsync(string key, string text, string speechLanguageCode, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
        public string? SpeechLanguageCode(string replyLanguageCode) => null;
    }

    [Fact]
    public async Task TheRealtimeSocketStreamsChunksAndTheFlushBringsTheLastWords()
    {
        var wire = new FakeSarvamWire();
        var socket = new FakeSpeechWebSocket { OpensOnConnect = true };
        var log = new TranscriptLog();
        var provider = new SarvamTranscriptionProvider(() => "hi-IN", () => " key\n", wire: () => wire, makeSocket: () => socket);
        var session = await provider.StartStreamingSessionAsync(["OpenClicky"], log.Partial, log.Final, log.Error);
        Assert.Equal("key", socket.ConnectedHeaders!["Api-Subscription-Key"]);
        Assert.Equal([SarvamRealtimeModel.V4], wire.RealtimeModelsAsked);

        // 0.25 s at 16 kHz is 8,000 bytes: two 0.15 s frames make one chunk, the rest waits.
        session.AppendAudio(TestAudio.Tone(2_400));
        session.AppendAudio(TestAudio.Tone(2_400));
        socket.Receive("partial:namaste");
        Assert.True(await Eventually.TrueAsync(() => log.Partials.Contains("namaste")));
        socket.Receive("final:namaste duniya");
        session.RequestFinalTranscript();
        Assert.True(await Eventually.TrueAsync(() => socket.SentTexts.Contains("event:flush")));
        socket.Receive("partial:phir");
        Assert.Equal("namaste duniya phir", await log.WaitForFinalAsync(5_000));
        Assert.Equal(["event:speech_start", "audio:9600", "event:speech_end", "event:flush", "event:end"], socket.SentTexts.Where(text => text != "audio:0").Take(5).ToList());
    }

    [Fact]
    public async Task ARefusedRealtimeSocketFallsBackToV3ThenChunkedThenUpload()
    {
        var wire = new FakeSarvamWire();
        var sockets = new List<FakeSpeechWebSocket>();
        var provider = new SarvamTranscriptionProvider(() => "auto", () => "key", wire: () => wire, makeSocket: () =>
        {
            var refused = new FakeSpeechWebSocket { ConnectFailure = new SpeechWebSocketHandshakeException(403, "refused") };
            sockets.Add(refused);
            return refused;
        });
        var log = new TranscriptLog();
        var session = await provider.StartStreamingSessionAsync([], log.Partial, log.Final, log.Error);
        Assert.IsType<SarvamUploadSession>(session);
        Assert.Equal([SarvamRealtimeModel.V4, SarvamRealtimeModel.V3], wire.RealtimeModelsAsked);
        Assert.Equal(3, sockets.Count);

        session.AppendAudio(TestAudio.Tone(1_600));
        session.RequestFinalTranscript();
        Assert.Equal("3200 bytes", await log.WaitForFinalAsync());
    }

    [Fact]
    public async Task ASilentUploadIsNotSent()
    {
        var wire = new FakeSarvamWire { TranscribeAnswer = "made up" };
        var log = new TranscriptLog();
        var session = new SarvamUploadSession(wire, "key", "en-IN", [], log.Partial, log.Final, log.Error);
        session.AppendAudio(TestAudio.Silence(16_000));
        session.RequestFinalTranscript();
        Assert.Equal("", await log.WaitForFinalAsync());
    }

    [Fact]
    public async Task NoKeyIsSaidInWords()
    {
        var provider = new SarvamTranscriptionProvider(() => "auto", () => "  ", wire: () => new FakeSarvamWire());
        Assert.False(provider.IsConfigured);
        var error = await Assert.ThrowsAsync<SarvamSpeechException>(() => provider.StartStreamingSessionAsync([], _ => { }, _ => { }, _ => { }));
        Assert.Equal("sarvam needs your key. paste it under settings → account.", error.Message);
    }
}
