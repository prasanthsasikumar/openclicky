// Port of macos/OpenClicky/OpenClicky/AnswerSourceCheck.swift
//
// "Does this really answer?": one tiny question (what colour is this red square) asked of a
// single source, for the Test buttons in Settings → Account and the debug answer smoke.
// The red square is a PNG built here byte by byte (Core has no imaging library).

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Answers;

public static class AnswerSourceCheck
{
    public abstract record Outcome
    {
        private Outcome() { }

        public sealed record Works(double Seconds) : Outcome;
        public sealed record Failed(string Message) : Outcome;

        /// <summary>The line the Test button leaves in its row.</summary>
        public string Sentence => this switch
        {
            Works works => $"✓ Works. It answered in {works.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} s.",
            Failed failed => failed.Message,
            _ => "",
        };
    }

    public const string AnthropicModelsUrl = "https://api.anthropic.com/v1/models?limit=1";

    /// <summary>
    /// Asks `source` alone, whatever the router would pick. Anthropic is checked by listing its
    /// models (ClaudeAPI answers it, not an IAnswerProvider); the free credits aren't checked here.
    /// </summary>
    /// <param name="makeProvider">Builds the provider (AnswerProviderFactory.MakeAsync by default).</param>
    public static async Task<Outcome> RunAsync(AnswerSource source, ProviderCredentials credentials,
                                               IReadOnlyDictionary<string, string> modelOverrides,
                                               Func<AnswerSource, Task<IAnswerProvider>>? makeProvider = null,
                                               CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            switch (source)
            {
                case AnswerSource.Anthropic:
                {
                    if (credentials.AnthropicKey is not { } key) return new Outcome.Failed("Add the key first.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, AnthropicModelsUrl);
                    request.SetHeader("x-api-key", key);
                    request.SetHeader("anthropic-version", DirectProviderRequests.AnthropicVersion);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    using var response = await HttpClients.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    return status == 200
                        ? new Outcome.Works(stopwatch.Elapsed.TotalSeconds)
                        : new Outcome.Failed(status == 401 ? "Anthropic didn’t accept this key." : $"Anthropic answered {status}.");
                }
                case AnswerSource.OpenClickyAccount:
                    return new Outcome.Failed("The free credits are checked by asking Clicky.");
                default:
                {
                    var provider = makeProvider is not null
                        ? await makeProvider(source).ConfigureAwait(false)
                        : await AnswerProviderFactory.MakeAsync(source, credentials, modelOverrides, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var request = new AnswerRequest(
                        [new AnswerRequest.Image(RedSquarePng(), "a test image", "image/png")],
                        "answer in lowercase, one word.", [],
                        "what color is this square?", []);
                    var result = await provider.AnswerAsync(request, _ => { }, cancellationToken).ConfigureAwait(false);
                    var seconds = stopwatch.Elapsed.TotalSeconds;
                    return result.Text.ToLowerInvariant().Contains("red")
                        ? new Outcome.Works(seconds)
                        : new Outcome.Failed($"It answered “{Prefix(result.Text, 40)}” instead of red.");
                }
            }
        }
        catch (AnswerProviderException error)
        {
            var message = error.VendorMessage;
            return new Outcome.Failed(message.Length == 0 ? message : char.ToUpperInvariant(message[0]) + message[1..]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new Outcome.Failed(error.Message);
        }
    }

    private static string Prefix(string text, int length)
    {
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var prefix = new StringBuilder();
        var count = 0;
        while (count < length && elements.MoveNext())
        {
            prefix.Append(elements.GetTextElement());
            count++;
        }
        return prefix.ToString();
    }

    /// <summary>A 64×64 red PNG (8-bit RGB, one zlib-compressed IDAT).</summary>
    public static byte[] RedSquarePng()
    {
        const int size = 64;
        var rawScanlines = new byte[size * (1 + size * 3)];
        for (var row = 0; row < size; row++)
        {
            var rowStart = row * (1 + size * 3);
            rawScanlines[rowStart] = 0; // filter: none
            for (var column = 0; column < size; column++)
            {
                rawScanlines[rowStart + 1 + column * 3] = 0xFF;
            }
        }
        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(rawScanlines);
            compressed = compressedStream.ToArray();
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: RGB
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        png.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
