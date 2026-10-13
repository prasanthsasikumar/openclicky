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

    /// <summary>Answer through a plan the person already pays for (Claude Code / Codex). Off until switched on.</summary>
    [JsonPropertyName("useClaudeCodePlan")] public bool? UseClaudeCodePlan { get; set; }
    [JsonPropertyName("useCodexPlan")] public bool? UseCodexPlan { get; set; }

    /// <summary>Who answers questions: null or "auto" picks in ProviderRouter's order; else an AnswerSource raw value.</summary>
    [JsonPropertyName("answerSource")] public string? AnswerSource { get; set; }

    /// <summary>Model overrides per provider ("openrouter": "anthropic/claude-sonnet-4.6").</summary>
    [JsonPropertyName("answerModels")] public Dictionary<string, string>? AnswerModels { get; set; }

    /// <summary>Who speaks answers (SpeechVoiceChoice raw value); null, "auto" or "cloud" picks in order.</summary>
    [JsonPropertyName("speechVoice")] public string? SpeechVoice { get; set; }

    /// <summary>Voice-only keys: ElevenLabs, Google Cloud TTS, Amazon Polly ("accessKeyId:secretKey[:region]").</summary>
    [JsonPropertyName("elevenLabsApiKey")] public string? ElevenLabsApiKey { get; set; }
    [JsonPropertyName("googleCloudApiKey")] public string? GoogleCloudApiKey { get; set; }
    [JsonPropertyName("amazonPollyCredentials")] public string? AmazonPollyCredentials { get; set; }

    /// <summary>The Kokoro speaker picked in Settings; null → the language's default.</summary>
    [JsonPropertyName("kokoroSpeaker")] public int? KokoroSpeaker { get; set; }

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
