// Ports Dictation/TakeAudioStore.swift: "retry failed dictations": the audio of a take that a
// network engine could not finish is kept on this PC as a WAV, so the take can be heard again once
// the key, the credits or the network are back. Bounded (100 takes, 1 GB); nothing is kept once a
// take succeeds or is cancelled, and nothing is kept at all when the setting is off.
//
// On Windows the capture side hands over PCM16 mono at 16 kHz already (the Platform layer converts
// the microphone's format), so `Append` takes bytes, not an audio buffer.
// TODO(integration): the Mac's `transcribe(fileURL:provider:…)` (feeding a retained WAV to a
// transcription provider as if it were the microphone) needs the Speech port's provider/session
// interfaces; `ReadPcm16` and `Slices` are its pure halves.

using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Dictation;

public sealed class TakeAudioStore
{
    /// <summary>%LOCALAPPDATA%\OpenClicky\TakeAudio</summary>
    public static string DefaultDirectoryPath => Path.Combine(OpenClickyPaths.AppDataDirectory, "TakeAudio");
    public const int MaxRetainedTakes = 100;
    public const long MaxRetainedBytes = 1_000_000_000;
    /// <summary>A take longer than this is cut off in memory (ten minutes of 16 kHz PCM16 is 19 MB).</summary>
    public const int MaxCaptureBytes = 16_000 * 2 * 600;

    private readonly object captureLock = new();
    private MemoryStream capture = new();

    public string DirectoryPath { get; }

    public TakeAudioStore(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? DefaultDirectoryPath;
    }

    // MARK: capturing the take in progress

    public void BeginCapture()
    {
        lock (captureLock) capture = new MemoryStream();
    }

    /// <summary>PCM16 mono 16 kHz from the capture thread.</summary>
    public void Append(ReadOnlySpan<byte> pcm16)
    {
        lock (captureLock)
        {
            if (capture.Length >= MaxCaptureBytes) return;
            capture.Write(pcm16);
        }
    }

    /// <summary>The take's audio so far, as PCM16 mono 16 kHz, and forgets it.</summary>
    public byte[] EndCapture()
    {
        lock (captureLock)
        {
            var data = capture.ToArray();
            capture = new MemoryStream();
            return data;
        }
    }

    // MARK: retaining failed takes

    public void Retain(Guid takeId, ReadOnlySpan<byte> pcm16)
    {
        Directory.CreateDirectory(DirectoryPath);
        var wav = DictationWavFile.FromPcm16Mono(pcm16, SarvamSpeechClient.SampleRate);
        AtomicFile.WriteAllBytes(PathFor(takeId), wav);
        PruneIfNeeded();
    }

    public string PathFor(Guid takeId) =>
        Path.Combine(DirectoryPath, $"{takeId.ToString("D", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()}.wav");

    public bool HasAudio(Guid takeId) => File.Exists(PathFor(takeId));

    /// <summary>A retained take's samples (PCM16 mono 16 kHz), null when there is none.</summary>
    public byte[]? ReadPcm16(Guid takeId)
    {
        try { return DictationWavFile.Pcm16(File.ReadAllBytes(PathFor(takeId))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Discard(Guid takeId)
    {
        try { File.Delete(PathFor(takeId)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void DiscardAll()
    {
        try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Oldest first beyond the caps.</summary>
    public void PruneIfNeeded()
    {
        List<(string Path, DateTime Modified, long Length)> files;
        try
        {
            files = new DirectoryInfo(DirectoryPath).EnumerateFiles()
                .Select(file => (file.FullName, file.LastWriteTimeUtc, file.Length))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        var total = files.Sum(file => file.Length);
        while ((files.Count > MaxRetainedTakes || total > MaxRetainedBytes) && files.Count > 0)
        {
            var oldest = files[0];
            try { File.Delete(oldest.Path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            total -= oldest.Length;
            files.RemoveAt(0);
        }
    }

    /// <summary>
    /// A recording cut into microphone-sized slices (`sliceSeconds` of PCM16 mono at `sampleRate`),
    /// in order, for feeding an engine as if it were the microphone.
    /// </summary>
    public static IEnumerable<ReadOnlyMemory<byte>> Slices(byte[] pcm16, int sampleRate = SarvamSpeechClient.SampleRate, double sliceSeconds = 0.25)
    {
        var sliceBytes = Math.Max(2, (int)(sampleRate * sliceSeconds) * 2);
        for (var offset = 0; offset < pcm16.Length; offset += sliceBytes)
        {
            yield return pcm16.AsMemory(offset, Math.Min(sliceBytes, pcm16.Length - offset));
        }
    }
}

/// <summary>Why feeding a recording to an engine gave no words; the smoke run says each differently.</summary>
public sealed class RecordedAudioTranscriptionException : Exception
{
    public enum ErrorKind
    {
        /// <summary>The file could not be opened or decoded.</summary>
        UnreadableRecording,
        /// <summary>The engine refused to open a session (no key, no account, not downloaded).</summary>
        EngineDidNotOpen,
        /// <summary>The engine took the audio but sent no final words in time.</summary>
        EngineDidNotAnswer,
    }

    public ErrorKind Kind { get; }

    public RecordedAudioTranscriptionException(ErrorKind kind, string? reason = null)
        : base(kind == ErrorKind.EngineDidNotAnswer ? "the engine did not answer in time" : reason ?? "")
    {
        Kind = kind;
    }
}
