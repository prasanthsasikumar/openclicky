using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// One OpenClicky per user session: a named mutex in the session namespace, and a named pipe on
/// which the running instance receives a later launch's arguments (JSON array, one per connection).
/// A relaunch passes <c>--openclicky-relaunched-from &lt;pid&gt;</c> so the new copy waits for the old one to exit before taking the mutex.
/// </summary>
public sealed class SingleInstance : ISingleInstance
{
    public const string RelaunchedFromArgument = "--openclicky-relaunched-from";

    private readonly string mutexName;
    private readonly string pipeName;
    private readonly CancellationTokenSource listening = new();
    private Mutex? mutex;
    private bool ownsMutex;

    public SingleInstance(string applicationId = "OpenClicky")
    {
        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        mutexName = $@"Local\{applicationId}.SingleInstance.{userId}";
        pipeName = $"{applicationId}.Arguments.{userId}";
    }

    public event Action<IReadOnlyList<string>>? ArgumentsReceived;

    public bool TryBecomePrimary(IReadOnlyList<string> args)
    {
        WaitForPreviousInstance(args);
        mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                // The owner died without releasing it: abandoned mutexes are ours to take.
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
        }
        else
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            Forward(args);
            return false;
        }
        _ = ListenAsync(listening.Token);
        return true;
    }

    public void Relaunch(IReadOnlyList<string>? args = null)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var startInfo = new ProcessStartInfo(processPath) { UseShellExecute = false };
        foreach (var argument in args ?? []) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(RelaunchedFromArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(startInfo);
    }

    private static void WaitForPreviousInstance(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] != RelaunchedFromArgument || !int.TryParse(args[index + 1], out var previousProcessId)) continue;
            try
            {
                using var previous = Process.GetProcessById(previousProcessId);
                previous.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }

    private void Forward(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(2000);
            JsonSerializer.Serialize(client, args.Where(argument => argument != RelaunchedFromArgument).ToArray());
            client.Flush();
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"single instance: couldn't forward arguments ({exception.Message})");
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var memory = new MemoryStream();
                await server.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                var args = memory.Length == 0 ? [] : JsonSerializer.Deserialize<string[]>(memory.ToArray()) ?? [];
                ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"single instance: {exception.Message}");
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        listening.Cancel();
        if (mutex is not null)
        {
            // Only the thread that took the mutex can release it; from any other, process exit releases it.
            try
            {
                if (ownsMutex) mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            mutex.Dispose();
            mutex = null;
        }
        listening.Dispose();
    }
}
