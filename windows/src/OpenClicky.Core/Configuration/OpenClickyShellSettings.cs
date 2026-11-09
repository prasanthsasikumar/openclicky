// Ports the OpenClickyShellSettings struct of OpenClickyConfiguration.swift: the fields of
// %USERPROFILE%\.openclicky\shell.json, the file the `openclicky` CLI and the Mac app share. Same
// keys, same defaults; an optional that is null is left out of the file, as Swift's encoder does.
// A field missing from the file takes its default (a short, hand-written shell.json is normal);
// a field of the wrong type fails the read, so a mistake is reported instead of quietly replaced.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClicky.Core.Configuration;

public sealed class OpenClickyShellSettings
{
    /// <summary>Command used to run the CLI, e.g. ["openclicky"] or ["node", "C:\\path\\agent\\dist\\cli.js"].</summary>
    [JsonPropertyName("cliCommand")] public List<string> CliCommand { get; set; } = ["openclicky"];

    /// <summary>The hosted backend by default; a self-hosted backend is one line (`http://localhost:8787`).</summary>
    [JsonPropertyName("backendUrl")] public string BackendUrl { get; set; } = OpenClickyConfiguration.HostedBackendUrl;

    [JsonPropertyName("token")] public string Token { get; set; } = "";

    /// <summary>Working directory for agent runs (created on demand).</summary>
    [JsonPropertyName("workspace")] public string Workspace { get; set; } = OpenClickyPaths.DefaultWorkspace;

    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("voiceSeconds")] public int VoiceSeconds { get; set; } = 5;

    /// <summary>"openai" (via the backend, default), "assemblyai", or "apple" (the offline engine).</summary>
    [JsonPropertyName("transcriptionProvider")] public string? TranscriptionProvider { get; set; }

    /// <summary>The language OpenClicky speaks and transcribes ("en", "hi"). Unset is English.</summary>
    [JsonPropertyName("language")] public string? Language { get; set; }

    /// <summary>Opt in to launching at login.</summary>
    [JsonPropertyName("registerAsLoginItem")] public bool? RegisterAsLoginItem { get; set; }

    /// <summary>
    /// Directories the agent's sandbox may write to outside the workspace. Unset leaves the agent's
    /// own default (the whole home folder); an empty list confines it to the workspace.
    /// </summary>
    [JsonPropertyName("writableRoots")] public List<string>? WritableRoots { get; set; }

    /// <summary>Composio: the MCP URL plus your `ck_…` key, sent as `x-consumer-api-key`.</summary>
    [JsonPropertyName("composioMcpUrl")] public string? ComposioMcpUrl { get; set; }
    [JsonPropertyName("composioApiKey")] public string? ComposioApiKey { get; set; }
    [JsonPropertyName("cuaDriverBin")] public string? CuaDriverBin { get; set; }

    /// <summary>Directory of app-teaching skills (`app-skills/` in the repo).</summary>
    [JsonPropertyName("appSkillsPath")] public string? AppSkillsPath { get; set; }

    /// <summary>Bring your own key: OpenAI, and optionally Anthropic for the Claude lanes.</summary>
    [JsonPropertyName("openaiApiKey")] public string? OpenaiApiKey { get; set; }
    [JsonPropertyName("anthropicApiKey")] public string? AnthropicApiKey { get; set; }

    /// <summary>Your Sarvam key: Saaras transcribes, a Sarvam model polishes. Never sent to the backend.</summary>
    [JsonPropertyName("sarvamKey")] public string? SarvamKey { get; set; }

    /// <summary>More keys that can answer questions, each straight to its vendor.</summary>
    [JsonPropertyName("openRouterApiKey")] public string? OpenRouterApiKey { get; set; }
    [JsonPropertyName("geminiApiKey")] public string? GeminiApiKey { get; set; }
    [JsonPropertyName("groqApiKey")] public string? GroqApiKey { get; set; }
    [JsonPropertyName("mistralApiKey")] public string? MistralApiKey { get; set; }
    [JsonPropertyName("xaiApiKey")] public string? XaiApiKey { get; set; }

    /// <summary>Models on this PC or the person's own server, through their OpenAI-compatible API: Ollama
    /// ("http://localhost:11434"), LM Studio ("http://localhost:1234"), and any other endpoint (vLLM, llama.cpp,
    /// LocalAI, DeepSeek, Together…) with an optional key. Each is off until its URL is set.</summary>
    [JsonPropertyName("ollamaUrl")] public string? OllamaUrl { get; set; }
    [JsonPropertyName("lmStudioUrl")] public string? LMStudioUrl { get; set; }
    [JsonPropertyName("customOpenAIUrl")] public string? CustomOpenAIUrl { get; set; }
    [JsonPropertyName("customOpenAIKey")] public string? CustomOpenAIKey { get; set; }

    /// <summary>Who writes drawn answers (the drawing stage): null uses whoever answers questions, else an
    /// AnswerSource raw value; `drawingModel` overrides that source's model for drawings only.</summary>
    [JsonPropertyName("drawingSource")] public string? DrawingSource { get; set; }
    [JsonPropertyName("drawingModel")] public string? DrawingModel { get; set; }

    /// <summary>How detailed drawings are: "simple", "detailed" (null) or "veryDetailed" (DrawStageLesson.DetailFrom).</summary>
    [JsonPropertyName("drawingDetail")] public string? DrawingDetail { get; set; }

    /// <summary>The person's own instructions for drawings, added to the drawing prompt (null: none).</summary>
    [JsonPropertyName("drawingInstructions")] public string? DrawingInstructions { get; set; }

    /// <summary>A quiz after a drawing: "off", "offer" (null: a Quiz me button on the drawing) or "always" (asks straight
    /// after every new drawing). DrawStageLesson.QuizFrom.</summary>
    [JsonPropertyName("drawingQuiz")] public string? DrawingQuiz { get; set; }

    /// <summary>The keys that start a mark from anywhere while drawn answers are on: unset is Ctrl+Alt+M, "ctrlAltShiftM",
    /// "off" (MarkerShortcuts).</summary>
    [JsonPropertyName("markerShortcut")] public string? MarkerShortcut { get; set; }

    /// <summary>Who makes a drawing's 3D pieces (PieceMakers.KindFrom: "drawingModel" when null, "llamaMeshOllama",
    /// "llamaMeshLmStudio", "hunyuan3d"), at `pieceMakerUrl` with `pieceMakerModel` (LLaMA-Mesh's name there).</summary>
    [JsonPropertyName("pieceMaker")] public string? PieceMaker { get; set; }
    [JsonPropertyName("pieceMakerUrl")] public string? PieceMakerUrl { get; set; }
    [JsonPropertyName("pieceMakerModel")] public string? PieceMakerModel { get; set; }

    /// <summary>Answer through a plan the person already pays for (Claude Code / Codex). Off until switched on.</summary>
    [JsonPropertyName("useClaudeCodePlan")] public bool? UseClaudeCodePlan { get; set; }
    [JsonPropertyName("useCodexPlan")] public bool? UseCodexPlan { get; set; }

    /// <summary>What the assistant is called ("Dyna"); null is OpenClicky (AssistantName).</summary>
    [JsonPropertyName("assistantName")] public string? AssistantName { get; set; }

    /// <summary>Who answers questions: null or "auto" picks in ProviderRouter's order; else an AnswerSource raw value.</summary>
    [JsonPropertyName("answerSource")] public string? AnswerSource { get; set; }

    /// <summary>Who runs the fast voice lane and hands-free (RealtimeVoiceProvider): null or "auto" uses
    /// OpenAI when its key is set, else Gemini; "openai" or "gemini" picks one.</summary>
    [JsonPropertyName("realtimeProvider")] public string? RealtimeProvider { get; set; }

    /// <summary>Model overrides per provider ("openrouter": "anthropic/claude-sonnet-4.6").</summary>
    [JsonPropertyName("answerModels")] public Dictionary<string, string>? AnswerModels { get; set; }

    /// <summary>Who speaks answers (SpeechVoiceChoice raw value); null, "auto" or "cloud" picks in order.</summary>
    [JsonPropertyName("speechVoice")] public string? SpeechVoice { get; set; }

    /// <summary>Voice-only keys: ElevenLabs, Google Cloud TTS, Amazon Polly ("accessKeyId:secretKey[:region]").</summary>
    [JsonPropertyName("elevenLabsApiKey")] public string? ElevenLabsApiKey { get; set; }
    [JsonPropertyName("googleCloudApiKey")] public string? GoogleCloudApiKey { get; set; }
    [JsonPropertyName("amazonPollyCredentials")] public string? AmazonPollyCredentials { get; set; }

    /// <summary>"play …": own Spotify client credentials ("clientId:clientSecret") for the song lookup; null → the backend's.</summary>
    [JsonPropertyName("spotifyCredentials")] public string? SpotifyCredentials { get; set; }

    /// <summary>Which inbox "check my email" and "draft an email" use ("gmail", "outlook"); null → automatic (Gmail).</summary>
    [JsonPropertyName("emailApp")] public string? EmailApp { get; set; }

    /// <summary>The Kokoro speaker picked in Settings; null → the language's default.</summary>
    [JsonPropertyName("kokoroSpeaker")] public int? KokoroSpeaker { get; set; }

    /// <summary>Gemini's prebuilt voice for answers and the fast voice ("Kore" when unset; CloudVoiceRequests.GeminiVoices).</summary>
    [JsonPropertyName("geminiVoice")] public string? GeminiVoice { get; set; }

    /// <summary>The font Urdu and other Arabic-script words are drawn in on the drawing stage: unset is the bundled Noto
    /// Nastaliq Urdu, "system" is Windows' own (Segoe UI), anything else names a font installed on this PC.</summary>
    [JsonPropertyName("drawingScriptFont")] public string? DrawingScriptFont { get; set; }

    /// <summary>The system voice picked in Settings (the Mac's key name, kept so the file is shared).</summary>
    [JsonPropertyName("macVoiceIdentifier")] public string? MacVoiceIdentifier { get; set; }

    /// <summary>Written by sign-in: the refresh token, when the token expires (unix seconds), whose account.</summary>
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("tokenExpiresAt")] public double? TokenExpiresAt { get; set; }
    [JsonPropertyName("accountEmail")] public string? AccountEmail { get; set; }

    /// <summary>A deep copy (Swift's struct value semantics).</summary>
    public OpenClickyShellSettings Clone()
    {
        var copy = (OpenClickyShellSettings)MemberwiseClone();
        copy.CliCommand = [.. CliCommand];
        copy.WritableRoots = WritableRoots is null ? null : [.. WritableRoots];
        copy.AnswerModels = AnswerModels is null ? null : new Dictionary<string, string>(AnswerModels);
        return copy;
    }

    // MARK: JSON

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Decodes shell.json's text. Throws JsonException for a field of the wrong kind.</summary>
    public static OpenClickyShellSettings Decode(string json)
    {
        var decoded = JsonSerializer.Deserialize<OpenClickyShellSettings>(json, JsonOptions) ?? new OpenClickyShellSettings();
        // An explicit null takes the default too (Swift's decodeIfPresent).
        var defaults = new OpenClickyShellSettings();
        decoded.CliCommand ??= defaults.CliCommand;
        decoded.BackendUrl ??= defaults.BackendUrl;
        decoded.Token ??= defaults.Token;
        decoded.Workspace ??= defaults.Workspace;
        decoded.Model ??= defaults.Model;
        return decoded;
    }

    public string Encode(bool indented = false) =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonOptions) { WriteIndented = indented });
}
