//
//  PictureMakers.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  A picture the drawing asks to have made (photo({generate})): the drawing model describes it, and the
//  person's own image model makes it (Settings → Clicky → Drawings → Pictures come from). Only the
//  description is sent, never the screen.
//
//  The sources, each with the key the person set for it:
//    - OpenAI: POST /v1/images/generations (gpt-image-1 by default).
//    - Google Gemini: POST /v1beta/models/{model}:generateContent, responseModalities IMAGE and TEXT.
//    - OpenRouter: POST /api/v1/chat/completions with modalities image and text, the picture as a data URL.
//  A failure makes nothing and is logged once, without the key.
//

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Plugins.DrawStage;

/// <summary>Who makes a picture the drawing asks for (Settings → Clicky → Drawings → Pictures come from).</summary>
public enum PictureSourceKind
{
    Off,
    OpenAI,
    Gemini,
    OpenRouter,
}

/// <summary>Makes the picture a drawing asks for, with the image model Settings picks; null when it can't.</summary>
public sealed class PictureMakers(Func<OpenClickyShellSettings> settings, Func<ProviderCredentials> credentials, HttpClient? client = null)
{
    public const string DefaultOpenAIModel = "gpt-image-1";
    public const string DefaultGeminiModel = "gemini-2.5-flash-image";
    public const string DefaultOpenRouterModel = "google/gemini-2.5-flash-image";

    /// <summary>shell.json's `pictureSource`; unset or unknown is off.</summary>
    public static PictureSourceKind KindFrom(string? raw) => raw switch
    {
        "openAI" => PictureSourceKind.OpenAI,
        "gemini" => PictureSourceKind.Gemini,
        "openRouter" => PictureSourceKind.OpenRouter,
        _ => PictureSourceKind.Off,
    };

    /// <summary>The value shell.json keeps for a source; null for off (left out of the file).</summary>
    public static string? RawValue(PictureSourceKind kind) => kind switch
    {
        PictureSourceKind.OpenAI => "openAI",
        PictureSourceKind.Gemini => "gemini",
        PictureSourceKind.OpenRouter => "openRouter",
        _ => null,
    };

    /// <summary>The model a source uses when no model is picked.</summary>
    public static string DefaultModel(PictureSourceKind kind) => kind switch
    {
        PictureSourceKind.OpenAI => DefaultOpenAIModel,
        PictureSourceKind.Gemini => DefaultGeminiModel,
        PictureSourceKind.OpenRouter => DefaultOpenRouterModel,
        _ => "",
    };

    /// <summary>The picture of <paramref name="description"/>, or null when no source is set, its key is missing or it
    /// couldn't make one (which is logged).</summary>
    public async Task<DrawStagePhoto?> MakeAsync(string description, CancellationToken cancellationToken)
    {
        var chosen = settings();
        var kind = KindFrom(chosen.PictureSource);
        if (kind == PictureSourceKind.Off) return null;
        var model = OpenClickyConfiguration.Cleaned(chosen.PictureModel) ?? DefaultModel(kind);
        var keys = credentials();
        var key = kind switch
        {
            PictureSourceKind.OpenAI => keys.OpenAIKey,
            PictureSourceKind.Gemini => keys.GeminiKey,
            _ => keys.OpenRouterKey,
        };
        if (key is null)
        {
            LogFailure(kind, model, "no key");
            return null;
        }
        var prompt = description.Trim() + ", clear, well lit, no text unless asked";
        try
        {
            return kind switch
            {
                PictureSourceKind.OpenAI => await FromOpenAIAsync(prompt, model, key, cancellationToken).ConfigureAwait(false),
                PictureSourceKind.Gemini => await FromGeminiAsync(prompt, model, key, cancellationToken).ConfigureAwait(false),
                _ => await FromOpenRouterAsync(prompt, model, key, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or FormatException or InvalidOperationException
            || (error is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFailure(kind, model, $"{error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private async Task<DrawStagePhoto?> FromOpenAIAsync(string prompt, string model, string key, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["size"] = "1024x1024",
            ["quality"] = "medium",
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await (client ?? HttpClients.Shared).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return await NothingAsync(PictureSourceKind.OpenAI, model, response).ConfigureAwait(false);
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var base64 = reply?["data"]?[0]?["b64_json"]?.GetValue<string>();
        return base64 is null ? Nothing(PictureSourceKind.OpenAI, model) : Photo(Convert.FromBase64String(base64), null, model);
    }

    private async Task<DrawStagePhoto?> FromGeminiAsync(string prompt, string model, string key, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("IMAGE", "TEXT") },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        using var response = await (client ?? HttpClients.Shared).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return await NothingAsync(PictureSourceKind.Gemini, model, response).ConfigureAwait(false);
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        // The picture is the first part that holds image data (a part may be text).
        if (reply?["candidates"]?[0]?["content"]?["parts"]?.AsArray() is not { } parts) return Nothing(PictureSourceKind.Gemini, model);
        foreach (var part in parts)
        {
            if (part?["inlineData"] is not JsonObject inline) continue;
            var base64 = inline["data"]?.GetValue<string>();
            if (string.IsNullOrEmpty(base64)) continue;
            var mediaType = inline["mimeType"]?.GetValue<string>();
            return Photo(Convert.FromBase64String(base64), mediaType?.StartsWith("image/") == true ? mediaType : null, model);
        }
        return Nothing(PictureSourceKind.Gemini, model);
    }

    private async Task<DrawStagePhoto?> FromOpenRouterAsync(string prompt, string model, string key, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["modalities"] = new JsonArray("image", "text"),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await (client ?? HttpClients.Shared).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return await NothingAsync(PictureSourceKind.OpenRouter, model, response).ConfigureAwait(false);
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var dataUrl = reply?["choices"]?[0]?["message"]?["images"]?[0]?["image_url"]?["url"]?.GetValue<string>();
        return dataUrl is not null && DataUrlBytes(dataUrl) is { } picture
            ? Photo(picture.Data, picture.MediaType, model)
            : Nothing(PictureSourceKind.OpenRouter, model);
    }

    /// <summary>A data URL's media type and bytes; null when it isn't one.</summary>
    private static (byte[] Data, string? MediaType)? DataUrlBytes(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (!dataUrl.StartsWith("data:", StringComparison.Ordinal) || comma < 0) return null;
        var header = dataUrl[5..comma];
        var mediaType = header.EndsWith(";base64", StringComparison.Ordinal) ? header[..^7] : header;
        if (!mediaType.StartsWith("image/", StringComparison.Ordinal)) mediaType = null;
        return (Convert.FromBase64String(dataUrl[(comma + 1)..]), mediaType);
    }

    private static DrawStagePhoto Photo(byte[] data, string? mediaType, string model) =>
        new(data, mediaType ?? DrawStageMessages.MediaTypeOf(data), $"Made with {model}");

    private static DrawStagePhoto? Nothing(PictureSourceKind kind, string model)
    {
        LogFailure(kind, model, "no image in the reply");
        return null;
    }

    private static async Task<DrawStagePhoto?> NothingAsync(PictureSourceKind kind, string model, HttpResponseMessage response)
    {
        LogFailure(kind, model, $"{(int)response.StatusCode}: {await VendorMessageAsync(response).ConfigureAwait(false)}");
        return null;
    }

    /// <summary>What the vendor said went wrong (its JSON error message), else the start of the body.</summary>
    private static async Task<string> VendorMessageAsync(HttpResponseMessage response)
    {
        var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
        try
        {
            if (JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } message) return message;
        }
        catch (JsonException)
        {
        }
        body = body.ReplaceLineEndings(" ");
        return body.Length > 200 ? body[..200] : body;
    }

    private static void LogFailure(PictureSourceKind kind, string model, string reason) =>
        AppLog.Write($"picture: {RawValue(kind)} {model} failed ({reason})");
}
