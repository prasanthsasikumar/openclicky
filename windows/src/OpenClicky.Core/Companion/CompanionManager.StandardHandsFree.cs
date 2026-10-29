//
//  CompanionManager.StandardHandsFree.cs
//  OpenClicky (Windows port of the standard hands-free half of CompanionManager.swift and
//  StandardHandsFree.swift's listener)
//
//  Hands-free without fast voice: the microphone stays open on its own capture, UtteranceDetector cuts
//  each spoken turn out of it, the ask lane's engine transcribes it, and it is asked as if Ctrl + Win had
//  been held. One side at a time: while Clicky thinks or speaks, or for 0.6 s after, nothing is listened
//  to, so it never answers its own voice. A whole turn of sign-off ("that's all, thanks") ends it.
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    private readonly object standardHandsFreeLock = new();
    private IAudioCapture? standardHandsFreeCapture;
    private readonly UtteranceDetector standardHandsFreeDetector = new();
    /// <summary>Samples waiting to fill a 20 ms frame (the capture's buffers are not frame-aligned).</summary>
    private readonly List<float> standardHandsFreePending = [];
    private bool isStandardHandsFreeOn;
    /// <summary>Read on the capture thread: whether frames are listened to (off while Clicky answers).</summary>
    private volatile bool isStandardHandsFreeAccepting;
    /// <summary>A heard turn being transcribed: nothing else is listened to until it is asked.</summary>
    private bool isStandardHandsFreeTranscribing;
    /// <summary>Re-decides four times a second whether the microphone is listened to (only when Clicky is idle).</summary>
    private Timer? standardHandsFreeGateTimer;
    /// <summary>When Clicky last became quiet, so the tail of its own voice is not heard as a turn.</summary>
    private DateTimeOffset? standardHandsFreeQuietSince;

    /// <summary>Hands-free runs on fast voice when it is on, else here; a change of lane (a key added, fast voice
    /// switched) moves it across.</summary>
    private void ApplyHandsFreeLane()
    {
        if (UsesRealtimeVoice || !IsAlwaysListening) StopStandardHandsFree();
        else StartStandardHandsFree();
    }

    private void StartStandardHandsFree()
    {
        if (isStandardHandsFreeOn) return;
        isStandardHandsFreeOn = true;
        standardHandsFreeQuietSince = DateTimeOffset.Now;
        _ = OpenStandardHandsFreeMicrophoneAsync();
    }

    private async Task OpenStandardHandsFreeMicrophoneAsync()
    {
        var capture = services.CreateAudioCapture();
        capture.FramesAvailable += OnStandardHandsFreeFrames;
        try
        {
            var started = await capture.StartAsync(new AudioCaptureOptions
            {
                DeviceId = BuddyDictationManager.PreferredMicrophoneId,
                SampleRate = UtteranceDetector.SampleRate,
                Format = AudioSampleFormat.Float32,
            });
            AppLog.Write($"hands-free (standard): microphone open ({started.DeviceName})");
        }
        catch (Exception error)
        {
            capture.FramesAvailable -= OnStandardHandsFreeFrames;
            capture.Dispose();
            AppLog.Write($"hands-free (standard): microphone failed ({error.GetType().Name})");
            services.MainThread.Post(() =>
            {
                isStandardHandsFreeOn = false;
                SetAlwaysListening(false);
                Overlay.StreamCursorCaption("hands-free couldn't open the microphone. check the microphone in settings, dictation.", holdSeconds: 6);
            });
            return;
        }
        services.MainThread.Post(() =>
        {
            // Switched off while the microphone opened.
            if (!isStandardHandsFreeOn)
            {
                CloseStandardHandsFreeMicrophone(capture);
                return;
            }
            lock (standardHandsFreeLock) standardHandsFreeCapture = capture;
            standardHandsFreeGateTimer?.Dispose();
            standardHandsFreeGateTimer = new Timer(_ => services.MainThread.Post(UpdateStandardHandsFreeGate), null, TimeSpan.Zero, TimeSpan.FromSeconds(0.25));
        });
    }

    private void StopStandardHandsFree()
    {
        if (!isStandardHandsFreeOn) return;
        isStandardHandsFreeOn = false;
        isStandardHandsFreeTranscribing = false;
        standardHandsFreeGateTimer?.Dispose();
        standardHandsFreeGateTimer = null;
        SetStandardHandsFreeAccepting(false);
        IAudioCapture? capture;
        lock (standardHandsFreeLock)
        {
            capture = standardHandsFreeCapture;
            standardHandsFreeCapture = null;
        }
        if (capture is not null) CloseStandardHandsFreeMicrophone(capture);
        AppLog.Write("hands-free (standard): off");
    }

    private void CloseStandardHandsFreeMicrophone(IAudioCapture capture)
    {
        capture.FramesAvailable -= OnStandardHandsFreeFrames;
        try { capture.Stop(); }
        catch (Exception error) { AppLog.Write($"hands-free (standard): closing the microphone failed ({error.GetType().Name})"); }
        capture.Dispose();
    }

    /// <summary>Whether a turn can be heard right now: hands-free on, Clicky neither thinking, speaking, transcribing,
    /// nor sharing the microphone with dictation or a held Ctrl + Win, and quiet for long enough that its own last
    /// words have died away.</summary>
    public static bool StandardHandsFreeAccepts(bool isOn, bool isBusy, TimeSpan? quietFor) =>
        isOn && !isBusy && quietFor is { } quiet && quiet >= TimeSpan.FromSeconds(0.6);

    private void UpdateStandardHandsFreeGate()
    {
        if (!isStandardHandsFreeOn) return;
        var isBusy = VoiceState != CompanionVoiceState.Idle || currentResponseCancellation is not null || isStandardHandsFreeTranscribing
            || BuddyDictationManager.IsDictationInProgress || IsTakeInProgress
            || answerVoicePlayer.IsPlaying || noticePlayer.IsPlaying || RecordedVoice.IsPlaying;
        if (isBusy) standardHandsFreeQuietSince = null;
        else standardHandsFreeQuietSince ??= DateTimeOffset.Now;
        SetStandardHandsFreeAccepting(StandardHandsFreeAccepts(isStandardHandsFreeOn, isBusy, DateTimeOffset.Now - standardHandsFreeQuietSince));
    }

    /// <summary>Listen to the microphone or ignore it; a turn half heard when ignoring starts is dropped.</summary>
    private void SetStandardHandsFreeAccepting(bool accepting)
    {
        if (isStandardHandsFreeAccepting == accepting) return;
        lock (standardHandsFreeLock)
        {
            isStandardHandsFreeAccepting = accepting;
            standardHandsFreeDetector.Reset();
            standardHandsFreePending.Clear();
        }
    }

    /// <summary>On the capture thread: frames go through the detector while listened to; its events go to the UI thread.</summary>
    private void OnStandardHandsFreeFrames(AudioFrame frame)
    {
        if (!isStandardHandsFreeAccepting) return;
        var events = new List<UtteranceDetector.Event>();
        lock (standardHandsFreeLock)
        {
            if (!isStandardHandsFreeAccepting) return;
            standardHandsFreePending.AddRange(frame.Samples);
            while (standardHandsFreePending.Count >= UtteranceDetector.FrameLength)
            {
                var samples = standardHandsFreePending.GetRange(0, UtteranceDetector.FrameLength).ToArray();
                standardHandsFreePending.RemoveRange(0, UtteranceDetector.FrameLength);
                if (standardHandsFreeDetector.Process(samples) is { } detected) events.Add(detected);
            }
        }
        foreach (var detected in events) services.MainThread.Post(() => HandleStandardHandsFreeEvent(detected));
    }

    private void HandleStandardHandsFreeEvent(UtteranceDetector.Event detected)
    {
        if (!isStandardHandsFreeOn) return;
        switch (detected)
        {
            case UtteranceDetector.Event.Started:
                AppLog.Write("hands-free (standard): speech started");
                ShowAskCaption("listening…", 30);
                break;
            case UtteranceDetector.Event.Discarded:
                Overlay.DismissCursorCaption();
                break;
            case UtteranceDetector.Event.Finished finished:
                isStandardHandsFreeTranscribing = true;
                SetStandardHandsFreeAccepting(false);
                ShowAskCaption("transcribing…", 30);
                _ = AskStandardHandsFreeTurnAsync(finished.Samples);
                break;
        }
    }

    private async Task AskStandardHandsFreeTurnAsync(float[] samples)
    {
        var seconds = (double)samples.Length / UtteranceDetector.SampleRate;
        var peak = UtteranceDetector.PeakDecibels(samples);
        var raised = UtteranceDetector.Normalized(samples, out var gain);
        var heard = "";
        try
        {
            // The ask lane's own engine, as the Ctrl + Win lane would hear it.
            heard = (await RecordedAudioTranscription.TranscribeAsync(UtteranceDetector.Pcm16(raised),
                BuddyDictationManager.TranscriptionProvider, keyterms: [])).Trim();
        }
        catch (Exception error)
        {
            AppLog.Write($"hands-free (standard): transcription failed ({error.GetType().Name})");
        }
        services.MainThread.Post(() =>
        {
            isStandardHandsFreeTranscribing = false;
            if (!isStandardHandsFreeOn) return;
            AppLog.Write($"hands-free (standard): heard {heard.Length} chars from {seconds:0.0} s (peak {peak:0} dBFS, gain ×{gain:0.0}, {BuddyDictationManager.TranscriptionProvider.DisplayName})");
            if (heard.Length == 0)
            {
                Overlay.DismissCursorCaption();
                return;
            }
            if (HandsFreeSignOff.IsSignOff(heard))
            {
                SetAlwaysListening(false);
                AnnounceHandsFreeChange("by voice");
                return;
            }
            // From here it is a Ctrl + Win question like any other.
            LastTranscript = heard;
            ShowAskCaption($"“{heard}” · thinking…", 30);
            SendTranscriptToClaudeWithScreenshot(heard);
        });
    }
}
