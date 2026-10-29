// Ports RealtimeWireDialectTests.swift: the Gemini Live translation, against the message shapes
// gemini-3.8-live sent on 2026-10-28 (checked on the Mac), then — through a fake socket — a Gemini
// session on the Windows client: the setup goes first, and binary frames are read.

using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Settings;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class RealtimeWireDialectTests
{
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    private static GeminiLiveDialect Gemini(JsonNode? turnDetection = null)
    {
        var dialect = new GeminiLiveDialect(24_000, resumptionHandle: null);
        dialect.Outgoing(new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["instructions"] = "be brief",
                ["tools"] = Json("""
                    {"t":[
                      {"type":"function","name":"open_app","description":"Open an app.",
                       "parameters":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}},
                      {"type":"function","name":"stop_listening","description":"Stop.",
                       "parameters":{"type":"object","properties":{},"required":[]}}
                    ]}
                    """)["t"]!.DeepClone(),
                ["audio"] = new JsonObject { ["input"] = new JsonObject { ["turn_detection"] = turnDetection } },
            },
        });
        return dialect;
    }

    /// <summary>100 ms of 24 kHz PCM16 is 4,800 bytes.</summary>
    private static JsonObject Audio(int bytes) => new() { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(new byte[bytes]) };

    private static List<string> Types(IEnumerable<JsonObject> events) =>
        events.Select(serverEvent => serverEvent["type"]?.GetValue<string>()).OfType<string>().ToList();

    [Fact]
    public void ChoosesOpenAIFirstThenGeminiThenTheAccount()
    {
        var both = new ProviderCredentials("sk", null, null, geminiKey: "g");
        Assert.Equal(RealtimeVoiceProvider.OpenAI, RealtimeVoiceProviders.Choose(both, null));
        Assert.Equal(RealtimeVoiceProvider.Gemini, RealtimeVoiceProviders.Choose(both, RealtimeVoiceProvider.Gemini));
        var geminiOnly = new ProviderCredentials(null, null, null, geminiKey: "g");
        Assert.Equal(RealtimeVoiceProvider.Gemini, RealtimeVoiceProviders.Choose(geminiOnly, RealtimeVoiceProvider.OpenAI));
        var account = new ProviderCredentials(null, null, "t");
        Assert.Equal(RealtimeVoiceProvider.OpenAI, RealtimeVoiceProviders.Choose(account, RealtimeVoiceProvider.Gemini));
        Assert.Null(RealtimeVoiceProviders.Choose(new ProviderCredentials(null, null, null), null));
    }

    [Fact]
    public void AGeminiKeyAloneTurnsOnRealtime()
    {
        Assert.True(new AccountCapabilities(AccountKind.SignedOut, OwnRealtimeKey: true).UsesRealtime);
        Assert.False(new AccountCapabilities(AccountKind.SignedOut).UsesRealtime);
        var geminiOnly = new OpenClickyShellSettings { GeminiApiKey = "g" };
        Assert.True(AccountCapabilities.Current(geminiOnly, Preferences.InMemory()).UsesRealtime);
    }

    [Fact]
    public void ThePickedProviderIsReadFromShellJson()
    {
        Assert.Equal("gemini", SettingsCatalog.RealtimeProviderChoice("gemini"));
        Assert.Equal("openai", SettingsCatalog.RealtimeProviderChoice("openai"));
        Assert.Equal("auto", SettingsCatalog.RealtimeProviderChoice(null));
        Assert.Equal("auto", SettingsCatalog.RealtimeProviderChoice("grok"));
        var decoded = OpenClickyShellSettings.Decode("""{"realtimeProvider":"gemini"}""");
        Assert.Equal("gemini", decoded.RealtimeProvider);
        Assert.Equal(RealtimeVoiceProvider.Gemini, RawValues.FromRawValue<RealtimeVoiceProvider>(decoded.RealtimeProvider));
        Assert.DoesNotContain("realtimeProvider", new OpenClickyShellSettings().Encode());
    }

    [Fact]
    public void TheProviderPickerShowsOnlyWhenBothCanRun()
    {
        static SettingsCatalog Catalog(OpenClickyShellSettings shell) =>
            new(new DictationSettings(Preferences.InMemory()), () => shell, () => AccountCapabilities.Current(shell, Preferences.InMemory()));
        static bool ShowsPicker(OpenClickyShellSettings shell) =>
            Catalog(shell).PageItems(DictationSettingsPage.Voice).Any(item => item.Id == "clicky.realtimeProvider");

        Assert.False(ShowsPicker(new OpenClickyShellSettings { GeminiApiKey = "g" }));
        Assert.False(ShowsPicker(new OpenClickyShellSettings { OpenaiApiKey = "sk" }));
        Assert.True(ShowsPicker(new OpenClickyShellSettings { GeminiApiKey = "g", OpenaiApiKey = "sk" }));
        Assert.True(ShowsPicker(new OpenClickyShellSettings { GeminiApiKey = "g", Token = "t" }));
        // A Gemini key alone gets the fast voice switches instead of the way to a key.
        var geminiOnly = Catalog(new OpenClickyShellSettings { GeminiApiKey = "g" }).PageItems(DictationSettingsPage.Voice);
        Assert.Contains(geminiOnly, item => item.Id == "clicky.fastVoice");
        Assert.DoesNotContain(geminiOnly, item => item.Id == "clicky.fastVoiceNeedsKey");
    }

    [Fact]
    public void SetupCarriesInstructionsToolsAndManualTurns()
    {
        var dialect = new GeminiLiveDialect(24_000, resumptionHandle: "h1");
        var messages = dialect.Outgoing(Json("""
            {"type":"session.update","session":{"instructions":"be brief",
              "tools":[{"type":"function","name":"stop_listening","parameters":{"type":"object","properties":{}}}],
              "audio":{"input":{"turn_detection":null}}}}
            """));
        var setup = Assert.IsType<JsonObject>(messages[0]["setup"]);
        Assert.Equal("models/gemini-3.8-live", setup["model"]!.GetValue<string>());
        Assert.True(setup["realtimeInputConfig"]!["automaticActivityDetection"]!["disabled"]!.GetValue<bool>());
        Assert.Equal("h1", setup["sessionResumption"]!["handle"]!.GetValue<string>());
        var declarations = Assert.IsType<JsonArray>(setup["tools"]![0]!["functionDeclarations"]);
        // Gemini refuses an object schema without properties.
        Assert.Null(declarations[0]!["parameters"]);
        // The same instructions again send nothing; new ones go in as context.
        Assert.Empty(dialect.Outgoing(Json("""{"type":"session.update","session":{"instructions":"be brief"}}""")));
        var update = dialect.Outgoing(Json("""{"type":"session.update","session":{"instructions":"use skill X"}}"""));
        Assert.False(update[0]["clientContent"]!["turnComplete"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolSchemasLoseTheKeywordsGeminiRejects()
    {
        var declaration = GeminiLiveDialect.FunctionDeclaration(Json("""
            {"type":"function","name":"open_app","parameters":{"type":"object","additionalProperties":false,
              "properties":{"name":{"type":"string"}}}}
            """));
        Assert.NotNull(declaration);
        Assert.Null(declaration["parameters"]!["additionalProperties"]);
        Assert.Equal("string", declaration["parameters"]!["properties"]!["name"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void HandsFreeLeavesTurnsToGemini()
    {
        var dialect = Gemini(new JsonObject { ["type"] = "server_vad", ["silence_duration_ms"] = 600 });
        Assert.False(dialect.ManualTurns);
        // Audio streams straight through, with no activity markers.
        var sent = dialect.Outgoing(Audio(960));
        Assert.Single(sent);
        Assert.Equal("audio/pcm;rate=24000", sent[0]["realtimeInput"]!["audio"]!["mimeType"]!.GetValue<string>());
        Assert.Equal(["input_audio_buffer.speech_started"], Types(dialect.Incoming(Json("""{"voiceActivity":{"type":"ACTIVITY_START"}}"""))));
        // While the person talks, the screen streams in with the audio.
        var screen = dialect.Outgoing(Json("""
            {"type":"conversation.item.create","item":{"type":"message","role":"user","content":[
              {"type":"input_text","text":"[Screen context, not spoken] pointer at 1,2"},
              {"type":"input_image","image_url":"data:image/jpeg;base64,AAAA"}]}}
            """));
        Assert.Equal(2, screen.Count);
        Assert.Equal("image/jpeg", screen[1]["realtimeInput"]!["video"]!["mimeType"]!.GetValue<string>());
        // response.create has nothing to add: Gemini replies on its own.
        Assert.Empty(dialect.Outgoing(Json("""{"type":"response.create"}""")));
    }

    [Fact]
    public void PushToTalkHoldsATapUntilThereIsEnoughAudio()
    {
        var dialect = Gemini();
        Assert.Empty(dialect.Outgoing(Json("""{"type":"input_audio_buffer.clear"}""")));
        Assert.Empty(dialect.Outgoing(Audio(2_400)));
        // A tap the client clears never opened a turn.
        Assert.Empty(dialect.Outgoing(Json("""{"type":"input_audio_buffer.clear"}""")));
        Assert.Empty(dialect.Outgoing(Audio(2_400)));
        var opened = dialect.Outgoing(Audio(2_400));
        Assert.NotNull(opened[0]["realtimeInput"]!["activityStart"]);
        Assert.Equal(3, opened.Count);
        Assert.Single(dialect.Outgoing(Audio(960)));
        var ended = dialect.Outgoing(Json("""{"type":"input_audio_buffer.commit"}"""));
        Assert.NotNull(ended[^1]["realtimeInput"]!["activityEnd"]);
    }

    [Fact]
    public void ATypedTurnGoesOutAsOneClientContentTurn()
    {
        var dialect = Gemini();
        Assert.Empty(dialect.Outgoing(Json("""
            {"type":"conversation.item.create","item":{"type":"message","content":[
              {"type":"input_text","text":"[Screen context] x"},{"type":"input_image","image_url":"data:image/jpeg;base64,AAAA"}]}}
            """)));
        Assert.Empty(dialect.Outgoing(Json("""
            {"type":"conversation.item.create","item":{"type":"message","content":[{"type":"input_text","text":"what is this?"}]}}
            """)));
        var turn = Assert.IsType<JsonObject>(dialect.Outgoing(Json("""{"type":"response.create"}"""))[0]["clientContent"]);
        Assert.True(turn["turnComplete"]!.GetValue<bool>());
        var parts = turn["turns"]![0]!["parts"]!.AsArray();
        Assert.Equal(3, parts.Count);
        Assert.Equal("AAAA", parts[1]!["inlineData"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void AReplyBecomesTheOpenAIResponseLifecycle()
    {
        var dialect = Gemini();
        Assert.Equal(["session.created"], Types(dialect.Incoming(Json("""{"setupComplete":{}}"""))));
        Assert.Empty(dialect.Incoming(Json("""{"serverContent":{"inputTranscription":{"text":"open safari"}}}""")));
        var audioEvents = dialect.Incoming(Json("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AAAA"}}]}}}"""));
        Assert.Equal(["conversation.item.input_audio_transcription.completed", "response.created", "response.output_audio.delta"], Types(audioEvents));
        Assert.Equal("open safari", audioEvents[0]["transcript"]!.GetValue<string>());
        Assert.Equal(["response.output_audio_transcript.delta"], Types(dialect.Incoming(Json("""{"serverContent":{"outputTranscription":{"text":"Done."}}}"""))));
        Assert.Equal(["response.output_audio_transcript.done", "response.done"], Types(dialect.Incoming(Json("""{"serverContent":{"generationComplete":true}}"""))));
        // turnComplete after generationComplete closes nothing twice.
        Assert.Empty(dialect.Incoming(Json("""{"serverContent":{"turnComplete":true}}""")));
    }

    [Fact]
    public void AToolCallIsAnsweredWithAToolResponseNamedLikeTheCall()
    {
        var dialect = Gemini();
        var events = dialect.Incoming(Json("""{"toolCall":{"functionCalls":[{"id":"call_1","name":"open_app","args":{"name":"Safari"}}]}}"""));
        Assert.Equal(["response.created", "response.function_call_arguments.done"], Types(events));
        Assert.Equal("call_1", events[^1]["call_id"]!.GetValue<string>());
        Assert.Equal("""{"name":"Safari"}""", events[^1]["arguments"]!.GetValue<string>());
        Assert.Equal(["response.output_audio_transcript.done", "response.done"], Types(dialect.Incoming(Json("""{"serverContent":{"generationComplete":true}}"""))));
        var answer = dialect.Outgoing(Json("""{"type":"conversation.item.create","item":{"type":"function_call_output","call_id":"call_1","output":"Opened Safari."}}"""));
        var response = answer[0]["toolResponse"]!["functionResponses"]![0]!;
        Assert.Equal("open_app", response["name"]!.GetValue<string>());
        Assert.Equal("Opened Safari.", response["response"]!["output"]!.GetValue<string>());
        // The continuation request adds nothing: the toolResponse already makes Gemini go on.
        Assert.Empty(dialect.Outgoing(Json("""{"type":"response.create"}""")));
    }

    [Fact]
    public void TheScreenAfterAnActionGoesBeforeTheToolResponse()
    {
        var dialect = Gemini();
        dialect.Incoming(Json("""{"toolCall":{"functionCalls":[{"id":"c","name":"open_app","args":{}}]}}"""));
        dialect.Outgoing(Json("""{"type":"conversation.item.create","item":{"type":"message","content":[{"type":"input_text","text":"[Screen context] after"}]}}"""));
        var messages = dialect.Outgoing(Json("""{"type":"conversation.item.create","item":{"type":"function_call_output","call_id":"c","output":"ok"}}"""));
        Assert.Equal(2, messages.Count);
        Assert.False(messages[0]["clientContent"]!["turnComplete"]!.GetValue<bool>());
        Assert.NotNull(messages[^1]["toolResponse"]);
    }

    [Fact]
    public void ACancelledReplyIsDroppedUntilItsTurnEnds()
    {
        var dialect = Gemini();
        dialect.Incoming(Json("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm","data":"AA"}}]}}}"""));
        dialect.Incoming(Json("""{"toolCall":{"functionCalls":[{"id":"late","name":"open_app","args":{}}]}}"""));
        Assert.Empty(dialect.Outgoing(Json("""{"type":"response.cancel"}""")));
        Assert.Empty(dialect.Incoming(Json("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm","data":"BB"}}]}}}""")));
        Assert.Empty(dialect.Incoming(Json("""{"serverContent":{"turnComplete":true}}""")));
        // An output for a call of the cancelled reply is not sent: it would make Gemini go on.
        Assert.Empty(dialect.Outgoing(Json("""{"type":"conversation.item.create","item":{"type":"function_call_output","call_id":"late","output":"x"}}""")));
        // The next reply plays again.
        Assert.Equal(["response.created", "response.output_audio.delta"],
            Types(dialect.Incoming(Json("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm","data":"CC"}}]}}}"""))));
    }

    [Fact]
    public void KeepsTheLatestResumptionHandleAndReportsGoAway()
    {
        var dialect = Gemini();
        dialect.Incoming(Json("""{"sessionResumptionUpdate":{"newHandle":"h2","resumable":true}}"""));
        dialect.Incoming(Json("""{"sessionResumptionUpdate":{"newHandle":"","resumable":false}}"""));
        Assert.Equal("h2", dialect.ResumptionHandle);
        Assert.Equal(["session.go_away"], Types(dialect.Incoming(Json("""{"goAway":{"timeLeft":"50s"}}"""))));
    }

    [Fact]
    public void ReadsAServerCloseReason()
    {
        Assert.Equal("Gemini closed the connection (1007): Invalid model", RealtimeVoiceClient.CloseDescription("Gemini", 1007, " Invalid model "));
        Assert.Equal("OpenAI closed the connection (1000)", RealtimeVoiceClient.CloseDescription("OpenAI", 1000, ""));
        Assert.Contains("Gemini refused", RealtimeVoiceClient.HandshakeFailureDescription(403, "x", "Gemini"));
    }

    [Fact]
    public void EachVendorHasItsOwnConnectionAge()
    {
        var start = DateTimeOffset.UnixEpoch;
        var gemini = new GeminiLiveDialect(24_000, null);
        Assert.True(RealtimeVoiceClient.IsTooOld(start, start + TimeSpan.FromMinutes(9), gemini.MaximumConnectionAge));
        Assert.False(RealtimeVoiceClient.IsTooOld(start, start + TimeSpan.FromMinutes(9), new OpenAIWireDialect().MaximumConnectionAge));
    }
}

/// <summary>A Gemini session on the client, through a fake socket.</summary>
public class GeminiRealtimeSessionTests : IDisposable
{
    private readonly SerialMainThread mainThread = new();

    public void Dispose() => mainThread.Dispose();

    [Fact]
    public async Task TheSetupGoesFirstAndBinaryFramesAreRead()
    {
        Assert.Equal(24_000, RealtimeAudio.SampleRate);
        var client = await mainThread.InvokeAsync(() =>
            new RealtimeVoiceClient(mainThread, new RealtimeAudio(() => new FakeAudioCapture(), () => new FakeStreamingPcmPlayer())));
        var transcripts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await mainThread.InvokeAsync(() =>
        {
            client.OnTranscript = (_, text) => transcripts.Enqueue(text);
            // What the session was opened with, so a turn sends no instructions update.
            client.InstructionsProvider = () => "be brief";
        });
        // An accepted ClientWebSocket handshake is open the moment the connect returns.
        var socket = new FakeSpeechWebSocket { OpensOnConnect = true };
        var session = mainThread.RunAsync(() => client.StartSessionAsync(socket, GeminiLiveDialect.SocketUrl,
            new Dictionary<string, string> { ["x-goog-api-key"] = "g" }, GeminiLiveDialect.DefaultModel, RealtimeTurnMode.PushToTalk,
            "be brief", () => Task.CompletedTask, new GeminiLiveDialect(RealtimeAudio.SampleRate, null)));
        // Gemini says nothing until it has the setup.
        Assert.True(await Eventually.TrueAsync(() => socket.SentTexts.Count == 1));
        var setup = JsonNode.Parse(socket.SentTexts[0])!["setup"]!;
        Assert.Equal("be brief", setup["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.False(await mainThread.InvokeAsync(() => client.IsConnected));

        socket.ReceiveBinary("""{"setupComplete":{}}""");
        await session;
        Assert.True(await mainThread.InvokeAsync(() => client.IsConnected));
        Assert.Equal("Gemini", await mainThread.InvokeAsync(() => client.ProviderName));
        // The same instructions are not sent again as an update.
        Assert.Single(socket.SentTexts);

        socket.ReceiveBinary("""{"serverContent":{"outputTranscription":{"text":"Hello there."}}}""");
        socket.ReceiveBinary("""{"serverContent":{"generationComplete":true}}""");
        Assert.True(await Eventually.TrueAsync(() => transcripts.Contains("Hello there.")));

        // A typed turn reaches Gemini as one clientContent turn, in Gemini's words.
        await mainThread.RunAsync(() => client.SendTextTurnAsync("what is this?"));
        Assert.True(await Eventually.TrueAsync(() => socket.SentTexts.Count == 2));
        var turn = JsonNode.Parse(socket.SentTexts[1])!["clientContent"]!;
        Assert.Equal("what is this?", turn["turns"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        await mainThread.InvokeAsync(() => client.Disconnect(null));
    }

    [Fact]
    public async Task ARefusedSetupSaysWhatGeminiSaid()
    {
        var client = await mainThread.InvokeAsync(() =>
            new RealtimeVoiceClient(mainThread, new RealtimeAudio(() => new FakeAudioCapture(), () => new FakeStreamingPcmPlayer())));
        var socket = new FakeSpeechWebSocket { OpensOnConnect = true };
        var session = mainThread.RunAsync(() => client.StartSessionAsync(socket, GeminiLiveDialect.SocketUrl,
            new Dictionary<string, string>(), GeminiLiveDialect.DefaultModel, RealtimeTurnMode.AlwaysOn,
            "be brief", () => Task.CompletedTask, new GeminiLiveDialect(RealtimeAudio.SampleRate, null)));
        Assert.True(await Eventually.TrueAsync(() => socket.SentTexts.Count == 1));
        socket.CloseFromServer(1007, "Invalid model");
        var error = await Assert.ThrowsAsync<RealtimeVoiceException>(() => session);
        Assert.Contains("Gemini closed the connection (1007): Invalid model", error.Message);
    }
}
