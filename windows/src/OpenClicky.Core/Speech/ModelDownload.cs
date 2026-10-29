// Ports Sherpa/ModelDownload.swift: fetching a sherpa-onnx model archive (Kokoro's voice,
// Parakeet's hearing): download, check against a pinned SHA-256, unpack into
// %LOCALAPPDATA%\OpenClicky\Models. A resumable download is not worth it: a failed one is retried
// whole, and GitHub serves both quickly. Also the conditions an automatic download waits for.
//
// The archives are .tar.bz2; .NET reads tar but not bzip2, so Windows' own tar.exe (bsdtar, in
// System32 since Windows 10 1803) unpacks them, as /usr/bin/tar does on the Mac.

using System.Security.Cryptography;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;

namespace OpenClicky.Core.Speech;

public sealed record ModelDownload(Uri Url, string Sha256, long Bytes)
{
    /// <summary>The free space an automatic download asks for: the archive and the unpacked model
    /// side by side, with room to spare.</summary>
    public const long AutomaticDownloadFreeSpace = 2_000_000_000;

    /// <summary>Windows' bsdtar.</summary>
    public static string TarExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

    /// <summary>Downloads, checks and unpacks into <paramref name="folder"/>, reporting the fraction downloaded.</summary>
    public async Task InstallAsync(string folder, Action<double> progress, IProcessRunner unpacker, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var archive = await FetchAsync(folder, written => progress(Math.Min(1, written / (double)Bytes)), cancellationToken).ConfigureAwait(false);
        try
        {
            await UnpackAsync(archive, folder, unpacker, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private async Task<string> FetchAsync(string folder, Action<long> progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        HttpResponseMessage response;
        try
        {
            response = await HttpClients.Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw ModelDownloadException.DownloadFailed(error.Message);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw ModelDownloadException.DownloadFailed($"the server answered {(int)response.StatusCode}");
            var destination = Path.Combine(folder, Path.GetFileName(Url.AbsolutePath));
            TryDelete(destination);
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long written = 0;
                    long lastReported = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        // A report per megabyte is plenty for a progress bar.
                        if (written - lastReported >= 1 << 20)
                        {
                            lastReported = written;
                            progress(written);
                        }
                    }
                    progress(written);
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                TryDelete(destination);
                throw ModelDownloadException.DownloadFailed(error.Message);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }
            if (!string.Equals(Sha256Of(destination), Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destination);
                throw ModelDownloadException.DownloadFailed("the download didn't match its checksum");
            }
            return destination;
        }
    }

    /// <summary>The file's SHA-256, lower-case hex.</summary>
    public static string Sha256Of(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Unpacks into a staging folder beside the model, then moves what it holds into <paramref name="folder"/>. Unpacked
    /// in place, a model's files exist long before they are whole (Parakeet's 650 MB encoder takes half a minute), and
    /// IsInstalled only checks that they exist: on a fresh install the voice and hands-free used Kokoro and Parakeet
    /// mid-unpack and failed with "the offline speech model didn't load" (2026-10-29). A folder move on one volume is
    /// immediate, so the model appears whole or not at all.
    /// </summary>
    internal static async Task UnpackAsync(string archive, string folder, IProcessRunner unpacker, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(folder, ".unpacking");
        TryDeleteFolder(staging);
        Directory.CreateDirectory(staging);
        try
        {
            var result = await unpacker.RunAsync(new ProcessLaunchRequest
            {
                Command = TarExecutablePath,
                Arguments = ["-xjf", archive, "-C", staging],
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw ModelDownloadException.UnpackFailed($"tar exited with {result.ExitCode}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                var destination = Path.Combine(folder, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    TryDeleteFolder(destination);
                    Directory.Move(entry, destination);
                }
                else
                {
                    File.Move(entry, destination, overwrite: true);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw ModelDownloadException.UnpackFailed(error.Message);
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // MARK: Automatic downloads

    /// <summary>Free bytes on the drive holding <paramref name="path"/> (the models folder by default); 0 when unknown.</summary>
    public static long FreeSpace(string? path = null)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path ?? Configuration.OpenClickyPaths.AppDataDirectory));
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Offline, a phone hotspot or a metered connection: an automatic download waits for the next launch.</summary>
    public static bool NetworkIsExpensiveOrConstrained(INetworkReachability network) =>
        !network.IsOnline || network.IsMetered || network.IsConstrained;
}

public sealed class ModelDownloadException(string message) : Exception(message)
{
    public static ModelDownloadException DownloadFailed(string reason) => new($"the download failed: {reason}.");
    public static ModelDownloadException UnpackFailed(string reason) => new($"it couldn't be unpacked: {reason}.");
}

/// <summary>What a model download is doing, as Settings shows it. Compare with ==.</summary>
public abstract record ModelInstallState
{
    private ModelInstallState() { }

    public sealed record NotInstalledState : ModelInstallState;
    public sealed record DownloadingState(double Fraction) : ModelInstallState;
    public sealed record UnpackingState : ModelInstallState;
    public sealed record InstalledState : ModelInstallState;
    public sealed record FailedState(string Message) : ModelInstallState;

    public static ModelInstallState NotInstalled { get; } = new NotInstalledState();
    public static ModelInstallState Unpacking { get; } = new UnpackingState();
    public static ModelInstallState Installed { get; } = new InstalledState();
    public static ModelInstallState Downloading(double fraction) => new DownloadingState(fraction);
    public static ModelInstallState Failed(string message) => new FailedState(message);

    public bool IsBusy => this is DownloadingState or UnpackingState;
}
