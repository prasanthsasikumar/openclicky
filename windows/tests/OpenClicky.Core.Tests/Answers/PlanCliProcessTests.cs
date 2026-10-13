// New on Windows: how a plan CLI path becomes a process (an .exe directly, an npm .cmd shim through
// its Node script, anything else through cmd.exe with every argument escaped), and the real runner
// (stdin in, stdout chunks out, timeout and cancellation kill the tree). The runner tests start
// cmd.exe with a small batch file in the temporary folder.

using System.Diagnostics;
using System.Text;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Tests.Answers;

public class PlanCliLaunchTests
{
    private static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PATH"] = @"C:\Program Files\nodejs;C:\Windows\System32",
        ["ComSpec"] = @"C:\Windows\System32\cmd.exe",
    };

    private const string NpmShim = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@openai\codex\bin\codex.js" %*
        """;

    [Fact]
    public void AnExeRunsDirectlyWithItsArgumentsAsTheyAre()
    {
        var launch = PlanCliLaunch.Resolve(@"C:\Users\test\.local\bin\claude.exe", ["-p", "--tools", "", "two\nlines"], Environment);
        Assert.Equal(@"C:\Users\test\.local\bin\claude.exe", launch.FileName);
        Assert.Equal(["-p", "--tools", "", "two\nlines"], launch.ArgumentList);
        Assert.Null(launch.RawArguments);
    }

    [Fact]
    public void AnNpmShimRunsItsScriptWithNode()
    {
        const string shim = @"C:\Users\test\AppData\Roaming\npm\codex.cmd";
        const string script = @"C:\Users\test\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { script, @"C:\Program Files\nodejs\node.exe" };
        var launch = PlanCliLaunch.Resolve(shim, ["exec", "-c", "developer_instructions=\"a\nb\""], Environment,
                                           path => path == shim ? NpmShim : null, existing.Contains);
        Assert.Equal(@"C:\Program Files\nodejs\node.exe", launch.FileName);
        Assert.Equal([script, "exec", "-c", "developer_instructions=\"a\nb\""], launch.ArgumentList);
        Assert.Null(launch.RawArguments);

        // The node.exe next to the shim wins.
        existing.Add(@"C:\Users\test\AppData\Roaming\npm\node.exe");
        Assert.Equal(@"C:\Users\test\AppData\Roaming\npm\node.exe",
                     PlanCliLaunch.Resolve(shim, [], Environment, path => path == shim ? NpmShim : null, existing.Contains).FileName);
    }

    [Fact]
    public void AnyOtherBatchFileGoesThroughCmdWithEveryPartEscaped()
    {
        var launch = PlanCliLaunch.Resolve(@"C:\tools\my cli.cmd", ["say \"hi\" & bye", "100%", "two\nlines"], Environment,
                                           _ => "@echo off\r\nnode cli.js %*\r\n", _ => false);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", launch.FileName);
        Assert.Empty(launch.ArgumentList);
        Assert.Equal("/d /s /c \"^\"C:\\tools\\my^ cli.cmd^\" ^\"say^ \\^\"hi\\^\"^ ^&^ bye^\" ^\"100^%^\" ^\"two^ lines^\"\"", launch.RawArguments);
    }

    [Fact]
    public void ArgumentsAreQuotedForTheCRuntimeThenEscapedForCmd()
    {
        Assert.Equal("^\"plain^\"", PlanCliLaunch.EscapeForCmd("plain"));
        Assert.Equal("^\"^\"", PlanCliLaunch.EscapeForCmd(""));
        Assert.Equal("^\"C:\\dir\\\\^\"", PlanCliLaunch.EscapeForCmd("C:\\dir\\"));
        Assert.Equal("^\"a\\\\\\^\"b^\"", PlanCliLaunch.EscapeForCmd("a\\\"b"));
    }
}

public class PlanCliProcessRunnerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "openclicky-runner-tests", Guid.NewGuid().ToString("N"));

    public PlanCliProcessRunnerTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string BatchFile(string name, string body)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "@echo off\r\n" + body.ReplaceLineEndings("\r\n"));
        return path;
    }

    [Fact]
    public async Task ItPassesArgumentsAndStdinAndCollectsBothStreams()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = BatchFile("echo.cmd", "echo arg=%1\r\nfindstr .\r\necho oops 1>&2\r\nexit /b 3\r\n");
        var chunks = new List<byte>();
        var output = await new PlanCliProcessRunner().RunAsync(script, ["a & b"], Encoding.UTF8.GetBytes("from stdin\r\n"), folder,
                                                                OpenClickyConfiguration.ProcessEnvironment(), 30, chunk => { lock (chunks) chunks.AddRange(chunk); });
        Assert.Equal(3, output.ExitStatus);
        Assert.False(output.TimedOut);
        Assert.Contains("arg=\"a & b\"", output.StandardOutputText);
        Assert.Contains("from stdin", output.StandardOutputText);
        Assert.Contains("oops", output.StandardErrorText);
        Assert.Equal(output.StandardOutput, chunks.ToArray());
    }

    [Fact]
    public async Task ATimeoutKillsTheRunAndSaysSo()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = BatchFile("slow.cmd", "ping -n 30 127.0.0.1 >nul\r\necho finished\r\n");
        var stopwatch = Stopwatch.StartNew();
        var output = await new PlanCliProcessRunner().RunAsync(script, [], null, folder, OpenClickyConfiguration.ProcessEnvironment(), 1, null);
        Assert.True(output.TimedOut);
        Assert.DoesNotContain("finished", output.StandardOutputText);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task CancellingKillsTheRunAndThrows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = BatchFile("slow2.cmd", "ping -n 30 127.0.0.1 >nul\r\n");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlanCliProcessRunner().RunAsync(script, [], null, folder, OpenClickyConfiguration.ProcessEnvironment(), 60, null, cancellation.Token));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task AMissingExecutableFailsToStart()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            new PlanCliProcessRunner().RunAsync(Path.Combine(folder, "nope.exe"), [], null, folder, OpenClickyConfiguration.ProcessEnvironment(), 5, null));
    }
}

public class AnswerSourceCheckTests
{
    [Fact]
    public void TheRedSquareIsARealPng()
    {
        var png = AnswerSourceCheck.RedSquarePng();
        Assert.Equal("image/png", ClaudeAPI.DetectImageMediaType(png));
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(64, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal("IEND", Encoding.ASCII.GetString(png, png.Length - 8, 4));
        // The CRC of IEND is a constant.
        Assert.Equal(new byte[] { 0xAE, 0x42, 0x60, 0x82 }, png[^4..]);
    }

    [Fact]
    public void TheSentenceSaysHowLongItTook()
    {
        Assert.Equal("✓ Works. It answered in 1.2 s.", new AnswerSourceCheck.Outcome.Works(1.234).Sentence);
        Assert.Equal("Add the key first.", new AnswerSourceCheck.Outcome.Failed("Add the key first.").Sentence);
    }

    [Fact]
    public async Task ASourceIsAskedAboutTheRedSquare()
    {
        var credentials = new ProviderCredentials(null, null, null, groqKey: "gsk");
        var red = await AnswerSourceCheck.RunAsync(AnswerSource.Groq, credentials, new Dictionary<string, string>(),
                                                   _ => Task.FromResult<IAnswerProvider>(new FakeAnswerProvider(["Red."], [])));
        Assert.IsType<AnswerSourceCheck.Outcome.Works>(red);
        var blue = await AnswerSourceCheck.RunAsync(AnswerSource.Groq, credentials, new Dictionary<string, string>(),
                                                    _ => Task.FromResult<IAnswerProvider>(new FakeAnswerProvider(["blue, definitely blue, absolutely nothing else at all"], [])));
        Assert.Equal("It answered “blue, definitely blue, absolutely nothin” instead of red.", blue.Sentence);
        var refused = await AnswerSourceCheck.RunAsync(AnswerSource.Groq, credentials, new Dictionary<string, string>(),
                                                       _ => throw new AnswerProviderException(AnswerSource.Groq, 401, "invalid API key"));
        Assert.Equal("Invalid API key", refused.Sentence);
        Assert.Equal("Add the key first.", (await AnswerSourceCheck.RunAsync(AnswerSource.Anthropic, credentials, new Dictionary<string, string>())).Sentence);
        Assert.Equal("The free credits are checked by asking Clicky.",
                     (await AnswerSourceCheck.RunAsync(AnswerSource.OpenClickyAccount, credentials, new Dictionary<string, string>())).Sentence);
    }

    [Fact]
    public async Task TheFactoryNamesWhatIsMissing()
    {
        var none = new ProviderCredentials(null, null, null);
        var missingKey = await Assert.ThrowsAsync<AnswerProviderException>(() =>
            AnswerProviderFactory.MakeAsync(AnswerSource.Mistral, none, new Dictionary<string, string>()));
        Assert.Equal("add its key in settings, account.", missingKey.VendorMessage);
        var noClaude = await Assert.ThrowsAsync<AnswerProviderException>(() =>
            AnswerProviderFactory.MakeAsync(AnswerSource.ClaudeCodePlan, none, new Dictionary<string, string>(), () => Task.FromResult<string?>(null)));
        Assert.Equal("the claude code app (claude) isn't installed on this pc.", noClaude.VendorMessage);
        var withModel = await AnswerProviderFactory.MakeAsync(AnswerSource.OpenRouter, new ProviderCredentials(null, null, null, openRouterKey: "or"),
                                                              new Dictionary<string, string> { ["openRouter"] = "x-ai/grok-4" });
        Assert.Equal("x-ai/grok-4", Assert.IsType<OpenAIChatAnswerProvider>(withModel).Model);
        var defaultModel = await AnswerProviderFactory.MakeAsync(AnswerSource.Gemini, new ProviderCredentials(null, null, null, geminiKey: "g"),
                                                                 new Dictionary<string, string> { ["gemini"] = "" });
        Assert.Equal("gemini-3.8-flash", Assert.IsType<GeminiAnswerProvider>(defaultModel).Model);
        var codex = await AnswerProviderFactory.MakeAsync(AnswerSource.CodexPlan, none, new Dictionary<string, string>(),
                                                          locateCodex: () => Task.FromResult<string?>(@"C:\npm\codex.cmd"));
        Assert.Equal(@"C:\npm\codex.cmd", Assert.IsType<CodexAnswerProvider>(codex).ExecutablePath);
    }
}
