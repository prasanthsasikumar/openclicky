// Ports SarvamSpeechClientTests.swift (the pure parts): request shapes and message decoding for
// Sarvam's REST and streaming speech APIs, with no network. The upload splitting and silence gate
// tests belong to the Speech port's upload session.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Tests.Providers;

namespace OpenClicky.Core.Tests.Dictation;

public sealed class SarvamSpeechClientTests
{
    private static byte[] Bytes(HttpContent content)
    {
        using var stream = new MemoryStream();
        content.ReadAsStream().CopyTo(stream);
        return stream.ToArray();
    }

    [Fact]
    public void TheTranscriptionRequestIsMultipartWithTheKeyInAHeader()
    {
        var request = SarvamSpeechClient.TranscriptionRequest([1, 2, 3, 4], DictationLanguage.Named("ml-IN"), ["FlowsXR"], " key123\n", SarvamSpeechClient.Host, boundary: "B");
        Assert.Equal("https://api.sarvam.ai/speech-to-text", request.RequestUri!.AbsoluteUri);
        Assert.Equal("key123", request.HeaderValue("api-subscription-key"));
        Assert.Equal("multipart/form-data; boundary=B", request.Content!.Headers.GetValues("Content-Type").Single());
        var body = Encoding.UTF8.GetString(Bytes(request.Content));
        Assert.Contains("name=\"language_code\"\r\n\r\nml-IN", body);
        Assert.Contains("name=\"model\"\r\n\r\nsaaras:v4", body);
        Assert.Contains("name=\"keyterms\"\r\n\r\nFlowsXR", body);
        Assert.Contains("filename=\"take.wav\"", body);
        Assert.EndsWith("\r\n--B--\r\n", body);
    }

    [Fact]
    public void TheSpeechRequestAsksBulbulInTheReplyLanguage()
    {
        var request = SarvamSpeechClient.SpeechRequest("hello", "hi-IN", "k", SarvamSpeechClient.Host);
        Assert.Equal("https://api.sarvam.ai/text-to-speech", request.RequestUri!.AbsoluteUri);
        Assert.Equal("k", request.HeaderValue("api-subscription-key"));
        var body = JsonNode.Parse(request.Content!.BodyText())!;
        Assert.Equal("bulbul:v3", body["model"]!.GetValue<string>());
        Assert.Equal("hi-IN", body["target_language_code"]!.GetValue<string>());
        Assert.Equal("hello", body["text"]!.GetValue<string>());
    }

    [Fact]
    public void SpeechDecodesTheFirstAudioAndRefusesAnEmptyAnswer()
    {
        var wav = Encoding.UTF8.GetBytes("RIFF....WAVE");
        var answer = Encoding.UTF8.GetBytes(new JsonObject { ["request_id"] = "r", ["audios"] = new JsonArray(Convert.ToBase64String(wav)) }.ToJsonString());
        Assert.Equal(wav, SarvamSpeechClient.DecodeSpeech(answer));
        var empty = Encoding.UTF8.GetBytes("""{"audios":[]}""");
        Assert.Throws<SarvamSpeechException>(() => SarvamSpeechClient.DecodeSpeech(empty));
    }

    [Fact]
    public void BulbulSpeaksIndianLanguagesOnly()
    {
        Assert.Equal("en-IN", SarvamSpeechClient.SpeechLanguageCode("en"));
        Assert.Equal("ta-IN", SarvamSpeechClient.SpeechLanguageCode("ta"));
        Assert.Null(SarvamSpeechClient.SpeechLanguageCode("fr"));
    }

    [Fact]
    public void AutoDetectIsSentAsUnknown()
    {
        Assert.Equal("unknown", SarvamSpeechClient.LanguageCode(DictationLanguage.Auto));
        Assert.Equal("hi-IN", SarvamSpeechClient.LanguageCode(DictationLanguage.Named("hi-IN")));
    }

    [Fact]
    public void TheStreamingSocketCarriesLanguageModelAndRate()
    {
        var request = SarvamSpeechClient.StreamingRequest(DictationLanguage.Named("hi-IN"), ["Kochi", "FlowsXR"], "k");
        var url = request.Url.AbsoluteUri;
        Assert.StartsWith("wss://api.sarvam.ai/speech-to-text/ws?", url);
        Assert.Contains("language-code=hi-IN", url);
        Assert.Contains("model=saaras:v4", url);
        Assert.Contains("sample_rate=16000", url);
        // The socket wants a JSON array ("'keyterms' must be a valid JSON array of strings"), percent-encoded in the query.
        Assert.Contains("keyterms=%5B%22Kochi%22,%22FlowsXR%22%5D", url);
        Assert.Equal("k", request.HeaderValue("Api-Subscription-Key"));
    }

    [Fact]
    public void AudioMessagesAreBase64WavAt16k()
    {
        var message = SarvamSpeechClient.StreamingAudioMessage([0, 0, 1, 0]);
        var audio = JsonNode.Parse(message)!["audio"]!;
        Assert.Equal("16000", audio["sample_rate"]!.GetValue<string>());
        Assert.Equal("audio/wav", audio["encoding"]!.GetValue<string>());
        var wav = Convert.FromBase64String(audio["data"]!.GetValue<string>());
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(44 + 4, wav.Length);
    }

    [Fact]
    public void ServerMessagesDecode()
    {
        Assert.Equal(SarvamSpeechClient.StreamingServerMessage.Transcript("namaste"),
            SarvamSpeechClient.DecodeStreamingMessage("""{"type":"data","data":{"transcript":"namaste","language_code":"hi-IN"}}"""));
        Assert.Equal(SarvamSpeechClient.StreamingServerMessage.Error("bad key"),
            SarvamSpeechClient.DecodeStreamingMessage("""{"type":"error","data":{"error":"bad key","code":"x"}}"""));
        Assert.Equal(SarvamSpeechClient.StreamingServerMessage.Other,
            SarvamSpeechClient.DecodeStreamingMessage("""{"type":"events","data":{"signal_type":"START_SPEECH"}}"""));
        Assert.Equal(SarvamSpeechClient.StreamingServerMessage.Other, SarvamSpeechClient.DecodeStreamingMessage("not json"));
    }

    [Fact]
    public void RefusalsAreToldApart()
    {
        Assert.Equal(SarvamSpeechErrorKind.KeyRefused, SarvamSpeechException.Refusal(403, []).Kind);
        Assert.Equal(SarvamSpeechErrorKind.OutOfCredits,
            SarvamSpeechException.Refusal(429, Encoding.UTF8.GetBytes("""{"error":{"code":"insufficient_quota_error"}}""")).Kind);
        Assert.Equal(SarvamSpeechErrorKind.Busy, SarvamSpeechException.Refusal(429, []).Kind);
        var refused = SarvamSpeechException.Refusal(500, Encoding.UTF8.GetBytes("""{"error":{"message":"boom"}}"""));
        Assert.Equal((SarvamSpeechErrorKind.Refused, 500, "boom"), (refused.Kind, refused.Status, refused.Detail));
        Assert.Equal("sarvam refused (500): boom", refused.Message);
    }

    [Fact]
    public void TheChatRequestIsOpenAIShaped()
    {
        var request = SarvamSpeechClient.ChatRequest("s", "u", 10, "k", SarvamSpeechClient.Host);
        Assert.Equal("https://api.sarvam.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer k", request.HeaderValue("Authorization"));
        Assert.Equal("k", request.HeaderValue("api-subscription-key"));
        var body = JsonNode.Parse(request.Content!.BodyText())!.AsObject();
        Assert.Equal("sarvam-105b", body["model"]!.GetValue<string>());
        Assert.Equal(["system", "user"], body["messages"]!.AsArray().Select(message => message!["role"]!.GetValue<string>()));
        Assert.Equal(10, body["max_tokens"]!.GetValue<int>());
        // sarvam-105b reasons by default and "low" still spent all 1,024 tokens thinking, leaving
        // no answer (2026-10-11). An explicit null switches reasoning off: 0.3 s, a real answer.
        Assert.True(body.ContainsKey("reasoning_effort"));
        Assert.Null(body["reasoning_effort"]);
    }

    [Fact]
    public async Task ACompletionGoesToSarvamAndComesBackTrimmed()
    {
        HttpRequestMessage? sent = null;
        using var client = new HttpClient(new DelegatingTestHandler((request, _) =>
        {
            sent = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"  Hello there.\n"}}]}"""),
            });
        }));
        var sarvam = new SarvamSpeechClient(" k\n", httpClient: client);
        Assert.Equal("Hello there.", await sarvam.CompleteAsync("tidy", "hello there"));
        Assert.Equal("Bearer k", sent!.HeaderValue("Authorization"));
        Assert.Equal("Sarvam", new SarvamTakePolisher(sarvam).DisplayName);
    }

    [Fact]
    public async Task ARefusedKeyIsSaidAsSarvams()
    {
        using var client = new HttpClient(new DelegatingTestHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") })));
        var error = await Assert.ThrowsAsync<SarvamSpeechException>(() => new SarvamSpeechClient("k", httpClient: client).CompleteAsync("s", "u"));
        Assert.Equal(SarvamSpeechErrorKind.KeyRefused, error.Kind);
        await Assert.ThrowsAsync<SarvamSpeechException>(() => new SarvamSpeechClient(" ", httpClient: client).CompleteAsync("s", "u"));
    }

    [Fact]
    public void TranscriptsDecode()
    {
        Assert.Equal("namaste", SarvamSpeechClient.DecodeTranscription(Encoding.UTF8.GetBytes("""{"transcript":" namaste "}""")));
        Assert.Throws<SarvamSpeechException>(() => SarvamSpeechClient.DecodeTranscription(Encoding.UTF8.GetBytes("{}")));
    }
}

public sealed class SarvamRealtimeClientTests
{
    [Fact]
    public void TheRealtimeSocketUsesManualEndpointingAndRawPcm()
    {
        var request = SarvamSpeechClient.RealtimeRequest(DictationLanguage.Auto, ["FlowsXR", " "], "k");
        var url = request.Url.AbsoluteUri;
        // v4 takes keyterms as a JSON array; v3-realtime refuses them and gets a prompt instead.
        Assert.Contains("model=saaras:v4", url);
        Assert.Contains("keyterms=%5B%22FlowsXR%22%5D", url);
        Assert.DoesNotContain("prompt=", url);
        var v3 = SarvamSpeechClient.RealtimeRequest(DictationLanguage.Auto, ["FlowsXR"], "k", SarvamSpeechClient.RealtimeModel.V3).Url.AbsoluteUri;
        Assert.Contains("model=saaras:v3-realtime", v3);
        Assert.DoesNotContain("keyterms=", v3);
        Assert.Contains("prompt=Names%20and%20terms%20that%20may%20come%20up:%20FlowsXR.", v3);
        Assert.DoesNotContain("keyterms", SarvamSpeechClient.RealtimeRequest(DictationLanguage.Auto, [], "k").Url.AbsoluteUri);
        Assert.StartsWith("wss://api.sarvam.ai/speech-to-text-realtime/ws?", url);
        Assert.Contains("language_code=auto", url);
        Assert.Contains("endpointing=manual", url);
        Assert.Contains("encoding=linear16", url);
        Assert.Equal("k", request.HeaderValue("Api-Subscription-Key"));
    }

    [Fact]
    public void RealtimeMessagesEncodeAndDecode()
    {
        var audio = JsonNode.Parse(SarvamSpeechClient.RealtimeAudioMessage([1, 0]))!;
        Assert.Equal("audio_input", audio["event"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 0]), audio["audio"]!.GetValue<string>());
        Assert.Equal("{\"event\":\"flush\"}", SarvamSpeechClient.RealtimeEventMessage("flush"));
        Assert.Equal(SarvamSpeechClient.RealtimeServerMessage.Partial("tech week"),
            SarvamSpeechClient.DecodeRealtimeMessage("""{"event":"transcript.partial","utterance_idx":0,"text":"tech week"}"""));
        Assert.Equal(SarvamSpeechClient.RealtimeServerMessage.Final("Tech Week starts."),
            SarvamSpeechClient.DecodeRealtimeMessage("""{"event":"transcript.final","utterance_idx":0,"text":"Tech Week starts."}"""));
        Assert.Equal(SarvamSpeechClient.RealtimeServerMessage.Error("no", fatal: false),
            SarvamSpeechClient.DecodeRealtimeMessage("""{"event":"error","code":"invalid_config","is_fatal":false,"message":"no"}"""));
        Assert.Equal(SarvamSpeechClient.RealtimeServerMessage.SessionEnd,
            SarvamSpeechClient.DecodeRealtimeMessage("""{"event":"session.end","request_id":"r"}"""));
        Assert.Equal(SarvamSpeechClient.RealtimeServerMessage.Other, SarvamSpeechClient.DecodeRealtimeMessage("""{"event":"pong"}"""));
    }
}
