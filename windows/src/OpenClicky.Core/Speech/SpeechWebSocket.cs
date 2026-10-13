// New on Windows: the one websocket shape the speech sockets (AssemblyAI, Sarvam, OpenAI Realtime)
// use, standing in for URLSessionWebSocketTask so a test can drive a session with a fake socket.
// The real one is ClientWebSocket. URLSession's lesson about one shared session per host has no
// direct twin here: each ClientWebSocket owns its connection, and nothing is pooled to corrupt.

using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace OpenClicky.Core.Speech;

public enum SpeechWebSocketMessageKind
{
    Text,
    Binary,
    /// <summary>The server closed the socket (<see cref="SpeechWebSocketMessage.CloseStatus"/> says how).</summary>
    Close,
}

public sealed record SpeechWebSocketMessage(SpeechWebSocketMessageKind Kind, string? Text, byte[]? Data, int? CloseStatus = null, string? CloseReason = null)
{
    public static SpeechWebSocketMessage FromText(string text) => new(SpeechWebSocketMessageKind.Text, text, null);
    public static SpeechWebSocketMessage FromBinary(byte[] data) => new(SpeechWebSocketMessageKind.Binary, null, data);
    public static SpeechWebSocketMessage Closed(int? status, string? reason) => new(SpeechWebSocketMessageKind.Close, null, null, status, reason);

    /// <summary>The text of a text frame, or a binary frame read as UTF-8.</summary>
    public string? AsText => Kind switch
    {
        SpeechWebSocketMessageKind.Text => Text,
        SpeechWebSocketMessageKind.Binary when Data is not null => Encoding.UTF8.GetString(Data),
        _ => null,
    };
}

/// <summary>A failed handshake, with the HTTP status the server answered (401, 403, 429…) when there was one.</summary>
public sealed class SpeechWebSocketHandshakeException(int? statusCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>A websocket as the speech sessions use it. Sends may be called from any thread; the
/// implementation keeps them in order and one at a time.</summary>
public interface ISpeechWebSocket : IDisposable
{
    /// <summary>Opens the socket. Throws <see cref="SpeechWebSocketHandshakeException"/> when the server refuses.</summary>
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);

    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>The next whole message; a Close message when the server closed the socket. Throws when the connection fails.</summary>
    Task<SpeechWebSocketMessage> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes politely (best effort, never throws).</summary>
    Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? reason = null);

    /// <summary>Drops the connection at once.</summary>
    void Abort();

    /// <summary>The handshake's HTTP status (101 when accepted), when known.</summary>
    int? HandshakeStatusCode { get; }
}

/// <summary><see cref="ISpeechWebSocket"/> over <see cref="ClientWebSocket"/>.</summary>
public sealed class ClientSpeechWebSocket : ISpeechWebSocket
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly SemaphoreSlim receiveLock = new(1, 1);
    private int disposed;

    public int? HandshakeStatusCode { get; private set; }

    /// <summary>How often a websocket ping frame keeps the connection alive through NATs (the server answers it).</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(20);

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        foreach (var (name, value) in headers) socket.Options.SetRequestHeader(name, value);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            HandshakeStatusCode = (int)socket.HttpStatusCode;
        }
        catch (WebSocketException error)
        {
            var status = socket.HttpStatusCode == 0 ? (int?)null : (int)socket.HttpStatusCode;
            HandshakeStatusCode = status;
            throw new SpeechWebSocketHandshakeException(status, status is { } code ? $"the server refused the connection ({code})" : error.Message, error);
        }
        catch (HttpRequestException error)
        {
            throw new SpeechWebSocketHandshakeException(error.StatusCode is { } code ? (int)code : null, error.Message, error);
        }
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default) =>
        SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, cancellationToken);

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        SendAsync(data, WebSocketMessageType.Binary, cancellationToken);

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open) throw new WebSocketException(WebSocketError.InvalidState, "Socket is not connected");
            await socket.SendAsync(data, type, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task<SpeechWebSocketMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        await receiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var message = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(new Memory<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var closeStatus = socket.CloseStatus is { } status ? (int)status : (int?)null;
                    return SpeechWebSocketMessage.Closed(closeStatus, socket.CloseStatusDescription);
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var bytes = message.ToArray();
                return result.MessageType == WebSocketMessageType.Text
                    ? SpeechWebSocketMessage.FromText(Encoding.UTF8.GetString(bytes))
                    : SpeechWebSocketMessage.FromBinary(bytes);
            }
        }
        finally
        {
            receiveLock.Release();
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? reason = null)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            Abort();
        }
    }

    public void Abort()
    {
        try { socket.Abort(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        Abort();
        socket.Dispose();
    }

    /// <summary>The status of an HTTP answer as an int, for handshake errors.</summary>
    public static int? StatusCode(HttpStatusCode code) => code == 0 ? null : (int)code;
}
