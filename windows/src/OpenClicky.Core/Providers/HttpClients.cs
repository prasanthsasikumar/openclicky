// New on Windows: the shared HttpClient (the Mac used URLSession.shared). One long-lived client
// for every request, never one per call: creating and dropping clients exhausts sockets, the
// lesson the Mac learned with URLSession. Tests swap the handler with `UseHandler`.

namespace OpenClicky.Core.Providers;

public static class HttpClients
{
    private static readonly object ClientLock = new();
    private static HttpClient shared = CreateDefault();

    /// <summary>The one client every Core request goes through.</summary>
    public static HttpClient Shared
    {
        get { lock (ClientLock) return shared; }
    }

    private static HttpClient CreateDefault()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return Configure(new HttpClient(handler, disposeHandler: true));
    }

    private static HttpClient Configure(HttpClient client)
    {
        // Streamed answers can run long; a caller that needs a shorter limit passes a token.
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"OpenClicky-Windows/{Configuration.AppBundleConfiguration.ShortVersionString.Replace(' ', '-')}");
        return client;
    }

    /// <summary>Routes every request through `handler` (tests: a fake that answers without a network).</summary>
    public static void UseHandler(HttpMessageHandler handler)
    {
        lock (ClientLock) shared = Configure(new HttpClient(handler, disposeHandler: false));
    }

    /// <summary>Back to the real network.</summary>
    public static void Reset()
    {
        lock (ClientLock) shared = CreateDefault();
    }
}

/// <summary>A handler answering from a function: the URLProtocol stub of the Mac tests.</summary>
public sealed class DelegatingTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        answer(request, cancellationToken);
}

public static class HttpRequestMessageExtensions
{
    /// <summary>
    /// A copy that can be sent again (an HttpRequestMessage goes out once): method, URL, headers,
    /// options and the body's bytes.
    /// </summary>
    public static async Task<HttpRequestMessage> CloneAsync(this HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }
        return clone;
    }

    /// <summary>A request header's value (URLRequest.value(forHTTPHeaderField:)), content headers included.</summary>
    public static string? HeaderValue(this HttpRequestMessage request, string name)
    {
        if (request.Headers.TryGetValues(name, out var values)) return string.Join(",", values);
        if (request.Content is not null && request.Content.Headers.TryGetValues(name, out var contentValues)) return string.Join(",", contentValues);
        return null;
    }

    /// <summary>Sets a header exactly as written (URLRequest.setValue(_:forHTTPHeaderField:)).</summary>
    public static void SetHeader(this HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}
