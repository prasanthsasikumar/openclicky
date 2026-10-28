// Ports ParakeetDictationTests.swift: which languages Parakeet takes, when it replaces the old
// offline engine, and the session's live and final words (the helper replaced by an injected
// transcriber). The "engine says what it needs" test exercises DictationEngineResolver, which
// belongs to the dictation port and is tested there.

using System.Buffers.Binary;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class ParakeetDictationTests
{
    [Fact]
    public void HearsAutoDetectAndEuropeanLanguagesButNotIndianOnes()
    {
        Assert.True(ParakeetDictation.Hears(null));
        Assert.True(ParakeetDictation.Hears("en"));
        Assert.True(ParakeetDictation.Hears("de"));
        Assert.False(ParakeetDictation.Hears("hi"));
        Assert.False(ParakeetDictation.Hears("ml"));
    }

    [Fact]
    public void AnAutomaticDownloadReplacesOnlyTheOldOfflineEngine()
    {
        Assert.Equal("parakeet", ParakeetDictation.EngineAfterAutomaticDownload("offline"));
        Assert.Equal("parakeet", ParakeetDictation.EngineAfterAutomaticDownload("moonshine"));
        Assert.Equal("sarvam", ParakeetDictation.EngineAfterAutomaticDownload("sarvam"));
        Assert.Equal("openclicky", ParakeetDictation.EngineAfterAutomaticDownload("openclicky"));
    }

    [Fact]
    public void Pcm16BecomesFloatSamples()
    {
        var pcm = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(0), 0);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(4), -32768);
        Assert.Equal([0f, 0.5f, -1f], ParakeetEngine.FloatSamples(pcm));
    }

    [Fact]
    public void TheModelIsPinnedAsOnTheMac()
    {
        Assert.Equal("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
            ParakeetDictation.ModelDownload.Url.ToString());
        Assert.Equal("5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf", ParakeetDictation.ModelDownload.Sha256);
        Assert.Equal(487_170_055, ParakeetDictation.ModelDownload.Bytes);
    }

    [Fact]
    public async Task TheFinalWordsReadTheWholeTake()
    {
        var heard = new HeardLog();
        var session = new ParakeetTranscriptionSession(_ => { }, heard.Final, _ => { },
            (audio, _) => Task.FromResult($"{audio.Length} bytes"));
        session.AppendPcm16(new byte[3200]);
        session.AppendPcm16(new byte[3200]);
        session.RequestFinalTranscript();
        Assert.Equal("6400 bytes", await heard.WaitForFinalAsync());
    }

    [Fact]
    public async Task ACancelledTakeSaysNothing()
    {
        var heard = new HeardLog();
        var session = new ParakeetTranscriptionSession(_ => { }, heard.Final, _ => { },
            async (_, _) => { await Task.Delay(200); return "words"; });
        session.AppendPcm16(new byte[3200]);
        session.RequestFinalTranscript();
        session.Cancel();
        await Task.Delay(500);
        Assert.Empty(heard.Finals);
    }

    [Fact]
    public async Task LiveWordsArriveWhileTheKeyIsHeld()
    {
        var heard = new HeardLog();
        var session = new ParakeetTranscriptionSession(heard.Partial, heard.Final, _ => { },
            (_, _) => Task.FromResult("so far"));
        session.AppendPcm16(new byte[32_000]);
        await Task.Delay(1_400);
        Assert.Contains("so far", heard.Partials);
        session.Cancel();
    }

    /// <summary>Every live read re-reads the whole take, so past 30 s of audio the live words stop; the
    /// final words still read all of it.</summary>
    [Fact]
    public async Task LiveWordsStopPastThirtySecondsButTheFinalReadsEverything()
    {
        var heard = new HeardLog();
        var session = new ParakeetTranscriptionSession(heard.Partial, heard.Final, _ => { },
            (audio, _) => Task.FromResult($"{audio.Length} bytes"));
        var longTakeBytes = ParakeetTranscriptionSession.LiveWordsAudioLimitBytes + 32_000;
        session.AppendPcm16(new byte[longTakeBytes]);
        await Task.Delay(1_400);
        Assert.Empty(heard.Partials);
        session.RequestFinalTranscript();
        Assert.Equal($"{longTakeBytes} bytes", await heard.WaitForFinalAsync());
    }

    /// <summary>The live-words loop holds the session only weakly: a session nobody keeps goes away.</summary>
    [Fact]
    public void TheLiveWordsLoopDoesNotKeepTheSessionAlive()
    {
        var releasedSession = MakeAndDropSession();
        for (var attempt = 0; attempt < 20 && !IsReleased(releasedSession); attempt++)
        {
            Thread.Sleep(50);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.True(IsReleased(releasedSession));
    }

    /// <summary>Checked in its own frame, so the test method never holds the target itself.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsReleased(WeakReference<ParakeetTranscriptionSession> reference) => !reference.TryGetTarget(out _);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<ParakeetTranscriptionSession> MakeAndDropSession() =>
        new(new ParakeetTranscriptionSession(_ => { }, _ => { }, _ => { }, (_, _) => Task.FromResult("words")));

    /// <summary>What a session reported, from whichever thread it reported on.</summary>
    private sealed class HeardLog
    {
        private readonly object listLock = new();
        private readonly List<string> finals = [];
        private readonly List<string> partials = [];

        public IReadOnlyList<string> Finals { get { lock (listLock) return finals.ToList(); } }
        public IReadOnlyList<string> Partials { get { lock (listLock) return partials.ToList(); } }
        public void Final(string text) { lock (listLock) finals.Add(text); }
        public void Partial(string text) { lock (listLock) partials.Add(text); }

        public async Task<string?> WaitForFinalAsync()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (Finals.Count > 0) return Finals[0];
                await Task.Delay(20);
            }
            return null;
        }
    }
}
