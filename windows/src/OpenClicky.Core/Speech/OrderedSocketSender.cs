// The serial send queue the Swift sessions had (a DispatchQueue per socket): every message goes
// out in the order it was queued, one at a time, from any thread, without the caller waiting.

using System.Threading.Channels;

namespace OpenClicky.Core.Speech;

public sealed class OrderedSocketSender
{
    private abstract record Outgoing;
    private sealed record OutgoingText(string Text) : Outgoing;
    private sealed record OutgoingBinary(byte[] Data) : Outgoing;

    private readonly Channel<Outgoing> queue = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ISpeechWebSocket socket;
    private readonly Action<Exception> onSendFailed;
    private readonly CancellationToken cancellationToken;

    /// <param name="onSendFailed">A send that failed (the socket is gone); called once per failed message.</param>
    public OrderedSocketSender(ISpeechWebSocket socket, Action<Exception> onSendFailed, CancellationToken cancellationToken)
    {
        this.socket = socket;
        this.onSendFailed = onSendFailed;
        this.cancellationToken = cancellationToken;
        Drained = Task.Run(DrainAsync);
    }

    public void SendText(string text) => queue.Writer.TryWrite(new OutgoingText(text));

    public void SendBinary(byte[] data) => queue.Writer.TryWrite(new OutgoingBinary(data));

    /// <summary>Nothing more is sent; what is queued is dropped.</summary>
    public void Complete() => queue.Writer.TryComplete();

    /// <summary>Finishes when everything queued before <see cref="Complete"/> went out (or the sender was cancelled).</summary>
    public Task Drained { get; }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var outgoing in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (outgoing is OutgoingText text) await socket.SendTextAsync(text.Text, cancellationToken).ConfigureAwait(false);
                    else if (outgoing is OutgoingBinary binary) await socket.SendBinaryAsync(binary.Data, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    onSendFailed(error);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
