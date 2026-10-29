// Port of macos/OpenClicky/OpenClicky/WebSearchURL.swift.
//
// `web_search`: "search for rotary engines", "search YouTube for lo-fi". A closed set of sites,
// each with its own search page, so the model never hands over a URL of its own making.

namespace OpenClicky.Core.Actions;

public enum WebSearchSite
{
    Web,
    YouTube,
    Wikipedia,
    Maps,
    Amazon,
}

public static class WebSearchSites
{
    /// <summary>In the order the tool schema lists them.</summary>
    public static readonly IReadOnlyList<WebSearchSite> All =
        [WebSearchSite.Web, WebSearchSite.YouTube, WebSearchSite.Wikipedia, WebSearchSite.Maps, WebSearchSite.Amazon];

    /// <summary>The schema value: "web", "youtube", …</summary>
    public static string RawValue(this WebSearchSite site) => site switch
    {
        WebSearchSite.YouTube => "youtube",
        WebSearchSite.Wikipedia => "wikipedia",
        WebSearchSite.Maps => "maps",
        WebSearchSite.Amazon => "amazon",
        _ => "web",
    };

    public static WebSearchSite? Named(string raw)
    {
        var wanted = raw.Trim().ToLowerInvariant();
        foreach (var site in All)
        {
            if (site.RawValue() == wanted) return site;
        }
        return null;
    }

    /// <summary>
    /// The site's search page for <paramref name="query"/>. Every character but the unreserved ones is
    /// percent-encoded (UTF-8): a bare `+` is a space to every search page, and `&amp;` or `#` would cut
    /// the query short.
    /// </summary>
    public static Uri Url(this WebSearchSite site, string query)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        var text = site switch
        {
            WebSearchSite.YouTube => $"https://www.youtube.com/results?search_query={encodedQuery}",
            WebSearchSite.Wikipedia => $"https://en.wikipedia.org/w/index.php?search={encodedQuery}",
            WebSearchSite.Maps => $"https://www.google.com/maps/search/{encodedQuery}",
            WebSearchSite.Amazon => $"https://www.amazon.com/s?k={encodedQuery}",
            _ => $"https://www.google.com/search?q={encodedQuery}",
        };
        return new Uri(text);
    }
}
