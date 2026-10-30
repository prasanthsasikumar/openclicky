//
//  TeacherPictureFinder.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherPictureFinder.swift)
//
//  When a lesson cannot be drawn (a neuron, the Taj Mahal, a real prism's rainbow), the Teacher
//  shows a real picture: Wikimedia Commons first (diagrams and photos, freely licensed, no key),
//  then the lead image of the matching Wikipedia article. Only the search words leave the PC.
//  The response parsing is pure and unit-tested; whether the downloaded bytes are an image is the
//  WPF side's to say (the decoder the caller passes in).
//

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Plugins.Teacher;

/// <summary>A downloaded picture: its bytes and pixel size, the words under it and where it came from.</summary>
/// <param name="Credit">"Wikimedia Commons" or "Wikipedia", shown under the picture.</param>
public sealed record TeacherPicture(byte[] ImageData, int PixelWidth, int PixelHeight, string Caption, string Credit);

public static class TeacherPictureFinder
{
    /// <summary>Wikimedia asks every client to name itself, with a way to reach its makers.</summary>
    public static string UserAgent =>
        $"OpenClicky-Windows/{AppBundleConfiguration.ShortVersionString.Replace(' ', '-')} (https://openclicky.org)";

    public const int ThumbnailWidth = 900;

    /// <summary>The first picture that downloads and decodes, Commons first; null when none does.</summary>
    /// <param name="imageSize">The pixel size of image bytes, null when they are not an image this PC can show.</param>
    public static async Task<TeacherPicture?> FindAsync(string query, string caption, Func<byte[], (int Width, int Height)?> imageSize,
                                                        CancellationToken cancellationToken = default)
    {
        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length == 0) return null;
        foreach (var (searchUrl, parse, credit) in new (Uri, Func<byte[], IReadOnlyList<Uri>>, string)[]
                 {
                     (CommonsSearchUrl(trimmedQuery), CommonsThumbnailUrls, "Wikimedia Commons"),
                     (WikipediaSearchUrl(trimmedQuery), WikipediaThumbnailUrls, "Wikipedia"),
                 })
        {
            try
            {
                if (await FirstUrlAsync(searchUrl, parse, cancellationToken) is not { } url) continue;
                if (await DownloadAsync(url, cancellationToken) is not { } data) continue;
                if (imageSize(data) is not { } size) continue;
                return new TeacherPicture(data, size.Width, size.Height, caption, credit);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
            {
                AppLog.Write($"teacher: picture search on {credit} failed ({error.GetType().Name})");
            }
        }
        AppLog.Write("teacher: no picture found for the search");
        return null;
    }

    public static Uri CommonsSearchUrl(string query) => Url("https://commons.wikimedia.org/w/api.php",
    [
        ("action", "query"),
        ("format", "json"),
        ("generator", "search"),
        ("gsrsearch", $"{query} filetype:bitmap|drawing"),
        ("gsrnamespace", "6"),
        ("gsrlimit", "6"),
        ("prop", "imageinfo"),
        ("iiprop", "url|mime"),
        ("iiurlwidth", ThumbnailWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ]);

    public static Uri WikipediaSearchUrl(string query) => Url("https://en.wikipedia.org/w/api.php",
    [
        ("action", "query"),
        ("format", "json"),
        ("generator", "search"),
        ("gsrsearch", query),
        ("gsrlimit", "3"),
        ("prop", "pageimages"),
        ("piprop", "thumbnail"),
        ("pithumbsize", ThumbnailWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ]);

    private static Uri Url(string baseUrl, IEnumerable<(string Name, string Value)> queryItems) =>
        new(baseUrl + "?" + string.Join("&", queryItems.Select(item => $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value)}")));

    /// <summary>Commons results in search order (`index`), still images only, as their thumbnail URLs (an SVG's thumbnail is a PNG).</summary>
    public static IReadOnlyList<Uri> CommonsThumbnailUrls(byte[] data) =>
        SearchPages(data).Select(page =>
        {
            if ((page["imageinfo"] as JsonArray)?.FirstOrDefault() is not JsonObject info) return null;
            if (StringValue(info["mime"]) is not ("image/png" or "image/jpeg" or "image/svg+xml" or "image/webp")) return null;
            return StringValue(info["thumburl"]) is { } thumbnail && Uri.TryCreate(thumbnail, UriKind.Absolute, out var url) ? url : null;
        }).OfType<Uri>().ToList();

    public static IReadOnlyList<Uri> WikipediaThumbnailUrls(byte[] data) =>
        SearchPages(data).Select(page =>
            StringValue((page["thumbnail"] as JsonObject)?["source"]) is { } thumbnail && Uri.TryCreate(thumbnail, UriKind.Absolute, out var url) ? url : null)
        .OfType<Uri>().ToList();

    /// <summary>`query.pages` is keyed by page id; the search order is each page's `index`.</summary>
    private static IEnumerable<JsonObject> SearchPages(byte[] data)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return []; }
        if ((root?["query"] as JsonObject)?["pages"] is not JsonObject pages) return [];
        return pages.Select(pair => pair.Value).OfType<JsonObject>()
            .OrderBy(page => page["index"] is JsonValue index && index.TryGetValue<int>(out var number) ? number : int.MaxValue)
            .ToList();
    }

    private static async Task<Uri?> FirstUrlAsync(Uri searchUrl, Func<byte[], IReadOnlyList<Uri>> parse, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        request.SetHeader("User-Agent", UserAgent);
        using var response = await HttpClients.Shared.SendAsync(request, timeout.Token);
        if ((int)response.StatusCode != 200) return null;
        return parse(await response.Content.ReadAsByteArrayAsync(timeout.Token)).FirstOrDefault();
    }

    private static async Task<byte[]?> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.SetHeader("User-Agent", UserAgent);
        using var response = await HttpClients.Shared.SendAsync(request, timeout.Token);
        if ((int)response.StatusCode != 200) return null;
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
