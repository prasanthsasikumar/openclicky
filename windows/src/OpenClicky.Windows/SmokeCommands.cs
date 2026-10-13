// The headless checks (OpenClickyApp.swift's launch flags), each printing to the console it was
// started from and exiting with a code:
//
//   --openclicky-platform-smoke                    the OS layer on this PC (Platform/PlatformSmoke)
//   --openclicky-smoke-run "<text>"                gate + agent run through the openclicky CLI
//   --openclicky-smoke-transcribe <wav> [engine]   one dictation engine on a recording (offline, parakeet, sarvam, openclicky, assemblyai)
//   --openclicky-voice-smoke <voice>               two sentences rendered in one voice (SpeechVoiceChoice raw value)
//   --openclicky-check-source <source>             Settings' Test button for one answer source
//   --openclicky-answer-smoke                      one real question (a red square) through ClaudeAPI

using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenClicky.Core.Agent;
using OpenClicky.Core.Answers;
using OpenClicky.Core.Configuration;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Core.Providers;
using OpenClicky.Core.Speech;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows;

internal static class SmokeCommands
{
    public static int? TryRun(string[] args)
    {
        string? Argument(string flag, int offset = 1)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + offset < args.Length ? args[index + offset] : null;
        }

        if (args.Contains(PlatformSmoke.Flag))
        {
            ConsoleAttachment.Attach();
            return PlatformSmoke.RunAsync(Console.Out).GetAwaiter().GetResult();
        }
        if (Argument("--openclicky-smoke-run") is { } transcript)
        {
            ConsoleAttachment.Attach();
            return Task.Run(() => SmokeRunAsync(transcript)).GetAwaiter().GetResult();
        }
        if (Argument("--openclicky-smoke-transcribe") is { } wavPath)
        {
            ConsoleAttachment.Attach();
            var engineName = Argument("--openclicky-smoke-transcribe", 2) ?? "offline";
            return Task.Run(() => TranscribeAsync(wavPath, engineName)).GetAwaiter().GetResult();
        }
        if (Argument("--openclicky-voice-smoke") is { } voiceName)
        {
            ConsoleAttachment.Attach();
            return Task.Run(() => VoiceSmokeAsync(voiceName)).GetAwaiter().GetResult();
        }
        if (Argument("--openclicky-check-source") is { } sourceName)
        {
            ConsoleAttachment.Attach();
            return Task.Run(() => CheckSourceAsync(sourceName)).GetAwaiter().GetResult();
        }
        if (args.Contains("--openclicky-answer-smoke"))
        {
            ConsoleAttachment.Attach();
            return Task.Run(AnswerSmokeAsync).GetAwaiter().GetResult();
        }
        return null;
    }

    private static async Task<int> SmokeRunAsync(string transcript)
    {
        var agentClient = new OpenClickyAgentClient(new ProcessRunner());
        var lane = await agentClient.ClassifyLaneAsync(transcript);
        Console.WriteLine($"smoke: lane={lane}");
        if (lane != "agent") return 0;
        try
        {
            var result = await agentClient.RunAgentAsync(transcript, null, null, milestone => Console.WriteLine($"smoke: ▸ {milestone}"));
            Console.WriteLine($"smoke: status={result.Status} thread={result.ThreadId ?? "-"}");
            Console.WriteLine($"smoke: text={result.Text}");
            Console.WriteLine($"smoke: artifacts=[{string.Join(", ", result.Artifacts)}]");
            return result.Status == "completed" ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.WriteLine($"smoke: error={error.Message}");
            return 1;
        }
    }

    /// <summary>Any audio file as 16 kHz mono float samples.</summary>
    private static float[] LoadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider samples = reader;
        if (samples.WaveFormat.Channels > 1) samples = new StereoToMonoSampleProvider(samples);
        if (samples.WaveFormat.SampleRate != 16_000) samples = new WdlResamplingSampleProvider(samples, 16_000);
        var all = new List<float>();
        var buffer = new float[16_000];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.AsSpan(0, read).ToArray());
        return all.ToArray();
    }

    private static async Task<int> TranscribeAsync(string wavPath, string engineName)
    {
        if (DictationEngineChoices.FromRawValue(engineName) is not { } engine)
        {
            Console.WriteLine($"transcribe: unknown engine {engineName}");
            return 1;
        }
        var platform = new HeadlessPlatform();
        var settings = DictationSettings.Shared;
        if (engine == DictationEngineChoice.Parakeet && !ParakeetDictation.IsInstalled)
        {
            Console.WriteLine("transcribe: parakeet isn't downloaded; downloading it first");
            var parakeet = ParakeetDictation.Shared!;
            parakeet.Download();
            while (parakeet.State is not (ModelInstallState.InstalledState or ModelInstallState.FailedState)) await Task.Delay(500);
            if (parakeet.State is ModelInstallState.FailedState failed)
            {
                Console.WriteLine($"transcribe: download failed: {failed.Message}");
                return 1;
            }
        }
        var provider = DictationEngineResolver.MakeProvider(engine, settings, new DictationProviderFactory(platform.OfflineSpeech));
        Console.WriteLine($"transcribe: {provider.DisplayName} on {Path.GetFileName(wavPath)}");
        if (provider.UnavailableExplanation is { } explanation)
        {
            Console.WriteLine($"transcribe: unavailable: {explanation}");
            return 1;
        }
        var samples = LoadMono16k(wavPath);
        var finalWords = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var session = await provider.StartStreamingSessionAsync([],
            partial => Console.WriteLine($"transcribe: … {partial}"),
            words => finalWords.TrySetResult(words),
            error => finalWords.TrySetException(error));
        const int chunk = 1_600;
        for (var offset = 0; offset < samples.Length; offset += chunk)
        {
            var slice = samples.AsSpan(offset, Math.Min(chunk, samples.Length - offset)).ToArray();
            session.AppendAudio(new AudioFrame(slice, BuddyPCM16AudioConverter.FloatToPcm16(slice), 16_000, 0.1f));
            await Task.Delay(20);
        }
        session.RequestFinalTranscript();
        var finished = await Task.WhenAny(finalWords.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (finished != finalWords.Task)
        {
            Console.WriteLine("transcribe: no final words after 30 s");
            session.Cancel();
            return 1;
        }
        string raw;
        try { raw = await finalWords.Task; }
        catch (Exception error)
        {
            Console.WriteLine($"transcribe: error: {error.Message}");
            return 1;
        }
        var context = new TakeFormattingContext { Style = DictationStyle.Seeded()[0], Language = settings.Language };
        Console.WriteLine($"transcribe: raw \"{raw}\" in {stopwatch.Elapsed.TotalSeconds:0.0} s");
        Console.WriteLine($"transcribe: formatted \"{TakeFormatter.FormatLocally(raw, context).Text}\"");
        return raw.Trim().Length > 0 ? 0 : 2;
    }

    private static async Task<int> VoiceSmokeAsync(string voiceName)
    {
        _ = new HeadlessPlatform();
        var voice = SpeechVoiceChoices.FromStored(voiceName);
        if (voice == SpeechVoiceChoice.Kokoro && !KokoroVoice.IsInstalled)
        {
            var kokoro = KokoroVoice.Shared!;
            kokoro.Download();
            var lastTenth = -1;
            while (kokoro.State is not (ModelInstallState.InstalledState or ModelInstallState.FailedState))
            {
                if (kokoro.State is ModelInstallState.DownloadingState downloading && (int)(downloading.Fraction * 10) != lastTenth)
                {
                    lastTenth = (int)(downloading.Fraction * 10);
                    Console.WriteLine($"voice-smoke: kokoro downloading {lastTenth * 10}%");
                }
                await Task.Delay(500);
            }
            if (kokoro.State is ModelInstallState.FailedState failed)
            {
                Console.WriteLine($"voice-smoke: kokoro download failed: {failed.Message}");
                return 1;
            }
            Console.WriteLine("voice-smoke: kokoro installed");
        }
        foreach (var sentence in new[] { "see that blue button up top? click it to start.", "then choose save as from the file menu, and pick your desktop." })
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var audio = await CloudVoiceSpeaker.AudioAsync(sentence, voice);
                Console.WriteLine($"voice-smoke: {voice.RawValue()} rendered {audio.Length / 1024} KB ({AudioSeconds(audio):0.0} s of audio) in {stopwatch.Elapsed.TotalSeconds:0.00} s");
            }
            catch (Exception error)
            {
                Console.WriteLine($"voice-smoke: {voice.RawValue()} error {error.Message}");
                return 1;
            }
        }
        return 0;
    }

    /// <summary>How long a WAV or MP3 plays, when NAudio can read it.</summary>
    private static double AudioSeconds(byte[] audio)
    {
        try
        {
            using var stream = new MemoryStream(audio);
            using WaveStream reader = audio.Length > 4 && audio[0] == 'R' && audio[1] == 'I' ? new WaveFileReader(stream) : new Mp3FileReader(stream);
            return reader.TotalTime.TotalSeconds;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static async Task<int> CheckSourceAsync(string sourceName)
    {
        if (AnswerSources.FromRawValue(sourceName) is not { } source)
        {
            Console.WriteLine("check-source: unknown source");
            return 1;
        }
        var outcome = await AnswerSourceCheck.RunAsync(source, ProviderRouter.CurrentCredentials(), OpenClickyConfiguration.Settings.AnswerModels ?? new Dictionary<string, string>());
        Console.WriteLine($"check-source: {sourceName}: {outcome.Sentence}");
        return outcome is AnswerSourceCheck.Outcome.Works ? 0 : 1;
    }

    private static async Task<int> AnswerSmokeAsync()
    {
        var route = ProviderRouter.Current(ProviderCapability.Claude);
        Console.WriteLine($"answer-smoke: route {route.LogName}");
        var api = new ClaudeAPI(() => $"{OpenClickyConfiguration.BackendBaseUrl}/chat");
        var chunks = 0;
        try
        {
            var (text, toolCalls, duration) = await api.AnalyzeImageStreamingAsync(
                [(AnswerSourceCheck.RedSquarePng(), "a test image")], "answer in lowercase, one word.", null,
                "what color is this square?", null, _ => Interlocked.Increment(ref chunks));
            Console.WriteLine($"answer-smoke: \"{text}\" in {duration.TotalSeconds:0.0} s, {chunks} chunk updates, {toolCalls.Count} tool calls");
            return text.Contains("red", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.WriteLine($"answer-smoke: error {error.Message}");
            return 1;
        }
    }

    /// <summary>The few services the headless checks need, with the local voices registered as in the app.</summary>
    private sealed class HeadlessPlatform
    {
        public IOfflineSpeechRecognizer OfflineSpeech { get; } = new WindowsSpeechRecognizer();

        public HeadlessPlatform()
        {
            AppComposition.RegisterLocalModels(new ProcessRunner(), new Platform.NetworkReachability(), Preferences.Shared, mainThread: null);
            SpeechVoiceServices.Current = SpeechVoiceServices.ForThisPC(new WindowsSystemVoice());
        }
    }
}
