using OpenClicky.Core.Platform;

namespace OpenClicky.Core.Tests.Platform;

/// <summary>How a paste is judged after the field is read back (PasteLandingTests in LiveTypingTests.swift).</summary>
public class PasteLandingTests
{
    private static FocusedFieldSnapshot Field(string? role = "Edit", bool editable = true, string? value = null) =>
        new() { AppProcessName = "notepad.exe", AppName = "Notepad", Role = role, IsEditable = editable, Value = value };

    [Fact]
    public void TextFoundInTheFieldIsVerified() =>
        Assert.Equal(PasteLandingResult.Verified, PasteLanding.Landing("hello there", Field(value: ""), Field(value: "hello there")));

    [Fact]
    public void NothingEditableWithFocusIsNoTarget()
    {
        var noElement = Field(role: null, editable: false);
        var notEditable = Field(role: "Group", editable: false);
        Assert.Equal(PasteLandingResult.NoTarget, PasteLanding.Landing("hello there", Field(value: ""), noElement));
        Assert.Equal(PasteLandingResult.NoTarget, PasteLanding.Landing("hello there", Field(value: ""), notEditable));
        Assert.False(PasteLanding.KeepsPastedWordsOnClipboard(Field(value: ""), noElement));
        Assert.False(PasteLanding.KeepsPastedWordsOnClipboard(Field(value: ""), notEditable));
    }

    [Fact]
    public void AnEditableFieldThatCannotBeReadBackIsPostedAndKeepsTheWords()
    {
        var page = Field(role: "Document", editable: true, value: null);
        Assert.Equal(PasteLandingResult.Posted, PasteLanding.Landing("hello there", Field(role: "Document"), page));
        Assert.True(PasteLanding.KeepsPastedWordsOnClipboard(Field(role: "Document"), page));
        Assert.False(PasteLanding.KeepsPastedWordsOnClipboard(Field(value: ""), Field(value: "hello there")));
    }

    [Fact]
    public void AReadableFieldWithoutTheTextIsPosted() =>
        Assert.Equal(PasteLandingResult.Posted, PasteLanding.Landing("hello there", Field(value: "abc"), Field(value: "abc")));

    [Fact]
    public void AnotherAppInFrontIsPosted() =>
        Assert.Equal(PasteLandingResult.Posted, PasteLanding.Landing("hello", Field(value: ""), Field(value: "hello") with { AppProcessName = "chrome.exe" }));

    [Fact]
    public void AFieldThatGrewByHalfTheTextIsVerified() =>
        Assert.Equal(PasteLandingResult.Verified, PasteLanding.Landing("abcdefgh", Field(value: "xy"), Field(value: "xy1234")));
}

public class KeyboardTextChunkingTests
{
    [Fact]
    public void TextIsCutIntoChunksOfAtMostTwentyUtf16Units()
    {
        var text = string.Concat(Enumerable.Repeat("abcde", 9));
        var chunks = LiveTypingChunker.KeyboardChunks(text);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 20));
        Assert.Equal(3, chunks.Count);
    }

    [Fact]
    public void SurrogatePairsAreNeverSplit()
    {
        var text = new string('a', 19) + "😀" + "b";
        var chunks = LiveTypingChunker.KeyboardChunks(text);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 20));
        Assert.Equal(new string('a', 19), chunks[0]);
    }

    [Fact]
    public void ALongEmojiSequenceIsSplitBetweenScalarsOnly()
    {
        // Five family emoji joined into one grapheme of more than 20 UTF-16 units.
        var family = "👨‍👩‍👧‍👦";
        var text = family + family;
        var chunks = LiveTypingChunker.KeyboardChunks(text);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk =>
        {
            Assert.True(chunk.Length <= 20);
            Assert.False(char.IsHighSurrogate(chunk[^1]));
        });
    }
}

public class LiveTypingTargetTests
{
    [Fact]
    public void OnlyRealTextRolesAreTypedInto()
    {
        Assert.True(FocusedTextTarget.IsLiveTypingTextRole("Edit"));
        Assert.True(FocusedTextTarget.IsLiveTypingTextRole("ComboBox"));
        // A web page that has focus turns letters into single-key shortcuts (Gmail's e, #, !).
        foreach (var role in new[] { "Document", "Group", "Text", "Custom", null })
        {
            Assert.False(FocusedTextTarget.IsLiveTypingTextRole(role));
        }
        Assert.False(new FocusedTextTarget { Role = "Edit", IsSecure = true }.AcceptsLiveTyping);
    }

    [Fact]
    public void TheSameElementNeedsTheSamePidAndRuntimeId()
    {
        var target = new FocusedTextTarget { ProcessId = 42, RuntimeId = "42.7.3" };
        Assert.True(target.IsSameElement(new FocusedTextTarget { ProcessId = 42, RuntimeId = "42.7.3" }));
        Assert.False(target.IsSameElement(new FocusedTextTarget { ProcessId = 43, RuntimeId = "42.7.3" }));
        Assert.False(target.IsSameElement(new FocusedTextTarget { ProcessId = 42, RuntimeId = "42.7.4" }));
        Assert.False(new FocusedTextTarget { ProcessId = 42 }.IsSameElement(new FocusedTextTarget { ProcessId = 42 }));
    }
}

public class SelectedTextLimitsTests
{
    [Fact]
    public void ASelectionIsTidiedAndCapped()
    {
        Assert.Equal("two words", SelectedTextLimits.Tidied("  two\n  words "));
        Assert.Null(SelectedTextLimits.Tidied(" \n "));
        var tidied = SelectedTextLimits.Tidied(new string('a', 700));
        Assert.Equal(SelectedTextLimits.MaximumCharacters + 1, tidied!.Length);
        Assert.EndsWith("…", tidied);
    }
}

public class CompanionScreenCaptureTests
{
    private static CompanionScreenCapture Capture(PixelRect frame, int width, int height) =>
        new([], "image/jpeg", "screen", true, frame.Width / 2, frame.Height / 2, frame, 2, @"\\.\DISPLAY2", width, height);

    [Fact]
    public void GlobalPointsMapIntoTheScreenshotAndBack()
    {
        // A 2560×1440 monitor to the right of the primary, captured at 1280×720.
        var capture = Capture(new PixelRect(1920, 0, 2560, 1440), 1280, 720);
        var (x, y) = capture.ScreenshotPoint(new PixelPoint(1920 + 1280, 720));
        Assert.Equal(640, x, 3);
        Assert.Equal(360, y, 3);
        Assert.Equal(new PixelPoint(3200, 720), capture.GlobalPoint(640, 360));
        Assert.True(capture.ContainsScreenshotPoint(x, y));
        Assert.False(capture.ContainsScreenshotPoint(-1, 10));
    }

    [Fact]
    public void LabelsAreTheMacs()
    {
        Assert.Equal("user's screen (cursor is here)", CompanionScreenCapture.LabelFor(0, 1, true));
        Assert.Equal("screen 1 of 2, cursor is on this screen (primary focus)", CompanionScreenCapture.LabelFor(0, 2, true));
        Assert.Equal("screen 2 of 2, secondary screen", CompanionScreenCapture.LabelFor(1, 2, false));
    }

    [Fact]
    public void TheLongerSideIsCappedAndAspectKept()
    {
        Assert.Equal((1280, 831), CompanionScreenCapture.ScaledSize(3024, 1964, 1280));
        Assert.Equal((720, 1280), CompanionScreenCapture.ScaledSize(1440, 2560, 1280));
        Assert.Equal((800, 600), CompanionScreenCapture.ScaledSize(800, 600, 1280));
        Assert.Equal((3024, 1964), CompanionScreenCapture.ScaledSize(3024, 1964, 0));
    }
}

public class DictationEarconSoundsTests
{
    [Fact]
    public void EachEarconIsAWavOfItsNotesLength()
    {
        foreach (var earcon in Enum.GetValues<DictationEarcon>())
        {
            var expectedSamples = DictationEarconSounds.Notes(earcon).Sum(note => (int)(note.Seconds * DictationEarconSounds.SampleRate));
            var wav = DictationEarconSounds.Wav(earcon);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal(44 + expectedSamples * 2, wav.Length);
        }
    }

    [Fact]
    public void TheStartChimeRisesAndTheStopChimeFalls()
    {
        Assert.Equal([(660.0, 0.06), (880.0, 0.09)], DictationEarconSounds.Notes(DictationEarcon.Start));
        Assert.Equal([(880.0, 0.06), (660.0, 0.09)], DictationEarconSounds.Notes(DictationEarcon.Stop));
    }

    [Fact]
    public void SamplesStartSilentAndStayInRange()
    {
        var samples = DictationEarconSounds.Samples(DictationEarcon.Complete);
        Assert.Equal(0, samples[0]);
        Assert.All(samples, sample => Assert.InRange(sample, (short)(-0.81 * short.MaxValue), (short)(0.81 * short.MaxValue)));
    }
}

public class PlatformGeometryTests
{
    [Fact]
    public void RectanglesContainTheirTopLeftButNotTheirBottomRight()
    {
        var rect = PixelRect.FromEdges(10, 20, 110, 70);
        Assert.Equal(100, rect.Width);
        Assert.True(rect.Contains(new PixelPoint(10, 20)));
        Assert.False(rect.Contains(new PixelPoint(110, 70)));
        Assert.True(rect.Inflated(40).Contains(new PixelPoint(140, 100)));
    }

    [Fact]
    public void KeySidesComeFromTheSideSpecificCodes()
    {
        Assert.Equal(KeySide.Right, VirtualKeys.SideOf(VirtualKeys.RightControl));
        Assert.Equal(KeySide.Left, VirtualKeys.SideOf(VirtualKeys.LeftWindows));
        Assert.Equal(KeySide.None, VirtualKeys.SideOf(VirtualKeys.CapsLock));
        Assert.True(VirtualKeys.IsModifier(VirtualKeys.RightAlt));
        Assert.Equal(0x4F434C54, LiveTypingMarker.Value);
    }

    [Fact]
    public void BrowsersAreKnownByImageName()
    {
        Assert.True(BrowserProcesses.IsBrowser("CHROME.EXE"));
        Assert.True(BrowserProcesses.IsBrowser("msedge.exe"));
        Assert.False(BrowserProcesses.IsBrowser("WindowsTerminal.exe"));
        Assert.False(BrowserProcesses.IsBrowser(null));
    }
}
