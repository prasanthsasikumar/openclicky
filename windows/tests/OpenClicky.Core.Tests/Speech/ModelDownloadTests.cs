// The checked model download (ModelDownload.swift): the checksum, the install states Settings
// shows, and the conditions an automatic download waits for.

using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class ModelDownloadTests
{
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
