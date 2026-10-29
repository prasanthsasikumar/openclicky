// Ports StandardHandsFreeTests.swift: cutting spoken turns out of the microphone, the fixed sign-offs, and
// listening only while Clicky is idle and has been quiet long enough.

using OpenClicky.Core.Companion;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Tests.Speech;

public class StandardHandsFreeTests
{
    /// <summary>20 ms of a tone (speech-loud) or of faint noise.</summary>
    private static float[] Frame(bool loud, int phase) =>
        Enumerable.Range(0, UtteranceDetector.FrameLength).Select(index =>
        {
            var time = (float)(phase * UtteranceDetector.FrameLength + index) / UtteranceDetector.SampleRate;
            return loud ? 0.2f * MathF.Sin(2 * MathF.PI * 220 * time) : 0.001f * MathF.Sin(2 * MathF.PI * 50 * time);
        }).ToArray();

    private static List<UtteranceDetector.Event> Run(UtteranceDetector detector, int quiet, int loud, int thenQuiet)
    {
        var events = new List<UtteranceDetector.Event>();
        var phase = 0;
        void Feed(bool isLoud, int count)
        {
            for (var index = 0; index < count; index++)
            {
                if (detector.Process(Frame(isLoud, phase)) is { } detected) events.Add(detected);
                phase++;
            }
        }
        Feed(false, quiet);
        Feed(true, loud);
        Feed(false, thenQuiet);
        return events;
    }

    [Fact]
    public void ASpokenTurnKeepsItsStartAndEndsAfterAPause()
    {
        // A second of room, a second of speech, then a second of quiet.
        var events = Run(new UtteranceDetector(), quiet: 50, loud: 50, thenQuiet: 50);
        Assert.IsType<UtteranceDetector.Event.Started>(events.First());
        var finished = Assert.IsType<UtteranceDetector.Event.Finished>(events.Last());
        // The half second before the voice was detected is kept, so the first word is whole, and a little quiet
        // after the last word.
        var seconds = (double)finished.Samples.Length / UtteranceDetector.SampleRate;
        Assert.InRange(seconds, 1.2, 1.9);
    }

    [Fact]
    public void AShortSoundIsNotATurn()
    {
        var events = Run(new UtteranceDetector(), quiet: 50, loud: 10, thenQuiet: 50);
        Assert.Collection(events,
            started => Assert.IsType<UtteranceDetector.Event.Started>(started),
            discarded => Assert.IsType<UtteranceDetector.Event.Discarded>(discarded));
    }

    [Fact]
    public void APauseShorterThanTheEndingKeepsTheTurnGoing()
    {
        var detector = new UtteranceDetector();
        var events = Run(detector, quiet: 50, loud: 30, thenQuiet: 20);
        events.AddRange(Run(detector, quiet: 0, loud: 30, thenQuiet: 50));
        Assert.Single(events.OfType<UtteranceDetector.Event.Started>());
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void RoomNoiseAloneNeverStartsATurn()
    {
        Assert.Empty(Run(new UtteranceDetector(), quiet: 500, loud: 0, thenQuiet: 0));
    }

    [Fact]
    public void SamplesBecomeLittleEndianPcm16()
    {
        Assert.Equal(new byte[] { 0, 0, 0xFF, 0x3F, 0x01, 0xC0 }, UtteranceDetector.Pcm16([0, 0.5f, -0.5f]));
    }

    [Fact]
    public void SignOffsAreWholeTurnsOnly()
    {
        Assert.True(HandsFreeSignOff.IsSignOff("That's all, thanks."));
        Assert.True(HandsFreeSignOff.IsSignOff("OK bye"));
        Assert.True(HandsFreeSignOff.IsSignOff("Stop listening!"));
        Assert.True(HandsFreeSignOff.IsSignOff("that’s it for now"));
        Assert.False(HandsFreeSignOff.IsSignOff("thank you"));
        Assert.False(HandsFreeSignOff.IsSignOff("stop the music"));
        Assert.False(HandsFreeSignOff.IsSignOff("how do I quit Photoshop"));
        Assert.False(HandsFreeSignOff.IsSignOff("that's all I needed to know about refraction, now explain lenses"));
    }

    [Fact]
    public void ListensOnlyWhenIdleAndQuietLongEnough()
    {
        Assert.True(CompanionManager.StandardHandsFreeAccepts(isOn: true, isBusy: false, quietFor: TimeSpan.FromSeconds(1)));
        Assert.False(CompanionManager.StandardHandsFreeAccepts(isOn: true, isBusy: false, quietFor: TimeSpan.FromSeconds(0.2)));
        Assert.False(CompanionManager.StandardHandsFreeAccepts(isOn: true, isBusy: true, quietFor: TimeSpan.FromSeconds(5)));
        Assert.False(CompanionManager.StandardHandsFreeAccepts(isOn: false, isBusy: false, quietFor: TimeSpan.FromSeconds(5)));
        Assert.False(CompanionManager.StandardHandsFreeAccepts(isOn: true, isBusy: false, quietFor: null));
    }
}
