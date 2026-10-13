// Ports RealtimeVoiceClientTests.swift and the Realtime half of VoiceReliabilityTests.swift: the
// socket URL from a server-supplied model, nothing sent before the server's first event (issue #1),
// a refused handshake that says what was refused, the connection rules, and — through a fake socket
// — the push-to-talk turn and a point_at call snapped to the screenshot's text.

using System.Text.Json.Nodes;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class RealtimeSocketUrlTests
{
    [Fact]
    public void ANormalModelNameIsUsedAsItIs() =>
        Assert.Equal("wss://api.openai.com/v1/realtime?model=gpt-realtime-mini", RealtimeVoiceClient.RealtimeSocketUrl("gpt-realtime-mini").AbsoluteUri);

    [Fact]
    public void AdversarialModelNamesStillMakeAWellFormedUrlOnOpenAIsHost()
    {
        string[] adversarialModelNames =
        [
            "", " ", "gpt realtime", "gpt\nrealtime", "gpt\u0000realtime", "\u202Ereversed", "gpt&model=something-else", new string('x', 10_000),
        ];
        foreach (var modelName in adversarialModelNames)
        {
            var url = RealtimeVoiceClient.RealtimeSocketUrl(modelName);
            Assert.Equal("wss", url.Scheme);
            Assert.Equal("api.openai.com", url.Host);
            Assert.StartsWith("?model=", url.Query);
            Assert.DoesNotContain("&", url.Query);
        }
    }

    [Fact]
    public void TheSessionAnswerGivesTheSecretAndTheModel()
    {
        Assert.Equal(("ek_1", "gpt-realtime-mini"), RealtimeVoiceClient.ParseClientSecret("""{"value":"ek_1","session":{"model":"gpt-realtime-mini"}}"""u8.ToArray()));
        Assert.Equal(("ek_2", "gpt-realtime"), RealtimeVoiceClient.ParseClientSecret("""{"value":"ek_2"}"""u8.ToArray()));
        Assert.Null(RealtimeVoiceClient.ParseClientSecret("""{"error":"no"}"""u8.ToArray()));
    }
}

public class RealtimeConnectionRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>OpenAI caps a Realtime session at 60 minutes: one that old is replaced before a turn.</summary>
    [Fact]
    public void AConnectionNearTheHourCapIsReplaced()
    {
        Assert.False(RealtimeVoiceClient.IsTooOld(Now.AddMinutes(-10), Now));
        Assert.True(RealtimeVoiceClient.IsTooOld(Now.AddMinutes(-50), Now));
        Assert.True(RealtimeVoiceClient.IsTooOld(null, Now));
    }

    /// <summary>Sleep can leave a socket that still looks open: a quiet one is checked, a busy one trusted.</summary>
    [Fact]
    public void AQuietConnectionIsCheckedAndABusyOneIsTrusted()
    {
        Assert.False(RealtimeVoiceClient.NeedsPing(Now.AddSeconds(-5), Now));
        Assert.True(RealtimeVoiceClient.NeedsPing(Now.AddSeconds(-90), Now));
        Assert.True(RealtimeVoiceClient.NeedsPing(null, Now));
        Assert.True(RealtimeVoiceClient.NeedsPing(DateTimeOffset.MinValue, Now));
    }

    /// <summary>Every session is minted and billed, so keep-warm reconnects only while OpenClicky is used.</summary>
    [Fact]
    public void KeepWarmReconnectsOnlyWhileRecentlyUsed()
    {
        Assert.True(RealtimeVoiceClient.IsRecentlyUsed(Now.AddMinutes(-1), Now));
        Assert.False(RealtimeVoiceClient.IsRecentlyUsed(Now.AddHours(-3), Now));
    }

    /// <summary>Under OpenAI's own 100 ms minimum a turn is not sent (an empty commit made the model invent a task).</summary>
    [Fact]
    public void ATurnWithoutAudioIsNeverSent()
    {
        Assert.False(RealtimeVoiceClient.HasEnoughAudioForATurn(0));
        Assert.False(RealtimeVoiceClient.HasEnoughAudioForATurn(4_799));
        Assert.True(RealtimeVoiceClient.HasEnoughAudioForATurn(4_800));
        Assert.True(RealtimeVoiceClient.HasEnoughAudioForATurn(96_000));
    }

    [Fact]
    public void ARefusedHandshakeSaysWhatWasRefused()
    {
        var rejected = RealtimeVoiceClient.HandshakeFailureDescription(401, "bad server response");
        Assert.Contains("401", rejected);
        Assert.Contains("rejected", rejected);
        Assert.Contains("rate limit", RealtimeVoiceClient.HandshakeFailureDescription(429, "x"));
        Assert.Equal("offline", RealtimeVoiceClient.HandshakeFailureDescription(null, "offline"));
        Assert.Equal("closed", RealtimeVoiceClient.HandshakeFailureDescription(101, "closed"));
    }

    [Fact]
    public void ABackendRefusalUsesTheBackendsOwnWords()
    {
        var description = RealtimeVoiceClient.BackendRefusalDescription(401, """{"error":"invalid OpenAI key"}"""u8.ToArray());
        Assert.Contains("invalid OpenAI key", description);
        Assert.Contains("401", description);
        Assert.DoesNotContain("{", description);
        Assert.Contains("quota exceeded", RealtimeVoiceClient.BackendRefusalDescription(500, """{"error":{"message":"quota exceeded"}}"""u8.ToArray()));
        Assert.Equal("no client secret", RealtimeVoiceClient.BackendRefusalDescription(0, []));
    }

    [Fact]
    public void ThePromptNoLongerInvitesSwitchingAndNamesWindowsTools()
    {
        Assert.DoesNotContain("unless the user speaks another language", RealtimeVoiceClient.DefaultInstructions);
        Assert.Contains("Windows voice assistant", RealtimeVoiceClient.DefaultInstructions);
        Assert.Contains("reveal_in_explorer", RealtimeVoiceClient.DefaultInstructions);
    }

    [Fact]
    public void TheMicrophoneLineNamesAPrivacySwitchInsteadOfANumber()
    {
        Assert.Equal("microphone authorization: allowed", RealtimeVoiceClient.MicrophoneAuthorizationDescription(MicrophoneConsent.Allowed));
        Assert.Contains("desktop apps", RealtimeVoiceClient.MicrophoneAuthorizationDescription(MicrophoneConsent.DeniedForDesktopApps));
        Assert.Contains("unknown", RealtimeVoiceClient.MicrophoneAuthorizationDescription(MicrophoneConsent.Unknown));
    }

    [Fact]
    public void TheSelectionIsNamedAsWhatThisMeans()
    {
        var line = RealtimeVoiceClient.SelectedTextContextLine("reconnects");
        Assert.Contains("«reconnects»", line);
        Assert.Contains("they almost certainly mean the selection", line);
    }
}

public class RealtimeSessionUpdateTests
{
    [Fact]
    public void PushToTalkHasNoTurnDetectionAndAlwaysOnUsesServerVad()
    {
        var pushToTalk = RealtimeVoiceClient.BuildSessionUpdate(RealtimeTurnMode.PushToTalk, "be brief", "marin", "en");
        var input = pushToTalk["session"]!["audio"]!["input"]!.AsObject();
        Assert.True(input.ContainsKey("turn_detection"));
        Assert.Null(input["turn_detection"]);
        Assert.Equal(24_000, input["format"]!["rate"]!.GetValue<int>());
        Assert.Equal("en", input["transcription"]!["language"]!.GetValue<string>());
        Assert.Equal("gpt-4o-mini-transcribe", input["transcription"]!["model"]!.GetValue<string>());
        Assert.Equal("marin", pushToTalk["session"]!["audio"]!["output"]!["voice"]!.GetValue<string>());

        var alwaysOn = RealtimeVoiceClient.BuildSessionUpdate(RealtimeTurnMode.AlwaysOn, "be brief", null, "hi");
        var turnDetection = alwaysOn["session"]!["audio"]!["input"]!["turn_detection"]!.AsObject();
        Assert.Equal("server_vad", turnDetection["type"]!.GetValue<string>());
        Assert.Equal(600, turnDetection["silence_duration_ms"]!.GetValue<int>());
        Assert.False(turnDetection["create_response"]!.GetValue<bool>());
        Assert.True(turnDetection["interrupt_response"]!.GetValue<bool>());
        Assert.False(alwaysOn["session"]!["audio"]!["output"]!.AsObject().ContainsKey("voice"));
    }

    [Fact]
    public void TheToolsAreTheAgentThePointerAndTheDesktopActions()
    {
        var openApp = new JsonObject { ["type"] = "function", ["name"] = "open_app" };
        var update = RealtimeVoiceClient.BuildSessionUpdate(RealtimeTurnMode.PushToTalk, "x", null, "en", [openApp]);
        var names = update["session"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["send_to_agent", "point_at", "open_app"], names);
        Assert.Equal("auto", update["session"]!["tool_choice"]!.GetValue<string>());
        Assert.Equal("session.update", update["type"]!.GetValue<string>());
        var pointRequired = update["session"]!["tools"]![1]!["parameters"]!["required"]!.AsArray().Select(node => node!.GetValue<string>());
        Assert.Equal(["x", "y", "label", "text"], pointRequired);
    }
}

/// <summary>Issue #1: nothing may go out on the realtime socket before the server's first event.</summary>
public class RealtimeSessionHandshakeOrderTests : IDisposable
{
    private readonly SerialMainThread mainThread = new();

    public void Dispose() => mainThread.Dispose();

    private Task<RealtimeVoiceClient> MakeClientAsync() => mainThread.InvokeAsync(() =>
        new RealtimeVoiceClient(mainThread, new RealtimeAudio(() => new FakeAudioCapture(), () => new FakeStreamingPcmPlayer())));

    private Task StartSessionAsync(RealtimeVoiceClient client, FakeSpeechWebSocket socket, RealtimeTurnMode mode) =>
        mainThread.RunAsync(() => client.StartSessionAsync(socket, RealtimeVoiceClient.RealtimeSocketUrl("gpt-realtime"),
            new Dictionary<string, string> { ["Authorization"] = "Bearer ek_test" }, "gpt-realtime", mode, "be brief", () => Task.CompletedTask));

    [Fact]
    public async Task SessionUpdateWaitsForTheServersFirstEvent()
    {
        var client = await MakeClientAsync();
        var socket = new FakeSpeechWebSocket();
        var session = StartSessionAsync(client, socket, RealtimeTurnMode.PushToTalk);
        Assert.True(await Eventually.TrueAsync(() => socket.IsWaitingToReceive));
        Assert.True(socket.WasConnected);
        Assert.Equal("Bearer ek_test", socket.ConnectedHeaders!["Authorization"]);
        Assert.False(client.IsConnected);

        socket.Open("""{"type":"session.created"}""");
        await session;

        Assert.True(await Eventually.TrueAsync(() => socket.SentTexts.Count > 0));
        Assert.Equal(0, socket.SendsBeforeOpen);
        var firstEvent = JsonNode.Parse(socket.SentTexts[0])!.AsObject();
        Assert.Equal("session.update", firstEvent["type"]!.GetValue<string>());
        Assert.True(client.IsConnected);
        await mainThread.InvokeAsync(() => client.Disconnect(null));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ARefusedHandshakeSaysWhatWasRefusedAndSendsNothing()
    {
        var client = await MakeClientAsync();
        var socket = new FakeSpeechWebSocket();
        var session = StartSessionAsync(client, socket, RealtimeTurnMode.AlwaysOn);
        Assert.True(await Eventually.TrueAsync(() => socket.IsWaitingToReceive));
        socket.Refuse(401);

        var error = await Assert.ThrowsAsync<RealtimeVoiceException>(() => session);
        Assert.Contains("401", error.Message);
        Assert.Equal(0, socket.SendsBeforeOpen);
        Assert.Empty(socket.SentTexts);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ASocketLostUnderATurnSaysSoOnce()
    {
        var client = await MakeClientAsync();
        var lost = new List<string>();
        await mainThread.InvokeAsync(() => client.OnConnectionLost = lost.Add);
        var socket = new FakeSpeechWebSocket();
        var session = StartSessionAsync(client, socket, RealtimeTurnMode.PushToTalk);
        Assert.True(await Eventually.TrueAsync(() => socket.IsWaitingToReceive));
        socket.Open("""{"type":"session.created"}""");
        await session;
        socket.CloseFromServer(1011, "server error");
        Assert.True(await Eventually.TrueAsync(() => lost.Count == 1));
        Assert.Contains("1011", lost[0]);
        Assert.False(await mainThread.InvokeAsync(() => client.IsConnected));
    }
}

/// <summary>A connected client driven through a fake socket: the push-to-talk turn and the point_at chain.</summary>
public class RealtimeTurnTests : IDisposable
{
    private readonly SerialMainThread mainThread = new();

    public void Dispose() => mainThread.Dispose();

    private sealed class FakeTextRecognizer(IReadOnlyList<RecognizedTextLine> lines) : IScreenTextRecognizer
    {
        public bool IsAvailable(string? languageTag = null) => true;
        public Task<IReadOnlyList<RecognizedTextLine>> RecognizeAsync(byte[] image, double upscale = 2, string? languageTag = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(lines);
    }

    private static CompanionScreenCapture Capture() => new(
        [0xFF, 0xD8], "image/jpeg", "user's screen (cursor is here)", true, 1280, 800,
        new PixelRect(0, 0, 2560, 1600), 2.0, @"\\.\DISPLAY1", 1280, 800);

    private async Task<(RealtimeVoiceClient Client, FakeSpeechWebSocket Socket, FakeAudioCapture Microphone)> ConnectedClientAsync(IScreenTextRecognizer? recognizer = null)
    {
        var microphone = new FakeAudioCapture();
        var client = await mainThread.InvokeAsync(() =>
            new RealtimeVoiceClient(mainThread, new RealtimeAudio(() => microphone, () => new FakeStreamingPcmPlayer()), recognizer, new FakeMicrophonePermission(MicrophoneConsent.Allowed)));
        var socket = new FakeSpeechWebSocket();
        var session = mainThread.RunAsync(() => client.StartSessionAsync(socket, RealtimeVoiceClient.RealtimeSocketUrl("gpt-realtime"),
            new Dictionary<string, string>(), "gpt-realtime", RealtimeTurnMode.PushToTalk, "be brief", () => Task.CompletedTask));
        Assert.True(await Eventually.TrueAsync(() => socket.IsWaitingToReceive));
        socket.Open("""{"type":"session.created"}""");
        await session;
        return (client, socket, microphone);
    }

    private static IEnumerable<string> SentTypes(FakeSpeechWebSocket socket) =>
        socket.SentTexts.Select(text => JsonNode.Parse(text)!["type"]!.GetValue<string>());

    [Fact]
    public async Task ATapWithoutAudioIsNotSentAndSaysItHeardNothing()
    {
        var (client, socket, _) = await ConnectedClientAsync();
        var heardNothing = 0;
        await mainThread.InvokeAsync(() =>
        {
            client.OnTurnHeardNothing = () => heardNothing++;
            client.BeginPushToTalk();
            client.EndPushToTalk();
        });
        Assert.True(await Eventually.TrueAsync(() => heardNothing == 1));
        Assert.DoesNotContain("input_audio_buffer.commit", SentTypes(socket));
        Assert.DoesNotContain("response.create", SentTypes(socket));
    }

    [Fact]
    public async Task AHeldTurnStreamsTheMicrophoneThenCommitsWithTheScreenAndAsksForTheReply()
    {
        var (client, socket, microphone) = await ConnectedClientAsync();
        await mainThread.InvokeAsync(() =>
        {
            client.ScreenContextProvider = () => Task.FromResult<RealtimeScreenContext?>(new RealtimeScreenContext([1, 2, 3], "pointer at (10, 20)", Capture()));
            client.BeginPushToTalk();
        });
        Assert.True(await Eventually.TrueAsync(() => microphone.IsCapturing && client.IsCapturing));
        Assert.Equal(24_000, microphone.LastOptions!.SampleRate);
        Assert.True(microphone.LastOptions.PreferEchoCancellation);
        // 0.2 s of speech.
        microphone.Push(TestAudio.Tone(4_800, sampleRate: 24_000));
        Assert.True(await Eventually.TrueAsync(() => SentTypes(socket).Contains("input_audio_buffer.append")));
        await mainThread.InvokeAsync(client.EndPushToTalk);
        Assert.True(await Eventually.TrueAsync(() => SentTypes(socket).Contains("response.create")));
        var types = SentTypes(socket).ToList();
        Assert.True(types.IndexOf("conversation.item.create") < types.IndexOf("input_audio_buffer.commit"));
        Assert.True(types.IndexOf("input_audio_buffer.commit") < types.LastIndexOf("response.create"));
        var screenItem = socket.SentTexts.Select(text => JsonNode.Parse(text)!).First(node => node["type"]!.GetValue<string>() == "conversation.item.create");
        Assert.Equal("[Screen context, not spoken] pointer at (10, 20)", screenItem["item"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AQID", screenItem["item"]!["content"]![1]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task PointAtSnapsToTheElementsTextAndContinuesOnceTheResponseIsDone()
    {
        var saveLine = new RecognizedTextLine("Save", new DoubleRect(600, 400, 40, 12), [new RecognizedTextWord("Save", new DoubleRect(600, 400, 40, 12))]);
        var (client, socket, _) = await ConnectedClientAsync(new FakeTextRecognizer([saveLine]));
        var pointed = new List<(ScreenPoint Point, string Label)>();
        await mainThread.InvokeAsync(() =>
        {
            client.ScreenContextProvider = () => Task.FromResult<RealtimeScreenContext?>(new RealtimeScreenContext([1], "screen", Capture()));
            client.OnPointAt = (point, label, _) => pointed.Add((point, label));
        });
        await mainThread.RunAsync(() => client.SendTextTurnAsync("where is save?"));
        socket.Receive("""{"type":"response.created","response":{"id":"resp_1"}}""");
        socket.Receive("""{"type":"response.function_call_arguments.done","response_id":"resp_1","call_id":"call_1","name":"point_at","arguments":"{\"x\":\"660\",\"y\":420,\"label\":\"save button\",\"text\":\"Save\"}"}""");
        Assert.True(await Eventually.TrueAsync(() => pointed.Count == 1));
        Assert.Equal("save button", pointed[0].Label);
        Assert.Equal(new ScreenPoint(620, 406), pointed[0].Point);
        Assert.True(await Eventually.TrueAsync(() => socket.SentTexts.Any(text => text.Contains("function_call_output"))));
        var output = socket.SentTexts.Select(text => JsonNode.Parse(text)!).Last(node => node["type"]!.GetValue<string>() == "conversation.item.create");
        Assert.Equal("function_call_output", output["item"]!["type"]!.GetValue<string>());
        Assert.Equal("pointing at save button", output["item"]!["output"]!.GetValue<string>());

        // The continuation waits for response.done, and is sent once.
        var createsBefore = SentTypes(socket).Count(type => type == "response.create");
        socket.Receive("""{"type":"response.done","response":{"status":"completed"}}""");
        Assert.True(await Eventually.TrueAsync(() => SentTypes(socket).Count(type => type == "response.create") == createsBefore + 1));
    }

    [Fact]
    public async Task ToolCallsFromACancelledResponseNeverArmAContinuation()
    {
        var (client, socket, _) = await ConnectedClientAsync();
        var responsesStarted = 0;
        await mainThread.InvokeAsync(() =>
        {
            client.OnAgentTask = task => Task.FromResult($"did {task}");
            client.OnResponseStarted = () => responsesStarted++;
        });
        socket.Receive("""{"type":"response.created","response":{"id":"resp_1"}}""");
        Assert.True(await Eventually.TrueAsync(() => responsesStarted == 1));
        await mainThread.InvokeAsync(client.StopSpeaking);
        Assert.True(await Eventually.TrueAsync(() => SentTypes(socket).Contains("response.cancel")));
        socket.Receive("""{"type":"response.function_call_arguments.done","response_id":"resp_1","call_id":"c","name":"send_to_agent","arguments":"{\"task\":\"x\"}"}""");
        socket.Receive("""{"type":"response.done","response":{"status":"cancelled"}}""");
        Assert.True(await Eventually.TrueAsync(() => SentTypes(socket).Contains("conversation.item.create")));
        await Task.Delay(100);
        Assert.DoesNotContain("response.create", SentTypes(socket));
    }

    [Fact]
    public async Task InvalidCoordinatesAndAMissingScreenshotAreAnsweredInWords()
    {
        var (_, socket, _) = await ConnectedClientAsync();
        socket.Receive("""{"type":"response.function_call_arguments.done","call_id":"a","name":"point_at","arguments":"{\"x\":\"NaN\",\"y\":1,\"label\":\"x\",\"text\":\"\"}"}""");
        socket.Receive("""{"type":"response.function_call_arguments.done","call_id":"b","name":"point_at","arguments":"{\"x\":5,\"y\":1,\"label\":\"\",\"text\":\"\"}"}""");
        Assert.True(await Eventually.TrueAsync(() => socket.SentTexts.Count(text => text.Contains("function_call_output")) == 2));
        var outputs = socket.SentTexts.Where(text => text.Contains("function_call_output")).Select(text => JsonNode.Parse(text)!["item"]!["output"]!.GetValue<string>()).ToList();
        Assert.Equal("invalid coordinates: point_at needs finite integer x and y", outputs[0]);
        Assert.Equal("no screenshot attached; describe the location in words", outputs[1]);
    }
}

public class RealtimeScreenTextTests
{
    [Fact]
    public void ACharacterRangeGetsTheBoxOfTheWordsItCovers()
    {
        var line = new RecognizedTextLine("Open File Menu", new DoubleRect(0, 0, 140, 10),
        [
            new RecognizedTextWord("Open", new DoubleRect(0, 0, 40, 10)),
            new RecognizedTextWord("File", new DoubleRect(50, 1, 30, 9)),
            new RecognizedTextWord("Menu", new DoubleRect(90, 0, 50, 10)),
        ]);
        var converted = RealtimeScreenText.ToScreenTextLine(line);
        Assert.Equal(new ScreenRect(50, 1, 30, 9), converted.BoxFor(5, 4));
        Assert.Equal(new ScreenRect(0, 0, 80, 10), converted.BoxFor(0, 9));
    }
}
