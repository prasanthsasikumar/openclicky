// The checked model download (ModelDownload.swift): the checksum, the install states Settings
// shows, and the conditions an automatic download waits for.

using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class ModelDownloadTests
{
    /// <summary>tar, faked: writes a model folder into the folder it is told to unpack into, and checks the model's
    /// final folder is still empty while it does.</summary>
    private sealed class FakeTar(string finalModelFolder) : IProcessRunner
    {
        public bool ModelWasVisibleWhileUnpacking { get; private set; }

        public string? ResolveExecutable(string command) => command;

        public IRunningProcess Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var target = request.Arguments[request.Arguments.ToList().IndexOf("-C") + 1];
            var model = Directory.CreateDirectory(Path.Combine(target, "the-model"));
            File.WriteAllText(Path.Combine(model.FullName, "encoder.onnx"), "half written");
            ModelWasVisibleWhileUnpacking = File.Exists(Path.Combine(finalModelFolder, "encoder.onnx"));
            File.WriteAllText(Path.Combine(model.FullName, "tokens.txt"), "tokens");
            return Task.FromResult(new ProcessRunResult(0, "", "", false));
        }
    }

    /// <summary>A fresh install used Kokoro and Parakeet while tar was still writing them (2026-10-29): the model only
    /// appears in its folder once it is whole.</summary>
    [Fact]
    public async Task AModelAppearsOnlyOnceItIsWhollyUnpacked()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"openclicky-unpack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var modelFolder = Path.Combine(folder, "the-model");
            var tar = new FakeTar(modelFolder);
            await ModelDownload.UnpackAsync("archive.tar.bz2", folder, tar, CancellationToken.None);
            Assert.False(tar.ModelWasVisibleWhileUnpacking);
            Assert.True(File.Exists(Path.Combine(modelFolder, "encoder.onnx")));
            Assert.True(File.Exists(Path.Combine(modelFolder, "tokens.txt")));
            Assert.False(Directory.Exists(Path.Combine(folder, ".unpacking")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheChecksumIsLowerCaseHexSha256()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ModelDownload.Sha256Of(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InstallStatesCompareByValueAndSayWhenBusy()
    {
        Assert.Equal(ModelInstallState.Downloading(0.5), ModelInstallState.Downloading(0.5));
        Assert.NotEqual(ModelInstallState.Downloading(0.5), ModelInstallState.Downloading(0.6));
        Assert.Equal(ModelInstallState.Failed("x"), ModelInstallState.Failed("x"));
        Assert.True(ModelInstallState.Downloading(0).IsBusy);
        Assert.True(ModelInstallState.Unpacking.IsBusy);
        Assert.False(ModelInstallState.Installed.IsBusy);
        Assert.False(ModelInstallState.NotInstalled.IsBusy);
        Assert.False(ModelInstallState.Failed("x").IsBusy);
    }

    [Fact]
    public void AnAutomaticDownloadWaitsForAnUnmeteredOnlineNetwork()
    {
        Assert.False(ModelDownload.NetworkIsExpensiveOrConstrained(new FakeNetwork(online: true, metered: false, constrained: false)));
        Assert.True(ModelDownload.NetworkIsExpensiveOrConstrained(new FakeNetwork(online: true, metered: true, constrained: false)));
        Assert.True(ModelDownload.NetworkIsExpensiveOrConstrained(new FakeNetwork(online: true, metered: false, constrained: true)));
        Assert.True(ModelDownload.NetworkIsExpensiveOrConstrained(new FakeNetwork(online: false, metered: false, constrained: false)));
        Assert.Equal(2_000_000_000, ModelDownload.AutomaticDownloadFreeSpace);
    }

    [Fact]
    public void FailuresSayWhatWentWrong()
    {
        Assert.Equal("the download failed: the server answered 404.", ModelDownloadException.DownloadFailed("the server answered 404").Message);
        Assert.Equal("it couldn't be unpacked: tar exited with 1.", ModelDownloadException.UnpackFailed("tar exited with 1").Message);
    }

    [Fact]
    public void FreeSpaceIsReadFromTheDrive() => Assert.True(ModelDownload.FreeSpace(Path.GetTempPath()) > 0);

    private sealed class FakeNetwork(bool online, bool metered, bool constrained) : INetworkReachability
    {
        public bool IsOnline => online;
        public bool IsMetered => metered;
        public bool IsConstrained => constrained;
        public event Action? Changed { add { } remove { } }
        public void Dispose() { }
    }
}
