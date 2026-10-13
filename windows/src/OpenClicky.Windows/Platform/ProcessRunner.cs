using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// Runs the openclicky / claude / codex CLIs. Commands resolve through PATH + PATHEXT and
/// %APPDATA%\npm (where npm puts global shims). npm's .cmd shims are not run through cmd.exe —
/// cmd's quoting would mangle prompts passed as arguments — but read for the script they wrap,
/// which is then run with node directly; other .cmd/.bat files go through cmd /d /s /c.
/// </summary>
public sealed partial class ProcessRunner : IProcessRunner
{
    public string? ResolveExecutable(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var hasExtension = Path.HasExtension(command);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "openclicky"))
            .Select(directory => Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')))
            .Where(directory => directory.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (hasExtension)
            {
                var exact = Path.Combine(directory, command);
                if (File.Exists(exact)) return exact;
                continue;
            }
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public IRunningProcess Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable(request.Command)
            ?? throw new ProcessRunnerException($"{request.Command} is not installed (not found on PATH)");
        var startInfo = BuildStartInfo(executable, request.Arguments);
        if (request.WorkingDirectory is not null) startInfo.WorkingDirectory = request.WorkingDirectory;
        if (request.Environment is not null)
        {
            foreach (var (name, value) in request.Environment)
            {
                if (value is null) startInfo.Environment.Remove(name);
                else startInfo.Environment[name] = value;
            }
        }
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new ProcessRunnerException($"{request.Command} did not start");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            process.Dispose();
            throw new ProcessRunnerException($"{request.Command} could not start: {exception.Message}", exception);
        }
        return new RunningProcess(process, cancellationToken);
    }

    public async Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit) timeoutSource.CancelAfter(limit);
        await using var running = Start(request, timeoutSource.Token);
        if (standardInput is not null) await running.WriteLineAsync(standardInput, timeoutSource.Token).ConfigureAwait(false);
        running.CloseStandardInput();

        var output = new StringBuilder();
        var errors = new StringBuilder();
        var readOutput = Collect(running.StandardOutputLines, output);
        var readErrors = Collect(running.StandardErrorLines, errors);
        try
        {
            var exitCode = await running.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            await Task.WhenAll(readOutput, readErrors).ConfigureAwait(false);
            return new ProcessRunResult(exitCode, output.ToString(), errors.ToString(), TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            running.Kill();
            return new ProcessRunResult(-1, output.ToString(), errors.ToString(), TimedOut: true);
        }
    }

    private static async Task Collect(ChannelReader<string> lines, StringBuilder into)
    {
        await foreach (var line in lines.ReadAllAsync().ConfigureAwait(false)) into.AppendLine(line);
    }

    private static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var extension = Path.GetExtension(executable).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
        {
            if (NpmShimScript(executable) is { } script)
            {
                var nodeNextToShim = Path.Combine(Path.GetDirectoryName(executable)!, "node.exe");
                startInfo.FileName = File.Exists(nodeNextToShim) ? nodeNextToShim : "node";
                startInfo.ArgumentList.Add(script);
            }
            else
            {
                // cmd /d /s /c ""shim" "arg" …": /s strips the outer quotes only.
                startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                var commandLine = new StringBuilder();
                commandLine.Append("/d /s /c \"").Append(QuoteForCmd(executable));
                foreach (var argument in arguments) commandLine.Append(' ').Append(QuoteForCmd(argument));
                commandLine.Append('"');
                startInfo.Arguments = commandLine.ToString();
                return startInfo;
            }
        }
        else
        {
            startInfo.FileName = executable;
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    /// <summary>The JS file an npm shim runs: the line `"%_prog%"  "%dp0%\node_modules\…\cli.js" %*`.</summary>
    internal static string? NpmShimScript(string shimPath)
    {
        try
        {
            var text = File.ReadAllText(shimPath);
            var match = NpmShimScriptPattern().Match(text);
            if (!match.Success) return null;
            var script = Path.Combine(Path.GetDirectoryName(shimPath)!, match.Groups["relative"].Value.TrimStart('\\'));
            return File.Exists(script) ? Path.GetFullPath(script) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"""%(?:~)?dp0%\\?(?<relative>[^""]+\.(?:js|mjs|cjs))""", RegexOptions.IgnoreCase)]
    private static partial Regex NpmShimScriptPattern();

    /// <summary>
    /// Quotes an argument for the cmd /c fallback: inside quotes &amp; | &lt; &gt; ^ are literal and inner
    /// quotes are doubled. A "%NAME%" inside an argument can still be expanded by cmd (there is no
    /// escape for % on a /c command line), which is why npm shims bypass cmd altogether.
    /// </summary>
    private static string QuoteForCmd(string argument) => "\"" + argument.Replace("\"", "\"\"") + "\"";

    private sealed class RunningProcess : IRunningProcess
    {
        private readonly Process process;
        private readonly Channel<string> outputLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly Channel<string> errorLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly CancellationTokenRegistration cancellationRegistration;
        private readonly SemaphoreSlim inputLock = new(1, 1);

        public RunningProcess(Process process, CancellationToken cancellationToken)
        {
            this.process = process;
            ProcessId = process.Id;
            _ = Pump(process.StandardOutput, outputLines.Writer);
            _ = Pump(process.StandardError, errorLines.Writer);
            cancellationRegistration = cancellationToken.Register(Kill);
        }

        public int ProcessId { get; }
        public ChannelReader<string> StandardOutputLines => outputLines.Reader;
        public ChannelReader<string> StandardErrorLines => errorLines.Reader;

        private static async Task Pump(StreamReader reader, ChannelWriter<string> writer)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) writer.TryWrite(line);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
            finally
            {
                writer.TryComplete();
            }
        }

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            await inputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                inputLock.Release();
            }
        }

        public void CloseStandardInput()
        {
            try { process.StandardInput.Close(); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
            Kill();
            process.Dispose();
            inputLock.Dispose();
        }
    }
}
