// The models a self-hosted source serves (Ollama, LM Studio, the person's own OpenAI-compatible server), read
// from its OpenAI-compatible `GET /v1/models`, which all of them answer. Settings lists them to pick from, and a
// source with no model picked answers with the first one, preferring one that sees images (questions carry a
// screenshot). Nothing here leaves the person's own address.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClicky.Core.Providers;

public static class LocalModels
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Names that usually mean a model sees images (llava, gemma3, qwen2.5-vl, llama3.2-vision, minicpm-v…).</summary>
    private static readonly string[] VisionHints = ["llava", "vision", "-vl", "vl-", "gemma3", "gemma-3", "minicpm-v", "pixtral", "moondream", "bakllava", "qwen2.5vl", "granite3.2-vision", "mistral-small3.1"];

    /// <summary>Whether a model's name suggests it reads images.</summary>
    public static bool LooksLikeItSeesImages(string model) =>
        VisionHints.Any(hint => model.Contains(hint, StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids in a `/v1/models` reply, in its order; empty when it isn't one.</summary>
    public static IReadOnlyList<string> ParseModelList(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["data"] is not JsonArray data) return [];
            return data.Select(entry => entry?["id"] is JsonValue id && id.TryGetValue<string>(out var name) ? name : null)
                .OfType<string>().Where(name => name.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The model a source answers with when none is picked: the first that sees images, else the first.</summary>
    public static string? DefaultModel(IReadOnlyList<string> models) =>
        models.FirstOrDefault(LooksLikeItSeesImages) ?? models.FirstOrDefault();

    /// <summary>The models served at <paramref name="baseUrl"/> ("http://localhost:11434"). Throws when the server can't be
    /// reached or refuses, with a sentence fit for Settings.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(string baseUrl, string? apiKey = null, HttpClient? client = null,
                                                            CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/v1/models");
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey.Trim()}");
        HttpResponseMessage response;
        try
        {
            response = await (client ?? HttpClients.Shared).SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new LocalModelsException($"nothing answered at {baseUrl}. is it running?");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new LocalModelsException($"{baseUrl} said {(int)response.StatusCode} when asked for its models.");
            var models = ParseModelList(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            if (models.Count == 0) throw new LocalModelsException($"{baseUrl} has no models yet. download one first (ollama pull gemma3).");
            return models;
        }
    }
}

/// <summary>A self-hosted source that could not list its models; the message is the sentence to show.</summary>
public sealed class LocalModelsException(string sentence) : Exception(sentence);
