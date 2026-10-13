// Ports Sherpa/SherpaHelperProcess.swift. The sherpa-onnx models (Kokoro's voice, Parakeet's
// hearing) run in helper processes: OpenClicky's own exe started with a flag (Program.Main catches
// it before any UI exists). Ending a helper is how its model leaves memory: sherpa-onnx keeps most
// of a model allocated after it is freed in-process.
//
// The protocol is one JSON line each way over stdin/stdout. A helper exits when its stdin closes,
// so it never outlives the app.

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

public static class SherpaHelperProtocol
{
    /// <summary>Property names as the Swift Codable keys write them (camelCase).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A helper's whole life: answer each request line with <paramref name="handle"/>'s reply until
    /// the app closes the pipe. A request that throws is answered <c>{"error": message}</c>.
    /// </summary>
    public static void Run<TRequest, TReply>(TextReader input, TextWriter output, Func<TRequest, TReply> handle)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            output.WriteLine(Answer(line, handle));
            output.Flush();
        }
    }

    /// <summary>The reply line for one request line.</summary>
    public static string Answer<TRequest, TReply>(string requestLine, Func<TRequest, TReply> handle)
    {
        try
        {
            var request = JsonSerializer.Deserialize<TRequest>(requestLine, JsonOptions)
                ?? throw new SherpaHelperException("the request was empty");
            return JsonSerializer.Serialize(handle(request), JsonOptions);
        }
        catch (Exception error)
        {
            return new JsonObject { ["error"] = error.Message }.ToJsonString();
        }
    }

    /// <summary>The <c>error</c> of a reply line, when it is one.</summary>
    public static string? ErrorOf(string replyLine)
    {
        try
        {
            return JsonNode.Parse(replyLine) is JsonObject json && json["error"] is JsonValue value && value.TryGetValue<string>(out var message)
                ? message
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class SherpaHelperException(string reason) : Exception($"the helper failed: {reason}.")
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The app's side of one helper: starts it on the first request, sends requests one at a time
/// (serially: the models are CPU-bound), and ends it on <see cref="UnloadAsync"/>.
/// </summary>
public sealed class SherpaHelperClient(string flag, string logName, IProcessRunner processRunner, string executablePath)
{
    private readonly SemaphoreSlim exchangeLock = new(1, 1);
    private IRunningProcess? process;
    private bool processHasExited;

    public string Flag { get; } = flag;

    /// <summary>One request, answered. A reply carrying <c>error</c> throws it.</summary>
    public async Task<TReply> SendAsync<TRequest, TReply>(TRequest request, CancellationToken cancellationToken = default)
    {
        await exchangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var replyLine = await ExchangeAsync(JsonSerializer.Serialize(request, SherpaHelperProtocol.JsonOptions), cancellationToken).ConfigureAwait(false);
            if (SherpaHelperProtocol.ErrorOf(replyLine) is { } message) throw new SherpaHelperException(message);
            try
            {
                return JsonSerializer.Deserialize<TReply>(replyLine, SherpaHelperProtocol.JsonOptions)
                    ?? throw new SherpaHelperException("its answer was empty");
            }
            catch (JsonException)
            {
                throw new SherpaHelperException("its answer could not be read");
            }
        }
        finally
        {
            exchangeLock.Release();
        }
    }

    /// <summary>Ends the helper; returns whether one was running.</summary>
    public async Task<bool> UnloadAsync()
    {
        await exchangeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var wasRunning = process is not null && !processHasExited;
            Stop();
            return wasRunning;
        }
        finally
        {
            exchangeLock.Release();
        }
    }

    public bool IsRunning => process is not null && !processHasExited;

    private async Task<string> ExchangeAsync(string requestLine, CancellationToken cancellationToken)
    {
        var running = StartIfNeeded();
        try
        {
            await running.WriteLineAsync(requestLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Stop();
            throw new SherpaHelperException("it stopped listening");
        }
        try
        {
            if (await running.StandardOutputLines.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && running.StandardOutputLines.TryRead(out var replyLine))
            {
                return replyLine;
            }
        }
        catch (OperationCanceledException)
        {
            // The reply to this request would answer the next one: the helper goes, and the next
            // request starts a fresh one.
            Stop();
            throw;
        }
        Stop();
        throw new SherpaHelperException("it stopped");
    }

    private IRunningProcess StartIfNeeded()
    {
        if (process is not null && !processHasExited) return process;
        Stop();
        IRunningProcess started;
        try
        {
            started = processRunner.Start(new ProcessLaunchRequest { Command = executablePath, Arguments = [Flag] });
        }
        catch (ProcessRunnerException error)
        {
            throw new SherpaHelperException(error.Message);
        }
        process = started;
        processHasExited = false;
        _ = WatchExitAsync(started);
        _ = DrainStandardErrorAsync(started);
        AppLog.Write($"{logName}: helper started (pid {started.ProcessId})");
        return started;
    }

    private async Task WatchExitAsync(IRunningProcess running)
    {
        try { await running.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
        if (ReferenceEquals(process, running)) processHasExited = true;
    }

    /// <summary>Stderr is read and dropped (the Mac sent it to /dev/null) so a chatty engine never fills the pipe.</summary>
    private static async Task DrainStandardErrorAsync(IRunningProcess running)
    {
        try
        {
            await foreach (var _ in running.StandardErrorLines.ReadAllAsync().ConfigureAwait(false)) { }
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
    }

    private void Stop()
    {
        var running = process;
        process = null;
        processHasExited = false;
        if (running is null) return;
        try { running.CloseStandardInput(); } catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        try { running.Kill(); } catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
        _ = running.DisposeAsync().AsTask();
    }
}
