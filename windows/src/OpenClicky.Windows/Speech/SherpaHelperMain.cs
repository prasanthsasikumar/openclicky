// The helper processes for Kokoro and Parakeet (KokoroHelper.swift, ParakeetEngine.swift's
// ParakeetHelper, KokoroEngine.swift): OpenClicky.exe started with a flag, caught in Program.Main
// before any UI exists. One JSON line each way over stdin/stdout (SherpaHelperProtocol); the
// helper ends when the app closes its stdin, or after 180 s without a request (the app ends it
// sooner itself; this is so an orphan never lingers).
//
// The engines run on sherpa-onnx's .NET API (org.k2fsa.sherpa.onnx 1.13.8, the same version the
// Mac embeds), configured as the Mac configures the C API.

using System.IO;
using System.Text;
using OpenClicky.Core.Speech;
using SherpaOnnx;

namespace OpenClicky.Windows.Speech;

public static class SherpaHelperMain
{
    public const string KokoroFlag = KokoroVoice.HelperFlag;
    public const string ParakeetFlag = ParakeetDictation.HelperFlag;

    /// <summary>How long a helper waits for a request before it ends on its own.</summary>
    public static readonly TimeSpan IdleExit = TimeSpan.FromSeconds(180);

    public static bool IsHelperInvocation(string[] args) => args.Any(argument => argument is KokoroFlag or ParakeetFlag);

    /// <summary>Runs the helper named in <paramref name="args"/> until stdin closes; returns the exit code.</summary>
    public static int Run(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        var idleInput = new IdleExitReader(input, IdleExit);
        if (args.Contains(KokoroFlag))
        {
            using var engine = new KokoroHelperEngine();
            SherpaHelperProtocol.Run<KokoroEngineRequest, KokoroHelperReply>(idleInput, output, request => new KokoroHelperReply(Convert.ToBase64String(engine.Generate(request))));
            return 0;
        }
        if (args.Contains(ParakeetFlag))
        {
            using var engine = new ParakeetHelperEngine();
            SherpaHelperProtocol.Run<ParakeetEngineRequest, ParakeetEngineReply>(idleInput, output, engine.Transcribe);
            return 0;
        }
        return 2;
    }

    /// <summary>Workers for the engines: min(4, max(1, cores − 1)).</summary>
    internal static int ThreadCount => Math.Min(4, Math.Max(1, Environment.ProcessorCount - 1));

    /// <summary>A reader whose ReadLine gives up (returns null, ending the helper) after a quiet spell.</summary>
    private sealed class IdleExitReader(TextReader inner, TimeSpan idleLimit) : TextReader
    {
        public override string? ReadLine()
        {
            var read = Task.Run(inner.ReadLine);
            return read.Wait(idleLimit) ? read.Result : null;
        }
    }
}

/// <summary>Kokoro through sherpa-onnx's OfflineTts (KokoroEngine.swift). Only ever used inside the helper.</summary>
internal sealed class KokoroHelperEngine : IDisposable
{
    private OfflineTts? tts;
    private string? loadedLanguageHint;
    private string? loadedModelFolder;

    /// <summary>The text spoken, as WAV bytes; loads the model first if it isn't (about 0.8 s).</summary>
    public byte[] Generate(KokoroEngineRequest request)
    {
        if (tts is null || loadedLanguageHint != request.LanguageHint || loadedModelFolder != request.ModelFolder)
        {
            tts?.Dispose();
            tts = null;
            var startedAt = DateTime.UtcNow;
            try
            {
                tts = new OfflineTts(Config(request));
            }
            catch (Exception)
            {
                throw new KokoroException(KokoroException.ModelNotLoadedMessage);
            }
            loadedLanguageHint = request.LanguageHint;
            loadedModelFolder = request.ModelFolder;
            Console.Error.WriteLine($"kokoro: model loaded in {(DateTime.UtcNow - startedAt).TotalSeconds:0.0} s");
        }
        var audio = tts.Generate(request.Text, 1.0f, request.SpeakerId);
        try
        {
            var samples = audio.Samples;
            if (samples is null || samples.Length == 0) throw new KokoroException(KokoroException.NothingGeneratedMessage);
            return KokoroEngine.Wav(samples, audio.SampleRate);
        }
        finally
        {
            audio.Dispose();
        }
    }

    private static OfflineTtsConfig Config(KokoroEngineRequest request)
    {
        var model = request.ModelFolder;
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = Path.Combine(model, "model.onnx");
        config.Model.Kokoro.Voices = Path.Combine(model, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(model, "tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(model, "espeak-ng-data");
        config.Model.Kokoro.DictDir = Path.Combine(model, "dict");
        config.Model.Kokoro.LengthScale = 1.0f;
        if (request.LanguageHint is "en-us" or "zh")
        {
            config.Model.Kokoro.Lexicon = string.Join(",", new[] { "lexicon-us-en.txt", "lexicon-zh.txt" }.Select(name => Path.Combine(model, name)));
        }
        else
        {
            config.Model.Kokoro.Lang = request.LanguageHint;
        }
        config.Model.NumThreads = SherpaHelperMain.ThreadCount;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        config.SilenceScale = 0.2f;
        return config;
    }

    public void Dispose() => tts?.Dispose();
}

/// <summary>NVIDIA's Parakeet TDT 0.6B v3 (int8) through sherpa-onnx's OfflineRecognizer (ParakeetEngine.swift).</summary>
internal sealed class ParakeetHelperEngine : IDisposable
{
    private OfflineRecognizer? recognizer;

    /// <summary>The words in the request's audio; loads the model first if it isn't. An empty request only
    /// loads it (the warm-up when the dictation key goes down).</summary>
    public ParakeetEngineReply Transcribe(ParakeetEngineRequest request)
    {
        if (recognizer is null)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                recognizer = new OfflineRecognizer(Config(request.ModelFolder));
            }
            catch (Exception)
            {
                throw new ParakeetException(ParakeetException.ModelNotLoadedMessage);
            }
            Console.Error.WriteLine($"parakeet: model loaded in {(DateTime.UtcNow - startedAt).TotalSeconds:0.0} s");
        }
        byte[] pcm16;
        try { pcm16 = Convert.FromBase64String(request.Pcm16Base64); }
        catch (FormatException) { throw new ParakeetException(ParakeetException.UnreadableAudioMessage); }
        var samples = ParakeetEngine.FloatSamples(pcm16);
        if (samples.Length == 0) return new ParakeetEngineReply("");
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(ParakeetEngine.SampleRate, samples);
        recognizer.Decode(stream);
        return new ParakeetEngineReply((stream.Result?.Text ?? "").Trim());
    }

    private static OfflineRecognizerConfig Config(string modelFolder)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = ParakeetEngine.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelFolder, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelFolder, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelFolder, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelFolder, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = SherpaHelperMain.ThreadCount;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        return config;
    }

    public void Dispose() => recognizer?.Dispose();
}
