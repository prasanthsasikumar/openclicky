// Ports RealtimeWireDialect.swift. The realtime client speaks OpenAI's Realtime protocol: it sends
// `session.update`, `input_audio_buffer.append`, `response.create`… and handles `response.created`,
// `response.output_audio.delta`, `response.function_call_arguments.done`, `response.done`…
// A dialect sits between that and the socket, so another vendor's live API runs the same turns,
// tools, pointing and barge-in: OpenAI's is the identity; Gemini Live's translates both ways.
//
// Everything here is pure (JSON in, JSON out) and unit-tested in RealtimeWireDialectTests.
// The Swift protocol is IRealtimeWireDialect here (C#'s interface naming).

using System.Text.Json.Nodes;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

/// <summary>Who answers the fast voice lane and hands-free. Raw values are shell.json's `realtimeProvider`.</summary>
public enum RealtimeVoiceProvider
{
    [RawValue("openai")] OpenAI,
    [RawValue("gemini")] Gemini,
}

public static class RealtimeVoiceProviders
{
    public static string DisplayName(this RealtimeVoiceProvider provider) => provider switch
    {
        RealtimeVoiceProvider.Gemini => "Gemini",
        _ => "OpenAI",
    };

    /// <summary>
    /// The provider the next connection uses, with how it pays: an own key goes straight to its
    /// vendor; with no realtime key, an account's token gets OpenAI through the backend.
    /// <paramref name="preferred"/> is the person's pick (null: automatic, OpenAI first, its prompts
    /// and pointing were tuned on it). A pick without its key falls back to automatic.
    /// </summary>
    public static RealtimeVoiceProvider? Choose(ProviderCredentials credentials, RealtimeVoiceProvider? preferred)
    {
        bool Usable(RealtimeVoiceProvider provider) => provider switch
        {
            RealtimeVoiceProvider.Gemini => credentials.GeminiKey is not null,
            _ => credentials.OpenAIKey is not null || credentials.AccountToken is not null,
        };
        if (preferred is { } picked && Usable(picked)) return picked;
        if (credentials.OpenAIKey is not null) return RealtimeVoiceProvider.OpenAI;
        if (credentials.GeminiKey is not null) return RealtimeVoiceProvider.Gemini;
        if (credentials.AccountToken is not null) return RealtimeVoiceProvider.OpenAI;
        return null;
    }

    /// <summary>Whether any own key can run realtime voice (an account alone is decided by its plan).</summary>
    public static bool HasOwnKey(ProviderCredentials credentials) => credentials.OpenAIKey is not null || credentials.GeminiKey is not null;
}

/// <summary>
/// Translates between the client's OpenAI-shaped events and one vendor's wire messages.
/// Used on the main thread only, like the client that owns it.
/// </summary>
public interface IRealtimeWireDialect
{
    /// <summary>For log lines and the sentences a person sees ("the connection to Gemini dropped").</summary>
    string ProviderName { get; }

    /// <summary>OpenAI greets a new socket first (`session.created`); Gemini says nothing until the
    /// client's setup message, so the client sends it before waiting for the handshake.</summary>
    bool ClientSpeaksFirst { get; }

    /// <summary>Hands-free: the vendor replies on its own once the person stops, without `response.create`
    /// (so the screen goes up when speech starts instead of when the utterance is committed).</summary>
    bool RespondsWithoutRequest { get; }

    /// <summary>A connection is replaced before this age, ahead of the vendor's own cap.</summary>
    TimeSpan MaximumConnectionAge { get; }

    /// <summary>The wire messages for one client event (none when the vendor has no equivalent).</summary>
    IReadOnlyList<JsonObject> Outgoing(JsonObject clientEvent);

    /// <summary>The client events for one wire message.</summary>
    IReadOnlyList<JsonObject> Incoming(JsonObject message);
}

/// <summary>OpenAI's own protocol: nothing to translate.</summary>
public sealed class OpenAIWireDialect : IRealtimeWireDialect
{
    public string ProviderName => "OpenAI";
    public bool ClientSpeaksFirst => false;
    public bool RespondsWithoutRequest => false;
    /// <summary>OpenAI cuts a session at 60 minutes.</summary>
    public TimeSpan MaximumConnectionAge => TimeSpan.FromMinutes(50);
    public IReadOnlyList<JsonObject> Outgoing(JsonObject clientEvent) => [clientEvent];
    public IReadOnlyList<JsonObject> Incoming(JsonObject message) => [message];
}

/// <summary>
/// Gemini Live (BidiGenerateContent). Checked against gemini-3.8-live on 2026-10-28 (on the Mac):
/// every server message arrives as a binary frame of JSON; `setupComplete` answers the setup;
/// `voiceActivity` reports ACTIVITY_START / ACTIVITY_END in both turn modes; a tool call is followed
/// by `generationComplete` + `turnComplete`, and the `toolResponse` alone makes the model go on
/// (a new turn of audio), which maps onto OpenAI's response.done → output → response.create.
/// </summary>
public sealed class GeminiLiveDialect(int sampleRate, string? resumptionHandle, string model = GeminiLiveDialect.DefaultModel) : IRealtimeWireDialect
{
    public const string DefaultModel = "gemini-3.8-live";
    public static readonly Uri SocketUrl = new("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");

    public string ProviderName => "Gemini";
    public bool ClientSpeaksFirst => true;
    public bool RespondsWithoutRequest => true;
    /// <summary>Gemini closes a connection after about ten minutes (it says so with `goAway` first);
    /// the session itself carries on through the resumption handle.</summary>
    public TimeSpan MaximumConnectionAge => TimeSpan.FromMinutes(9);

    public string Model { get; } = model;
    public int SampleRate { get; } = sampleRate;
    /// <summary>The latest handle Gemini offered; a new connection given it continues this conversation.</summary>
    public string? ResumptionHandle { get; private set; } = resumptionHandle;

    /// <summary>Push-to-talk turns are marked by the client (activityStart / activityEnd); hands-free
    /// leaves them to Gemini's voice activity detection.</summary>
    public bool ManualTurns { get; private set; } = true;
    private bool setupSent;
    private string sentInstructions = "";
    /// <summary>Push-to-talk audio is held until there is enough for a turn (the client's own 100 ms
    /// rule), so a tap that the client then clears never opens a turn Gemini would answer.</summary>
    private readonly List<string> heldAudio = [];
    private int heldAudioBytes;
    private bool activityOpen;
    /// <summary>Hands-free: between Gemini's ACTIVITY_START and ACTIVITY_END.</summary>
    private bool personIsSpeaking;
    /// <summary>Screen context and typed text not sent yet: they go out as one `clientContent` turn.</summary>
    private readonly List<JsonObject> pendingParts = [];
    private bool responseOpen;
    private int responseCount;
    /// <summary>After `response.cancel` the rest of that turn is dropped, up to its `interrupted` or
    /// `turnComplete`: Gemini has no cancel, so its audio would otherwise keep playing.</summary>
    private bool discardingTurn;
    private string userTranscript = "";
    private readonly Dictionary<string, string> callNames = [];
    /// <summary>Calls of a turn that was cancelled or moved on from: their outputs are not sent, since a
    /// `toolResponse` alone would make Gemini carry on talking about a turn the person left.</summary>
    private readonly HashSet<string> abandonedCalls = [];
    private readonly HashSet<string> openCalls = [];

    private string CurrentResponseId => $"gemini-response-{responseCount}";

    // MARK: Client → Gemini

    public IReadOnlyList<JsonObject> Outgoing(JsonObject clientEvent)
    {
        switch (SpeechJson.String(clientEvent["type"]))
        {
            case "session.update":
                return SessionUpdate(clientEvent["session"] as JsonObject ?? []);
            case "input_audio_buffer.append":
            {
                if (SpeechJson.String(clientEvent["audio"]) is not { } audio) return [];
                if (!ManualTurns || activityOpen) return [AudioMessage(audio)];
                heldAudio.Add(audio);
                heldAudioBytes += DecodedLength(audio);
                return heldAudioBytes >= MinimumTurnBytes ? OpenActivity() : [];
            }
            case "input_audio_buffer.clear":
                heldAudio.Clear();
                heldAudioBytes = 0;
                AbandonOpenCalls();
                return [];
            case "input_audio_buffer.commit":
            {
                if (!ManualTurns) return [];
                var messages = activityOpen ? [] : OpenActivity();
                messages.Add(new JsonObject { ["realtimeInput"] = new JsonObject { ["activityEnd"] = new JsonObject() } });
                activityOpen = false;
                return messages;
            }
            case "conversation.item.create":
                return ConversationItem(clientEvent["item"] as JsonObject ?? []);
            case "response.create":
            {
                var messages = new List<JsonObject>();
                if (SpeechJson.String((clientEvent["response"] as JsonObject)?["instructions"]) is { Length: > 0 } instructions)
                    pendingParts.Add(new JsonObject { ["text"] = $"[Say this now, in your own voice] {instructions}" });
                if (pendingParts.Count > 0) messages.Add(ClientContent(turnComplete: true));
                // Otherwise the reply is already coming: activityEnd, Gemini's own turn detection
                // or a toolResponse each start one.
                return messages;
            }
            case "response.cancel":
                if (responseOpen)
                {
                    responseOpen = false;
                    discardingTurn = true;
                }
                AbandonOpenCalls();
                return [];
            default:
                return [];
        }
    }

    private int MinimumTurnBytes => SampleRate / 10 * sizeof(short);

    /// <summary>The byte count of a base64 string without decoding it.</summary>
    private static int DecodedLength(string base64)
    {
        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0;
        return Math.Max(0, base64.Length / 4 * 3 - padding);
    }

    private List<JsonObject> OpenActivity()
    {
        activityOpen = true;
        userTranscript = "";
        AbandonOpenCalls();
        var messages = new List<JsonObject> { new() { ["realtimeInput"] = new JsonObject { ["activityStart"] = new JsonObject() } } };
        messages.AddRange(heldAudio.Select(AudioMessage));
        heldAudio.Clear();
        heldAudioBytes = 0;
        return messages;
    }

    private JsonObject AudioMessage(string base64) => new()
    {
        ["realtimeInput"] = new JsonObject { ["audio"] = new JsonObject { ["data"] = base64, ["mimeType"] = $"audio/pcm;rate={SampleRate}" } },
    };

    private void AbandonOpenCalls()
    {
        abandonedCalls.UnionWith(openCalls);
        openCalls.Clear();
    }

    private IReadOnlyList<JsonObject> SessionUpdate(JsonObject session)
    {
        var instructions = SpeechJson.String(session["instructions"]) ?? "";
        if (setupSent)
        {
            // A live session's setup is fixed; changed instructions (another app in front, a skill
            // switched on) go in as context the model reads from here on.
            if (instructions == sentInstructions) return [];
            sentInstructions = instructions;
            return
            [
                new JsonObject
                {
                    ["clientContent"] = new JsonObject
                    {
                        ["turns"] = new JsonArray(new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray(new JsonObject { ["text"] = $"[Updated instructions; follow these from now on, do not answer this] {instructions}" }),
                        }),
                        ["turnComplete"] = false,
                    },
                },
            ];
        }
        setupSent = true;
        sentInstructions = instructions;
        var input = (session["audio"] as JsonObject)?["input"] as JsonObject;
        var turnDetection = input?["turn_detection"] as JsonObject;
        ManualTurns = turnDetection is null;
        var activityDetection = new JsonObject { ["disabled"] = ManualTurns };
        if (SpeechJson.Int(turnDetection?["silence_duration_ms"]) is { } silence) activityDetection["silenceDurationMs"] = silence;
        var declarations = new JsonArray();
        foreach (var tool in (session["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (FunctionDeclaration(tool) is { } declaration) declarations.Add(declaration);
        }
        var setup = new JsonObject
        {
            ["model"] = $"models/{Model}",
            ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("AUDIO") },
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instructions }) },
            ["realtimeInputConfig"] = new JsonObject { ["automaticActivityDetection"] = activityDetection },
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject(),
            // Without compression an audio-and-screenshots session is cut after a few minutes.
            ["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() },
            ["sessionResumption"] = ResumptionHandle is { } handle ? new JsonObject { ["handle"] = handle } : new JsonObject(),
        };
        if (declarations.Count > 0) setup["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        return [new JsonObject { ["setup"] = setup }];
    }

    /// <summary>An OpenAI function tool as a Gemini function declaration. Gemini refuses an object schema
    /// with no properties, so a tool without arguments (stop_listening) is declared without one.</summary>
    public static JsonObject? FunctionDeclaration(JsonObject tool)
    {
        if ((SpeechJson.String(tool["type"]) ?? "function") != "function" || SpeechJson.String(tool["name"]) is not { } name) return null;
        var declaration = new JsonObject { ["name"] = name };
        if (SpeechJson.String(tool["description"]) is { } description) declaration["description"] = description;
        if (tool["parameters"] is JsonObject parameters && parameters["properties"] is JsonObject { Count: > 0 })
            declaration["parameters"] = GeminiAnswerProvider.SchemaGeminiAccepts(parameters);
        return declaration;
    }

    private IReadOnlyList<JsonObject> ConversationItem(JsonObject item)
    {
        switch (SpeechJson.String(item["type"]))
        {
            case "function_call_output":
            {
                var callId = SpeechJson.String(item["call_id"]) ?? "";
                openCalls.Remove(callId);
                if (abandonedCalls.Contains(callId) || !callNames.TryGetValue(callId, out var name)) return [];
                // Context added since (the screen after an action) goes first, so the model's
                // continuation already sees it.
                var messages = pendingParts.Count == 0 ? new List<JsonObject>() : [ClientContent(turnComplete: false)];
                messages.Add(new JsonObject
                {
                    ["toolResponse"] = new JsonObject
                    {
                        ["functionResponses"] = new JsonArray(new JsonObject
                        {
                            ["id"] = callId,
                            ["name"] = name,
                            ["response"] = new JsonObject { ["output"] = SpeechJson.String(item["output"]) ?? "" },
                        }),
                    },
                });
                return messages;
            }
            case "message":
            {
                var parts = (item["content"] as JsonArray ?? []).OfType<JsonObject>().Select(Part).OfType<JsonObject>().ToList();
                // While the person is talking the screen belongs to that turn, so it streams in with
                // the audio; otherwise it waits for the next response.create.
                if (activityOpen || (!ManualTurns && personIsSpeaking))
                {
                    return parts.Select(part => part["inlineData"] is JsonObject inline
                        ? new JsonObject { ["realtimeInput"] = new JsonObject { ["video"] = inline.DeepClone() } }
                        : new JsonObject { ["realtimeInput"] = new JsonObject { ["text"] = SpeechJson.String(part["text"]) ?? "" } }).ToList();
                }
                pendingParts.AddRange(parts);
                return [];
            }
            default:
                return [];
        }
    }

    /// <summary>One OpenAI content part as a Gemini part: text as text, a data-URL image as inline data.</summary>
    public static JsonObject? Part(JsonObject content)
    {
        switch (SpeechJson.String(content["type"]))
        {
            case "input_text":
                return SpeechJson.String(content["text"]) is { } text ? new JsonObject { ["text"] = text } : null;
            case "input_image":
            {
                if (SpeechJson.String(content["image_url"]) is not { } url || !url.StartsWith("data:", StringComparison.Ordinal)) return null;
                var comma = url.IndexOf(',');
                if (comma < 0) return null;
                var header = url[5..comma];
                var mimeType = header.Split(';')[0];
                if (mimeType.Length == 0) mimeType = "image/jpeg";
                return new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mimeType, ["data"] = url[(comma + 1)..] } };
            }
            default:
                return null;
        }
    }

    private JsonObject ClientContent(bool turnComplete)
    {
        var parts = new JsonArray(pendingParts.Select(part => (JsonNode)part).ToArray());
        pendingParts.Clear();
        return new JsonObject
        {
            ["clientContent"] = new JsonObject
            {
                ["turns"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
                ["turnComplete"] = turnComplete,
            },
        };
    }

    // MARK: Gemini → client

    public IReadOnlyList<JsonObject> Incoming(JsonObject message)
    {
        var events = new List<JsonObject>();
        if (message.ContainsKey("setupComplete")) events.Add(Event("session.created"));
        if (message["sessionResumptionUpdate"] is JsonObject update && SpeechJson.Bool(update["resumable"]) == true
            && SpeechJson.String(update["newHandle"]) is { Length: > 0 } handle)
            ResumptionHandle = handle;
        if (message["goAway"] is JsonObject goAway)
        {
            var goAwayEvent = Event("session.go_away");
            goAwayEvent["time_left"] = SpeechJson.String(goAway["timeLeft"]) ?? "";
            events.Add(goAwayEvent);
        }
        if (SpeechJson.String((message["voiceActivity"] as JsonObject)?["type"]) is { } activity && !ManualTurns)
        {
            switch (activity)
            {
                case "ACTIVITY_START":
                    personIsSpeaking = true;
                    userTranscript = "";
                    events.Add(Event("input_audio_buffer.speech_started"));
                    break;
                case "ACTIVITY_END":
                    personIsSpeaking = false;
                    events.Add(Event("input_audio_buffer.speech_stopped"));
                    break;
            }
        }
        if (message["serverContent"] is JsonObject content) events.AddRange(ServerContent(content));
        if ((message["toolCall"] as JsonObject)?["functionCalls"] is JsonArray calls && !discardingTurn)
        {
            events.AddRange(OpenResponseIfNeeded());
            foreach (var call in calls.OfType<JsonObject>())
            {
                if (SpeechJson.String(call["id"]) is not { } id || SpeechJson.String(call["name"]) is not { } name) continue;
                callNames[id] = name;
                openCalls.Add(id);
                var arguments = (call["args"] as JsonObject)?.ToJsonString(SpeechJson.CompactOptions) ?? "{}";
                var callEvent = Event("response.function_call_arguments.done");
                callEvent["call_id"] = id;
                callEvent["name"] = name;
                callEvent["arguments"] = arguments;
                callEvent["response_id"] = CurrentResponseId;
                events.Add(callEvent);
            }
        }
        if ((message["toolCallCancellation"] as JsonObject)?["ids"] is JsonArray ids)
        {
            foreach (var id in ids.Select(SpeechJson.String).OfType<string>())
            {
                abandonedCalls.Add(id);
                openCalls.Remove(id);
            }
        }
        return events;
    }

    private List<JsonObject> ServerContent(JsonObject content)
    {
        var events = new List<JsonObject>();
        if (SpeechJson.String((content["inputTranscription"] as JsonObject)?["text"]) is { } heard) userTranscript += heard;
        if (SpeechJson.Bool(content["interrupted"]) == true)
        {
            if (discardingTurn)
            {
                discardingTurn = false;
            }
            else if (responseOpen)
            {
                responseOpen = false;
                events.Add(ResponseDone("cancelled"));
            }
            return events;
        }
        if (!discardingTurn)
        {
            foreach (var part in ((content["modelTurn"] as JsonObject)?["parts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (SpeechJson.Bool(part["thought"]) == true || part["inlineData"] is not JsonObject inline
                    || !(SpeechJson.String(inline["mimeType"]) ?? "").StartsWith("audio/", StringComparison.Ordinal)
                    || SpeechJson.String(inline["data"]) is not { } data) continue;
                events.AddRange(OpenResponseIfNeeded());
                var audioEvent = Event("response.output_audio.delta");
                audioEvent["delta"] = data;
                events.Add(audioEvent);
            }
            if (SpeechJson.String((content["outputTranscription"] as JsonObject)?["text"]) is { Length: > 0 } said)
            {
                events.AddRange(OpenResponseIfNeeded());
                var transcriptEvent = Event("response.output_audio_transcript.delta");
                transcriptEvent["delta"] = said;
                events.Add(transcriptEvent);
            }
        }
        var generationDone = SpeechJson.Bool(content["generationComplete"]) == true;
        var turnDone = SpeechJson.Bool(content["turnComplete"]) == true;
        if (generationDone || turnDone)
        {
            if (discardingTurn)
            {
                if (turnDone) discardingTurn = false;
            }
            else
            {
                events.AddRange(UserTranscriptEvent());
                if (responseOpen)
                {
                    responseOpen = false;
                    events.Add(Event("response.output_audio_transcript.done"));
                    events.Add(ResponseDone("completed"));
                }
            }
        }
        return events;
    }

    private List<JsonObject> OpenResponseIfNeeded()
    {
        if (responseOpen) return [];
        responseOpen = true;
        responseCount += 1;
        var events = UserTranscriptEvent();
        var created = Event("response.created");
        created["response"] = new JsonObject { ["id"] = CurrentResponseId };
        events.Add(created);
        return events;
    }

    /// <summary>What the person said, once per turn, before the reply to it.</summary>
    private List<JsonObject> UserTranscriptEvent()
    {
        var transcript = userTranscript.Trim();
        userTranscript = "";
        if (transcript.Length == 0) return [];
        var completed = Event("conversation.item.input_audio_transcription.completed");
        completed["transcript"] = transcript;
        return [completed];
    }

    private JsonObject ResponseDone(string status)
    {
        var done = Event("response.done");
        done["response"] = new JsonObject { ["id"] = CurrentResponseId, ["status"] = status };
        return done;
    }

    private static JsonObject Event(string type) => new() { ["type"] = type };
}
