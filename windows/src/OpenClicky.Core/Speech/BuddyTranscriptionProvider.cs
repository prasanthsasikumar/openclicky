// Ports BuddyTranscriptionProvider.swift: the shared surface of every transcription backend (a
// streaming session fed the take's audio, and the provider that opens one). The factory that picks
// the backend from shell.json is in BuddyTranscriptionProviderFactory.cs.

using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Speech;

/// <summary>One take being heard. Audio arrives on the capture thread; the callbacks given to the
/// provider may be raised on any thread.</summary>
public interface IBuddyStreamingTranscriptionSession
{
    /// <summary>How long the manager waits after <see cref="RequestFinalTranscript"/> before it
    /// finishes the take with whatever was heard.</summary>
    TimeSpan FinalTranscriptFallbackDelay { get; }

    /// <summary>A block of captured audio (any rate; the session converts what it needs). Called on the capture thread.</summary>
    void AppendAudio(AudioFrame frame);

    void RequestFinalTranscript();

    void Cancel();
}

/// <summary>A transcription backend (BuddyTranscriptionProvider).</summary>
public interface IBuddyTranscriptionProvider
{
    string DisplayName { get; }

    /// <summary>The Mac asked for Speech Recognition permission for Apple Speech; on Windows it marks the
    /// engine that listens through the Windows speech recogniser (its own privacy switch).</summary>
    bool RequiresSpeechRecognitionPermission { get; }

    bool IsConfigured { get; }

    /// <summary>The sentence saying why it cannot run; null when it can.</summary>
    string? UnavailableExplanation { get; }

    Task<IBuddyStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken = default);
}

/// <summary>The three callbacks a transcription session reports through.</summary>
public sealed record BuddyTranscriptionSessionCallbacks(
    Action<string> OnTranscriptUpdate,
    Action<string> OnFinalTranscriptReady,
    Action<Exception> OnError);

/// <summary>An error whose message is the sentence to show (the Swift providers' LocalizedError structs).</summary>
public sealed class TranscriptionProviderException(string message, Exception? inner = null) : Exception(message, inner);
