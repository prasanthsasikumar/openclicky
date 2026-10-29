//
//  StandardHandsFree.cs
//  OpenClicky (Windows port of StandardHandsFree.swift's detector and sign-offs)
//
//  Hands-free without a realtime key: the microphone stays open, a spoken turn is cut out of it on
//  this PC (loudness against the room's own noise, half a second kept from before the speech so the
//  first word is whole), transcribed by the ask lane's engine, and asked exactly as if Ctrl + Win had
//  been held for it. Slower than fast voice (the answer comes after the person stops, not while they
//  finish), but it works on any key, a plan, or a free account. The microphone and the gate that keeps
//  it deaf while Clicky answers are CompanionManager.StandardHandsFree.cs.
//

using System.Text;

namespace OpenClicky.Core.Speech;

/// <summary>Finds spoken turns in a stream of 20 ms frames (16 kHz mono). Pure, so it is unit-tested with
/// synthetic audio (StandardHandsFreeTests).</summary>
public sealed class UtteranceDetector
{
    public abstract record Event
    {
        public sealed record Started : Event;
        /// <summary>The turn's samples, from half a second before it started to just after its last frame of speech.</summary>
        public sealed record Finished(float[] Samples) : Event;
        /// <summary>Too short to be speech (a cough, a click, a door).</summary>
        public sealed record Discarded : Event;
    }

    public const int SampleRate = 16_000;
    public const int FrameLength = 320;
    /// <summary>Frames of 20 ms.</summary>
    public const int PrerollFrames = 25;
    public const int FramesToStart = 8;
    public const int StartWindow = 10;
    public const int SilenceFramesToEnd = 40;
    public const int MinimumSpeechFrames = 15;
    public const int MaximumFrames = 30 * 50;
    /// <summary>Below this nothing counts as speech, however quiet the room.</summary>
    public const float MinimumLevel = 0.006f;
    /// <summary>Speech is this many times louder than the room's noise.</summary>
    public const float LevelOverNoise = 3;
    /// <summary>The room's noise is read from the quieter end of the last three seconds (minimum statistics): speech
    /// has gaps between words, steady sound (a fan, music) does not, so this follows the room up and down without
    /// taking a voice for noise.</summary>
    public const int NoiseWindowFrames = 150;
    /// <summary>No turn starts before a second of the room has been heard (a voice at once would otherwise set the
    /// bar, and in a loud room a turn would never end).</summary>
    public const int WarmUpFrames = 50;

    public float NoiseFloor { get; private set; } = MinimumLevel / LevelOverNoise;
    public bool IsInTurn { get; private set; }

    private readonly List<float> recentLevels = [];
    private readonly Queue<float[]> recentFrames = new();
    private readonly Queue<bool> recentWasSpeech = new();
    private readonly List<float> turn = [];
    private int turnFrames;
    private int speechFrames;
    private int silentRun;
    private int samplesUpToLastSpeech;

    public static float Level(float[] frame)
    {
        if (frame.Length == 0) return 0;
        double sum = 0;
        foreach (var sample in frame) sum += sample * sample;
        return (float)Math.Sqrt(sum / frame.Length);
    }

    public bool IsSpeech(float level) => level > Math.Max(MinimumLevel, NoiseFloor * LevelOverNoise);

    /// <summary>The tenth-quietest percent of the window, so one dropout does not pull the floor down.</summary>
    private void NoteLevel(float level)
    {
        recentLevels.Add(level);
        if (recentLevels.Count > NoiseWindowFrames) recentLevels.RemoveAt(0);
        var sorted = recentLevels.Order().ToList();
        NoiseFloor = sorted[Math.Min(sorted.Count - 1, sorted.Count / 10)];
    }

    /// <summary>Starts over, room included (hands-free paused, or its microphone reopened).</summary>
    public void Reset()
    {
        recentLevels.Clear();
        NoiseFloor = MinimumLevel / LevelOverNoise;
        EndTurn();
    }

    private void EndTurn()
    {
        IsInTurn = false;
        recentFrames.Clear();
        recentWasSpeech.Clear();
        turn.Clear();
        turnFrames = 0;
        speechFrames = 0;
        silentRun = 0;
        samplesUpToLastSpeech = 0;
    }

    public Event? Process(float[] frame)
    {
        var level = Level(frame);
        NoteLevel(level);
        var speech = IsSpeech(level);
        if (!IsInTurn)
        {
            recentFrames.Enqueue(frame);
            recentWasSpeech.Enqueue(speech);
            if (recentFrames.Count > PrerollFrames) recentFrames.Dequeue();
            if (recentWasSpeech.Count > StartWindow) recentWasSpeech.Dequeue();
            if (recentLevels.Count < WarmUpFrames || recentWasSpeech.Count(isSpeech => isSpeech) < FramesToStart) return null;
            IsInTurn = true;
            turn.AddRange(recentFrames.SelectMany(recentFrame => recentFrame));
            turnFrames = recentFrames.Count;
            speechFrames = recentWasSpeech.Count(isSpeech => isSpeech);
            silentRun = 0;
            samplesUpToLastSpeech = turn.Count;
            recentFrames.Clear();
            recentWasSpeech.Clear();
            return new Event.Started();
        }
        turn.AddRange(frame);
        turnFrames++;
        if (speech)
        {
            speechFrames++;
            silentRun = 0;
            samplesUpToLastSpeech = turn.Count;
        }
        else
        {
            silentRun++;
        }
        if (silentRun < SilenceFramesToEnd && turnFrames < MaximumFrames) return null;
        var enoughSpeech = speechFrames >= MinimumSpeechFrames;
        // A little of the quiet after the last word stays, so its ending is not clipped.
        var tail = Math.Min(turn.Count, samplesUpToLastSpeech + FrameLength * 10);
        var samples = turn.GetRange(0, tail).ToArray();
        EndTurn();
        return enoughSpeech ? new Event.Finished(samples) : new Event.Discarded();
    }

    /// <summary>16 kHz mono float samples as 16-bit little-endian PCM, what the transcription engines take.</summary>
    public static byte[] Pcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var value = (short)(Math.Clamp(samples[index], -1f, 1f) * short.MaxValue);
            bytes[index * 2] = (byte)(value & 0xFF);
            bytes[index * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return bytes;
    }
}

/// <summary>"That's all, thanks" ends hands-free on the standard lane. Fast voice lets its model decide
/// (stop_listening); here a model round trip would cost seconds, so the phrases are fixed and narrow: a whole
/// turn of sign-off, never "stop the music" or "how do I quit Photoshop".</summary>
public static class HandsFreeSignOff
{
    private static readonly HashSet<string> Phrases =
    [
        "that's all", "thats all", "that is all", "that's all i need", "that's all thanks", "that's all thank you",
        "that's it", "that's it for now", "that's it thanks", "stop listening", "you can stop listening",
        "ok bye", "okay bye", "bye", "goodbye", "bye clicky", "goodbye clicky", "i'm done", "im done", "i'm done thanks",
        "we're done", "that'll be all", "that will be all", "thanks that's all", "thank you that's all",
    ];

    /// <summary>Politeness around a sign-off that does not change it: "okay, that's all I need for now" is "that's all
    /// I need" (a tester's goodbye that kept hands-free on, 2026-10-29). Only whole words at either end are dropped, so
    /// "that's all I needed to know about refraction" still is not one.</summary>
    private static readonly string[][] LeadingFiller =
    [
        ["okay"], ["ok"], ["alright"], ["all", "right"], ["right"], ["cool"], ["great"], ["perfect"], ["awesome"],
        ["thanks"], ["thank", "you"], ["so"], ["well"], ["yeah"], ["yes"], ["hey"], ["clicky"],
    ];

    private static readonly string[][] TrailingFiller =
    [
        ["for", "now"], ["for", "today"], ["now"], ["then"], ["thanks"], ["thank", "you"], ["clicky"], ["bye"],
    ];

    public static bool IsSignOff(string transcript)
    {
        var words = Words(transcript);
        if (words.Count == 0) return false;
        if (Phrases.Contains(string.Join(' ', words))) return true;
        // Peel filler off both ends, one piece at a time, checking after each.
        var start = 0;
        var end = words.Count;
        var peeled = true;
        while (peeled && start < end)
        {
            peeled = false;
            if (StartsWith(words, start, end, LeadingFiller) is { } leading)
            {
                start += leading;
                peeled = true;
            }
            else if (EndsWith(words, start, end, TrailingFiller) is { } trailing)
            {
                end -= trailing;
                peeled = true;
            }
            if (peeled && start < end && Phrases.Contains(string.Join(' ', words.Skip(start).Take(end - start)))) return true;
        }
        return false;
    }

    private static int? StartsWith(List<string> words, int start, int end, string[][] fillers)
    {
        foreach (var filler in fillers)
        {
            if (end - start > filler.Length && words.Skip(start).Take(filler.Length).SequenceEqual(filler)) return filler.Length;
        }
        return null;
    }

    private static int? EndsWith(List<string> words, int start, int end, string[][] fillers)
    {
        foreach (var filler in fillers)
        {
            if (end - start > filler.Length && words.Skip(end - filler.Length).Take(filler.Length).SequenceEqual(filler)) return filler.Length;
        }
        return null;
    }

    private static List<string> Words(string transcript)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var character in transcript.ToLowerInvariant().Replace('’', '\''))
        {
            if (char.IsLetter(character) || character == '\'')
            {
                word.Append(character);
                continue;
            }
            if (word.Length > 0) words.Add(word.ToString());
            word.Clear();
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }
}
