// Ports SpeechVoices.swift: who speaks the answers. The person picks in Settings → Clicky → Voice
// from what they have: the free voice on an account, a voice key (OpenAI, ElevenLabs, Google Cloud,
// Amazon Polly, Sarvam), Kokoro (an open model downloaded only when picked), or this PC's own voice.
// Automatic takes the first that can speak, own keys first. Every cloud request goes straight to
// its vendor from this PC; none of these keys is ever sent to the OpenClicky backend. Request
// building is pure so it is tested without a network (SpeechVoicesTests).

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

/// <summary>shell.json's `speechVoice`. "mac" stays the stored value of this PC's voice, so shell.json is interchangeable between platforms.</summary>
public enum SpeechVoiceChoice
{
    [RawValue("automatic")] Automatic,
    [RawValue("account")] Account,
    [RawValue("openai")] OpenAI,
    [RawValue("elevenlabs")] ElevenLabs,
    [RawValue("google")] Google,
    [RawValue("gemini")] Gemini,
    [RawValue("polly")] Polly,
    [RawValue("sarvam")] Sarvam,
    [RawValue("kokoro")] Kokoro,
    [RawValue("mac")] Mac,
}

public static class SpeechVoiceChoices
{
    public static IReadOnlyList<SpeechVoiceChoice> AllCases { get; } = Enum.GetValues<SpeechVoiceChoice>();

    /// <summary>shell.json's `speechVoice`; null, "auto" and the older "cloud" are Automatic.</summary>
    public static SpeechVoiceChoice FromStored(string? stored) =>
        RawValues.FromRawValue<SpeechVoiceChoice>(stored) ?? SpeechVoiceChoice.Automatic;

    /// <summary>The value written to shell.json: null for Automatic.</summary>
    public static string? StoredValue(this SpeechVoiceChoice choice) =>
        choice == SpeechVoiceChoice.Automatic ? null : choice.RawValue();

    public static string Title(this SpeechVoiceChoice choice) => choice switch
    {
        SpeechVoiceChoice.Automatic => "Automatic",
        SpeechVoiceChoice.Account => "Free voice (ElevenLabs)",
        SpeechVoiceChoice.OpenAI => "OpenAI",
        SpeechVoiceChoice.ElevenLabs => "ElevenLabs (your key)",
        SpeechVoiceChoice.Google => "Google Cloud",
        SpeechVoiceChoice.Gemini => "Google Gemini",
        SpeechVoiceChoice.Polly => "Amazon Polly",
        SpeechVoiceChoice.Sarvam => "Sarvam",
        SpeechVoiceChoice.Kokoro => "Kokoro (on this PC)",
        _ => "This PC",
    };

    /// <summary>
    /// Automatic's order: own keys first (the person chose to pay for them), then Kokoro once it is
    /// downloaded (free, offline, and it saves the free ElevenLabs characters for those without
    /// it), then the free voice, then this PC. Gemini comes last of the keys: its voice takes about
    /// four seconds a sentence.
    /// </summary>
    public static IReadOnlyList<SpeechVoiceChoice> AutomaticOrder { get; } =
    [
        SpeechVoiceChoice.OpenAI, SpeechVoiceChoice.ElevenLabs, SpeechVoiceChoice.Google, SpeechVoiceChoice.Polly,
        SpeechVoiceChoice.Sarvam, SpeechVoiceChoice.Gemini, SpeechVoiceChoice.Kokoro, SpeechVoiceChoice.Account, SpeechVoiceChoice.Mac,
    ];

    private static bool Has(string? key) => !string.IsNullOrWhiteSpace(key);

    /// <summary>Whether this voice can speak right now in <paramref name="languageCode"/> with these settings.</summary>
    public static bool CanSpeak(SpeechVoiceChoice choice, OpenClickyShellSettings settings, bool hasAccount, bool kokoroInstalled, string languageCode) => choice switch
    {
        SpeechVoiceChoice.Automatic => false,
        SpeechVoiceChoice.Account => hasAccount,
        SpeechVoiceChoice.OpenAI => Has(settings.OpenaiApiKey),
        SpeechVoiceChoice.ElevenLabs => Has(settings.ElevenLabsApiKey),
        SpeechVoiceChoice.Google => Has(settings.GoogleCloudApiKey),
        SpeechVoiceChoice.Gemini => Has(settings.GeminiApiKey),
        SpeechVoiceChoice.Polly => AWSCredentials.TryParse(settings.AmazonPollyCredentials) is not null && CloudVoiceRequests.PollyVoice(languageCode) is not null,
        SpeechVoiceChoice.Sarvam => Has(settings.SarvamKey) && SarvamSpeechWire.SpeechLanguageCode(languageCode) is not null,
        SpeechVoiceChoice.Kokoro => kokoroInstalled && KokoroVoice.SpeakerFor(languageCode, null) is not null,
        _ => true,
    };

    /// <summary>
    /// The voices the picker offers: Automatic, every voice there is a key or account for, Kokoro
    /// (picking it downloads it), and this PC. The one already picked stays listed even if its key
    /// went, so the picker never shows a blank.
    /// </summary>
    public static IReadOnlyList<SpeechVoiceChoice> Offered(OpenClickyShellSettings settings, bool hasAccount, string languageCode)
    {
        var picked = FromStored(settings.SpeechVoice);
        return AllCases.Where(choice => choice switch
        {
            SpeechVoiceChoice.Automatic or SpeechVoiceChoice.Mac => true,
            SpeechVoiceChoice.Kokoro => choice == picked || KokoroVoice.SpeakerFor(languageCode, null) is not null,
            _ => choice == picked || CanSpeak(choice, settings, hasAccount, kokoroInstalled: false, languageCode),
        }).ToList();
    }

    /// <summary>
    /// The voice that speaks now: the one picked if it can, else the account's free voice, else this
    /// PC's (a missing key or a Kokoro still downloading: this PC's voice sounds robotic next to the
    /// free one, 2026-10-28); Automatic, the first in <see cref="AutomaticOrder"/> that can. Kokoro in a
    /// language it doesn't speak (it became the voice on its own, then the language changed) goes
    /// on as Automatic.
    /// </summary>
    public static SpeechVoiceChoice Resolve(OpenClickyShellSettings settings, bool hasAccount, bool kokoroInstalled, string languageCode)
    {
        var picked = FromStored(settings.SpeechVoice);
        var kokoroCantSpeakLanguage = picked == SpeechVoiceChoice.Kokoro && KokoroVoice.SpeakerFor(languageCode, null) is null;
        IReadOnlyList<SpeechVoiceChoice> candidates = picked == SpeechVoiceChoice.Automatic || kokoroCantSpeakLanguage
            ? AutomaticOrder
            : [picked, SpeechVoiceChoice.Account];
        foreach (var candidate in candidates)
        {
            if (CanSpeak(candidate, settings, hasAccount, kokoroInstalled, languageCode)) return candidate;
        }
        return SpeechVoiceChoice.Mac;
    }

    /// <summary><see cref="Resolve"/> with this PC's settings.</summary>
    public static SpeechVoiceChoice Current(SpeechVoiceServices? services = null) =>
        Resolve(OpenClickyConfiguration.Settings, OpenClickyConfiguration.HasAccountToken,
                (services ?? SpeechVoiceServices.Current)?.KokoroIsInstalled() ?? false, ReplyLanguage.CurrentCode);
}

/// <summary>
/// The local voices CloudVoiceSpeaker hands work to: Kokoro (the helper) and this PC's voice.
/// Wired (AppComposition): the app sets <see cref="Current"/> = ForThisPC(platform ISystemVoice) at startup, after KokoroVoice.Shared
/// is assigned.
/// </summary>
public sealed record SpeechVoiceServices(
    Func<bool> KokoroIsInstalled,
    Func<string, string, CancellationToken, Task<byte[]>> KokoroAudio,
    ISystemVoice SystemVoice)
{
    public static SpeechVoiceServices? Current { get; set; }

    /// <summary>The live services: KokoroVoice.Shared (once the app assigned it) and this PC's voice.</summary>
    public static SpeechVoiceServices ForThisPC(ISystemVoice systemVoice) => new(
        () => KokoroVoice.IsInstalled,
        (text, languageCode, cancellationToken) =>
            (KokoroVoice.Shared ?? throw new KokoroException(KokoroException.NotInstalledMessage)).AudioAsync(text, languageCode, cancellationToken),
        systemVoice);

    public static SpeechVoiceServices Required =>
        Current ?? throw new InvalidOperationException("the voices aren't set up in this build yet.");
}

// MARK: - Amazon credentials

/// <summary>
/// Amazon Polly's credentials as one pasted line, "accessKeyId:secretKey" or
/// "accessKeyId:secretKey:region" (us-east-1 when left out). Secret keys may contain "/" and "+",
/// never ":".
/// </summary>
public sealed record AWSCredentials(string AccessKeyId, string SecretAccessKey, string Region)
{
    public static AWSCredentials? TryParse(string? stored)
    {
        var parts = (stored ?? "").Trim().Split(':', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).ToArray();
        if (parts.Length is not (2 or 3) || parts[0].Length == 0 || parts[1].Length == 0) return null;
        var region = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : "us-east-1";
        return new AWSCredentials(parts[0], parts[1], region);
    }

    /// <summary>Never prints the secret.</summary>
    public override string ToString() => $"AWSCredentials({AccessKeyId}, ***, {Region})";
}

/// <summary>AWS Signature Version 4 for one request (Polly's SynthesizeSpeech), from
/// docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html.</summary>
public static class AWSSignatureV4
{
    public static void Sign(HttpRequestMessage request, AWSCredentials credentials, string service, DateTimeOffset date)
    {
        if (request.RequestUri is not { } url || string.IsNullOrEmpty(url.Host)) return;
        var utc = date.UtcDateTime;
        var amzDate = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var body = request.Content is null ? [] : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var payloadHash = Hex(SHA256.HashData(body));
        request.SetHeader("X-Amz-Date", amzDate);

        // Every header set on the request is signed, lowercased and sorted, plus host.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}" };
        foreach (var header in request.Headers)
            headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value).Trim();
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                // Content-Length is filled in by the client later; it was never set by the request builder.
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value).Trim();
            }
        }
        headers.Remove("authorization");
        var canonicalHeaders = string.Concat(headers.Select(pair => $"{pair.Key}:{pair.Value}\n"));
        var signedHeaders = string.Join(";", headers.Keys);
        var path = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;
        var canonicalRequest = string.Join("\n", request.Method.Method, path, CanonicalQuery(url), canonicalHeaders, signedHeaders, payloadHash);

        var scope = $"{dateStamp}/{credentials.Region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Encoding.UTF8.GetBytes($"AWS4{credentials.SecretAccessKey}");
        foreach (var part in new[] { dateStamp, credentials.Region, service, "aws4_request" })
            key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        var signature = Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
        request.SetHeader("Authorization", $"AWS4-HMAC-SHA256 Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static string CanonicalQuery(Uri url)
    {
        var query = url.Query.TrimStart('?');
        if (query.Length == 0) return "";
        return string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Contains('=') ? item : item + "=")
            .OrderBy(item => item, StringComparer.Ordinal));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

// MARK: - Requests

public static class CloudVoiceRequests
{
    // MARK: ElevenLabs (the person's own key)

    /// <summary>Sarah, a premade voice: the free tier refuses library voices.</summary>
    public const string ElevenLabsVoiceId = "EXAVITQu4vr4xnSDxMaL";
    public const string ElevenLabsModel = "eleven_flash_v2_5";

    public static HttpRequestMessage ElevenLabsSpeech(string key, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{ElevenLabsVoiceId}?output_format=mp3_44100_128");
        request.SetHeader("xi-api-key", key);
        request.SetHeader("Accept", "audio/mpeg");
        request.Content = DirectProviderRequests.JsonContent(ElevenLabsSpeechBody(text));
        return request;
    }

    /// <summary>The ElevenLabs text-to-speech body, for an own key here and for the account's free voice
    /// (the backend's /tts relays it to ElevenLabs), so both ask for the same model and settings.</summary>
    public static byte[] ElevenLabsSpeechBody(string text) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["text"] = text,
        ["model_id"] = ElevenLabsModel,
        ["voice_settings"] = new JsonObject { ["stability"] = 0.5, ["similarity_boost"] = 0.75 },
    }.ToJsonString());

    // MARK: Google Cloud Text-to-Speech

    /// <summary>The region each reply language is spoken in, and a Neural2 voice where one is chosen; other
    /// languages get Google's default voice for the region.</summary>
    public static (string LanguageCode, string? Name) GoogleVoice(string languageCode) => languageCode switch
    {
        "en" => ("en-US", "en-US-Neural2-F"),
        "hi" => ("hi-IN", "hi-IN-Neural2-A"),
        "zh" => ("cmn-CN", null),
        "pt" => ("pt-BR", null),
        "ja" => ("ja-JP", null),
        "ko" => ("ko-KR", null),
        "bn" or "gu" or "kn" or "ml" or "mr" or "ta" or "te" or "pa" => ($"{languageCode}-IN", null),
        _ => ($"{languageCode}-{languageCode.ToUpperInvariant()}", null),
    };

    public static HttpRequestMessage GoogleSpeech(string key, string text, string languageCode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://texttospeech.googleapis.com/v1/text:synthesize");
        request.SetHeader("X-Goog-Api-Key", key);
        var voice = GoogleVoice(languageCode);
        var voiceObject = new JsonObject { ["languageCode"] = voice.LanguageCode };
        if (voice.Name is { } name) voiceObject["name"] = name;
        request.Content = DirectProviderRequests.JsonContent(new JsonObject
        {
            ["input"] = new JsonObject { ["text"] = text },
            ["voice"] = voiceObject,
            ["audioConfig"] = new JsonObject { ["audioEncoding"] = "MP3" },
        });
        return request;
    }

    /// <summary>Google answers with the MP3 as base64 in `audioContent`.</summary>
    public static byte[] DecodeGoogleSpeech(byte[] data)
    {
        var content = ReadJson(data)?["audioContent"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var audio = Base64(content);
        if (audio is null || audio.Length == 0) throw CloudVoiceException.Unreadable("Google");
        return audio;
    }

    // MARK: Google Gemini (the AI Studio key that also answers questions)

    public const string GeminiSpeechModel = "gemini-3.8-flash-tts";
    public const string GeminiVoiceName = "Kore";

    public static HttpRequestMessage GeminiSpeech(string key, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiSpeechModel}:generateContent");
        request.SetHeader("x-goog-api-key", key);
        request.Content = DirectProviderRequests.JsonContent(new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }),
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = GeminiVoiceName } },
                },
            },
        });
        return request;
    }

    private static readonly Regex RatePattern = new(@"rate=(\d+)", RegexOptions.CultureInvariant);

    /// <summary>Gemini's audio is inline base64: WAV from the 3.8 models, raw 16-bit PCM ("audio/L16;
    /// rate=24000", little-endian whatever the label says) from older ones, which is wrapped in WAV.</summary>
    public static byte[] DecodeGeminiSpeech(byte[] data)
    {
        JsonObject? inline = null;
        if (ReadJson(data)?["candidates"] is JsonArray { Count: > 0 } candidates
            && candidates[0]?["content"]?["parts"] is JsonArray parts)
        {
            inline = parts.OfType<JsonObject>().Select(part => part["inlineData"] as JsonObject).FirstOrDefault(found => found is not null);
        }
        var audio = Base64(StringOf(inline?["data"]));
        if (inline is null || audio is null || audio.Length == 0) throw CloudVoiceException.Unreadable("Gemini");
        var mimeType = (StringOf(inline["mimeType"]) ?? "").ToLowerInvariant();
        if (!mimeType.Contains("l16") && !mimeType.Contains("pcm")) return audio;
        var match = RatePattern.Match(mimeType);
        var rate = match.Success && int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : 24_000;
        return BuddyWAVFileBuilder.BuildWavData(audio, rate);
    }

    // MARK: Amazon Polly

    /// <summary>A neural Polly voice for each reply language it speaks; null where Polly has none.</summary>
    public static string? PollyVoice(string languageCode) => languageCode switch
    {
        "en" => "Joanna",
        "hi" => "Kajal",
        "fr" => "Lea",
        "de" => "Vicki",
        "es" => "Lucia",
        "it" => "Bianca",
        "ja" => "Kazuha",
        "ko" => "Seoyeon",
        "pt" => "Camila",
        "zh" => "Zhiyu",
        _ => null,
    };

    public static HttpRequestMessage PollySpeech(AWSCredentials credentials, string text, string voiceId, DateTimeOffset? date = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://polly.{credentials.Region}.amazonaws.com/v1/speech");
        request.Content = DirectProviderRequests.JsonContent(new JsonObject
        {
            ["Engine"] = "neural", ["OutputFormat"] = "mp3", ["Text"] = text, ["VoiceId"] = voiceId,
        });
        AWSSignatureV4.Sign(request, credentials, "polly", date ?? DateTimeOffset.UtcNow);
        return request;
    }

    private static JsonNode? ReadJson(byte[] data)
    {
        try { return JsonNode.Parse(data); }
        catch (JsonException) { return null; }
    }

    private static string? StringOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static byte[]? Base64(string? text)
    {
        if (text is null) return null;
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }
}

public enum CloudVoiceFailure
{
    NoKey,
    Refused,
    Unreadable,
}

public sealed class CloudVoiceException : Exception
{
    public CloudVoiceFailure Failure { get; }
    public string Vendor { get; }
    public int Status { get; }

    private CloudVoiceException(CloudVoiceFailure failure, string vendor, string message, int status = 0) : base(message)
    {
        Failure = failure;
        Vendor = vendor;
        Status = status;
    }

    public static CloudVoiceException NoKey(string vendor) => new(CloudVoiceFailure.NoKey, vendor, $"{vendor} needs a key under Settings → Keys & Plans.");

    public static CloudVoiceException Refused(string vendor, int status, string message) =>
        new(CloudVoiceFailure.Refused, vendor, $"{vendor} refused ({status}): {message}", status);

    public static CloudVoiceException Unreadable(string vendor) => new(CloudVoiceFailure.Unreadable, vendor, $"{vendor}'s answer had no audio in it.");
}

// MARK: - Speaking

/// <summary>The audio for one piece of an answer in a voice that needs no account (the account's voice goes
/// through the backend, in AnswerVoicePlayer). Shared by the answers and the keys' Test buttons.</summary>
public static class CloudVoiceSpeaker
{
    public static async Task<byte[]> AudioAsync(
        string text,
        SpeechVoiceChoice voice,
        OpenClickyShellSettings? settings = null,
        string? languageCode = null,
        SpeechVoiceServices? services = null,
        CancellationToken cancellationToken = default,
        HttpClient? client = null)
    {
        settings ??= OpenClickyConfiguration.Settings;
        languageCode ??= ReplyLanguage.CurrentCode;
        static string Key(string? value, string vendor)
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.Length == 0) throw CloudVoiceException.NoKey(vendor);
            return trimmed;
        }
        switch (voice)
        {
            case SpeechVoiceChoice.OpenAI:
                return await SendAsync(DirectProviderRequests.OpenAISpeech(Key(settings.OpenaiApiKey, "OpenAI"), text), "OpenAI", client, cancellationToken).ConfigureAwait(false);
            case SpeechVoiceChoice.ElevenLabs:
                return await SendAsync(CloudVoiceRequests.ElevenLabsSpeech(Key(settings.ElevenLabsApiKey, "ElevenLabs"), text), "ElevenLabs", client, cancellationToken).ConfigureAwait(false);
            case SpeechVoiceChoice.Google:
            {
                var data = await SendAsync(CloudVoiceRequests.GoogleSpeech(Key(settings.GoogleCloudApiKey, "Google"), text, languageCode), "Google", client, cancellationToken).ConfigureAwait(false);
                return CloudVoiceRequests.DecodeGoogleSpeech(data);
            }
            case SpeechVoiceChoice.Gemini:
            {
                var data = await SendAsync(CloudVoiceRequests.GeminiSpeech(Key(settings.GeminiApiKey, "Gemini"), text), "Gemini", client, cancellationToken).ConfigureAwait(false);
                return CloudVoiceRequests.DecodeGeminiSpeech(data);
            }
            case SpeechVoiceChoice.Polly:
            {
                var credentials = AWSCredentials.TryParse(settings.AmazonPollyCredentials) ?? throw CloudVoiceException.NoKey("Amazon Polly");
                var voiceId = CloudVoiceRequests.PollyVoice(languageCode) ?? throw CloudVoiceException.Unreadable("Amazon Polly");
                return await SendAsync(CloudVoiceRequests.PollySpeech(credentials, text, voiceId), "Amazon Polly", client, cancellationToken).ConfigureAwait(false);
            }
            case SpeechVoiceChoice.Sarvam:
            {
                var speechLanguage = SarvamSpeechWire.SpeechLanguageCode(languageCode) ?? throw CloudVoiceException.Unreadable("Sarvam");
                var key = Key(settings.SarvamKey, "Sarvam");
                return await SarvamSpeechWire.Required.SpeechAsync(key, text, speechLanguage, cancellationToken).ConfigureAwait(false);
            }
            case SpeechVoiceChoice.Kokoro:
                return await (services ?? SpeechVoiceServices.Required).KokoroAudio(text, languageCode, cancellationToken).ConfigureAwait(false);
            default:
                return await new SystemVoice((services ?? SpeechVoiceServices.Required).SystemVoice).AudioAsync(text, settings, languageCode, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> SendAsync(HttpRequestMessage request, string vendor, HttpClient? client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await (client ?? HttpClients.Shared).SendAsync(request, timeout.Token).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300)
            throw CloudVoiceException.Refused(vendor, status, Encoding.UTF8.GetString(data, 0, Math.Min(300, data.Length)));
        if (data.Length == 0) throw CloudVoiceException.Unreadable(vendor);
        return data;
    }
}
