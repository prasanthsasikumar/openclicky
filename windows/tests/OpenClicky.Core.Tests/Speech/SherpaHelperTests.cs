// The helper protocol (SherpaHelperProcess.swift): one JSON line each way, an error line for a
// request that throws, and the app-side client against a fake helper process.

using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class SherpaHelperTests
{
    [Fact]
    public void EachRequestLineGetsOneReplyLineUntilInputEnds()
    {
        var input = new StringReader(
            "{\"text\":\"hi\",\"speakerID\":3,\"languageHint\":\"en-us\",\"modelFolder\":\"C:\\\\m\"}\n"
            + "{\"text\":\"\",\"speakerID\":9,\"languageHint\":\"fr\",\"modelFolder\":\"x\"}\n");
        var output = new StringWriter { NewLine = "\n" };
        SherpaHelperProtocol.Run<KokoroEngineRequest, KokoroHelperReply>(input, output, request =>
        {
            if (request.Text.Length == 0) throw new KokoroException(KokoroException.NothingGeneratedMessage);
            return new KokoroHelperReply($"{request.Text}/{request.SpeakerId}/{request.LanguageHint}/{request.ModelFolder}");
        });
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"wav\":\"hi/3/en-us/C:\\\\m\"}", lines[0]);
        Assert.Equal("Kokoro said nothing.", SherpaHelperProtocol.ErrorOf(lines[1]));
    }

    [Fact]
    public void AnUnreadableRequestIsAnErrorLineNotACrash()
    {
        var reply = SherpaHelperProtocol.Answer<ParakeetEngineRequest, ParakeetEngineReply>("not json", _ => new ParakeetEngineReply("x"));
        Assert.NotNull(SherpaHelperProtocol.ErrorOf(reply));
        Assert.Null(SherpaHelperProtocol.ErrorOf("{\"text\":\"fine\"}"));
    }

    [Fact]
    public void RequestsUseTheSwiftKeys()
    {
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            new ParakeetEngineRequest("AAA=", "m"), SherpaHelperProtocol.JsonOptions))!.AsObject();
        Assert.Equal("AAA=", (string?)json["pcm16Base64"]);
        Assert.Equal("m", (string?)json["modelFolder"]);
    }

    [Fact]
    public async Task TheClientStartsTheHelperOnceAndReadsEachReply()
    {
        var runner = new FakeHelperRunner(line =>
        {
            var request = JsonNode.Parse(line)!;
            var text = (string?)request["text"];
            return text == "boom" ? "{\"error\":\"it broke\"}" : $"{{\"wav\":\"{text}\"}}";
        });
        var client = new SherpaHelperClient("--flag", "test", runner, @"C:\app\OpenClicky.exe");
        var first = await client.SendAsync<KokoroEngineRequest, KokoroHelperReply>(new KokoroEngineRequest("one", 3, "en-us", "m"));
        var second = await client.SendAsync<KokoroEngineRequest, KokoroHelperReply>(new KokoroEngineRequest("two", 3, "en-us", "m"));
        Assert.Equal("one", first.Wav);
        Assert.Equal("two", second.Wav);
        Assert.Equal(1, runner.StartCount);
        Assert.Equal(["--flag"], runner.LastArguments);
        var failure = await Assert.ThrowsAsync<SherpaHelperException>(() =>
            client.SendAsync<KokoroEngineRequest, KokoroHelperReply>(new KokoroEngineRequest("boom", 3, "en-us", "m")));
        Assert.Equal("the helper failed: it broke.", failure.Message);
        Assert.True(await client.UnloadAsync());
        Assert.False(await client.UnloadAsync());
        await client.SendAsync<KokoroEngineRequest, KokoroHelperReply>(new KokoroEngineRequest("three", 3, "en-us", "m"));
        Assert.Equal(2, runner.StartCount);
    }

    private sealed class FakeHelperRunner(Func<string, string> answer) : IProcessRunner
    {
        public int StartCount;
        public IReadOnlyList<string> LastArguments = [];

        public string? ResolveExecutable(string command) => command;

        public IRunningProcess Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastArguments = request.Arguments;
            return new FakeHelperProcess(answer);
        }

        public Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHelperProcess(Func<string, string> answer) : IRunningProcess
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        private readonly Channel<string> errors = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> exited = new();

        public int ProcessId => 42;
        public ChannelReader<string> StandardOutputLines => output.Reader;
        public ChannelReader<string> StandardErrorLines => errors.Reader;

        public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            output.Writer.TryWrite(answer(line));
            return Task.CompletedTask;
        }

        public void CloseStandardInput() { }

        public void Kill()
        {
            output.Writer.TryComplete();
            errors.Writer.TryComplete();
            exited.TrySetResult(-1);
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => exited.Task.WaitAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Kill();
            return ValueTask.CompletedTask;
        }
    }
}
