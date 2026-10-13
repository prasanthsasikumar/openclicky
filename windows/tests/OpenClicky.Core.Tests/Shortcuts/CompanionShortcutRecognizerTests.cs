// Port of CompanionShortcutRecognizerTests.swift. Mac → Windows: ⌃ → Ctrl, ⌥ → Win, fn (the Mac default
// dictation key) → Right Ctrl (the Windows default). The dictation key is identified by its
// side-specific virtual-key code, as the low-level hook reports it.

using OpenClicky.Core.Shortcuts;
using static OpenClicky.Core.Shortcuts.CompanionShortcutEvent;

namespace OpenClicky.Core.Tests.Shortcuts;

public class CompanionShortcutRecognizerTests
{
    private const PhysicalModifiers LCtrl = PhysicalModifiers.LeftControl;
    private const PhysicalModifiers RCtrl = PhysicalModifiers.RightControl;
    private const PhysicalModifiers LWin = PhysicalModifiers.LeftWindows;
    private const PhysicalModifiers None = PhysicalModifiers.None;

    private static CompanionShortcutEvent[] Flags(CompanionShortcutRecognizer recognizer, PhysicalModifiers modifiers, int virtualKey, double at)
    {
        var isDown = (modifiers & VirtualKeys.ModifierFor(virtualKey)) != 0;
        return recognizer.Handle(new ShortcutKeyEvent(virtualKey, isDown, modifiers, at)).ToArray();
    }

    private static CompanionShortcutEvent[] Key(CompanionShortcutRecognizer recognizer, int virtualKey, bool isDown, PhysicalModifiers modifiers, double at) =>
        recognizer.Handle(new ShortcutKeyEvent(virtualKey, isDown, modifiers, at)).ToArray();

    [Fact]
    public void HoldingControlAndWindowsIsTalk()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Assert.Empty(Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0));
        Assert.Equal(new[] { TalkPressed }, Flags(recognizer, LCtrl | LWin, VirtualKeys.LeftWindows, 0.05));
        Assert.Equal(new[] { TalkReleased }, Flags(recognizer, LCtrl, VirtualKeys.LeftWindows, 2.0));
        Assert.Empty(Flags(recognizer, None, VirtualKeys.LeftControl, 2.05));
    }

    [Fact]
    public void TappingControlTwiceOpensTheTextComposer()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Assert.Empty(Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0));
        Assert.Empty(Flags(recognizer, None, VirtualKeys.LeftControl, 0.1));
        Assert.Empty(Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0.3));
        Assert.Equal(new[] { TextComposerRequested }, Flags(recognizer, None, VirtualKeys.LeftControl, 0.4));
    }

    [Fact]
    public void SlowControlTapsDoNotOpenTheComposer()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Flags(recognizer, None, VirtualKeys.LeftControl, 0.1);
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 1.0);
        Assert.Empty(Flags(recognizer, None, VirtualKeys.LeftControl, 1.1));
    }

    [Fact]
    public void ControlShortcutsLikeControlCAreNotTaps()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Assert.Empty(Key(recognizer, 'C', true, LCtrl, 0.05));
        Key(recognizer, 'C', false, LCtrl, 0.1);
        Flags(recognizer, None, VirtualKeys.LeftControl, 0.15);
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0.3);
        Assert.Empty(Flags(recognizer, None, VirtualKeys.LeftControl, 0.4));
    }

    [Fact]
    public void ATalkPressInsideTheTapWindowIsNotAControlTap()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Flags(recognizer, LCtrl | LWin, VirtualKeys.LeftWindows, 0.05);
        Flags(recognizer, LCtrl, VirtualKeys.LeftWindows, 0.1);
        Flags(recognizer, None, VirtualKeys.LeftControl, 0.15);
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0.3);
        Assert.Empty(Flags(recognizer, None, VirtualKeys.LeftControl, 0.4));
    }

    // dictation (default key: right Ctrl)

    [Fact]
    public void HoldingTheDictationKeyIsADictationTake()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Assert.Equal(new[] { DictationPressed }, Flags(recognizer, RCtrl, VirtualKeys.RightControl, 0));
        Assert.Equal(new[] { DictationReleased(wasTap: false) }, Flags(recognizer, None, VirtualKeys.RightControl, 3.0));
    }

    [Fact]
    public void AQuickPressIsATap()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, RCtrl, VirtualKeys.RightControl, 0);
        Assert.Equal(new[] { DictationReleased(wasTap: true) }, Flags(recognizer, None, VirtualKeys.RightControl, 0.2));
    }

    [Fact]
    public void TwoQuickTapsCancel()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, RCtrl, VirtualKeys.RightControl, 0);
        Flags(recognizer, None, VirtualKeys.RightControl, 0.1);
        Flags(recognizer, RCtrl, VirtualKeys.RightControl, 0.3);
        Assert.Equal(new[] { DictationReleased(wasTap: true), DictationDoubleTapped }, Flags(recognizer, None, VirtualKeys.RightControl, 0.4));
    }

    [Fact]
    public void TheKeyWithAnArrowKeyIsNotATap()
    {
        var recognizer = new CompanionShortcutRecognizer();
        const int leftArrow = 0x25;
        Flags(recognizer, RCtrl, VirtualKeys.RightControl, 0);
        Key(recognizer, leftArrow, true, RCtrl, 0.05);
        Key(recognizer, leftArrow, false, RCtrl, 0.1);
        Assert.Equal(new[] { DictationReleased(wasTap: false, keyWasPressedDuringHold: true) }, Flags(recognizer, None, VirtualKeys.RightControl, 0.2));
    }

    [Fact]
    public void ControlJoiningAHeldKeyIsHeyClicky()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Assert.Equal(new[] { DictationPressed }, Flags(recognizer, RCtrl, VirtualKeys.RightControl, 0));
        Assert.Equal(new[] { DictationEditModifierJoined }, Flags(recognizer, RCtrl | LCtrl, VirtualKeys.LeftControl, 0.1));
        Assert.Empty(Flags(recognizer, RCtrl, VirtualKeys.LeftControl, 2.0));
        Assert.Equal(new[] { DictationReleased(wasTap: false) }, Flags(recognizer, None, VirtualKeys.RightControl, 2.1));
    }

    [Fact]
    public void TheKeyPressedWhileControlIsDownIsHeyClickyFromTheStart()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Assert.Equal(new[] { DictationEditPressed }, Flags(recognizer, LCtrl | RCtrl, VirtualKeys.RightControl, 0.05));
    }

    [Fact]
    public void TappingTheKeyAndControlTwiceTogglesHandsFree()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Flags(recognizer, LCtrl | RCtrl, VirtualKeys.RightControl, 0.02);
        Assert.Equal(new[] { DictationReleased(wasTap: true) }, Flags(recognizer, LCtrl, VirtualKeys.RightControl, 0.1));
        Flags(recognizer, LCtrl | RCtrl, VirtualKeys.RightControl, 0.3);
        Assert.Equal(new[] { DictationReleased(wasTap: true), HandsFreeToggleRequested }, Flags(recognizer, LCtrl, VirtualKeys.RightControl, 0.4));
    }

    [Fact]
    public void TheKeyWithControlAndWindowsIsTalkNotDictation()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Flags(recognizer, LCtrl | LWin, VirtualKeys.LeftWindows, 0.01);
        Assert.Empty(Flags(recognizer, RCtrl | LCtrl | LWin, VirtualKeys.RightControl, 0.02));
        Assert.Equal(new[] { TalkReleased }, Flags(recognizer, RCtrl | LCtrl, VirtualKeys.LeftWindows, 1.0));
    }

    [Fact]
    public void CapsLockAsTheKeyBehavesLikeTheMacFnKey()
    {
        // fn on the Mac has no twin; Caps Lock is its Windows analogue.
        var recognizer = new CompanionShortcutRecognizer(DictationHotkey.CapsLock);
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Flags(recognizer, LCtrl | LWin, VirtualKeys.LeftWindows, 0.01);
        Assert.Empty(Flags(recognizer, PhysicalModifiers.CapsLock | LCtrl | LWin, VirtualKeys.CapsLock, 0.02));
        Assert.Equal(new[] { TalkReleased }, Flags(recognizer, PhysicalModifiers.CapsLock | LCtrl, VirtualKeys.LeftWindows, 1.0));
    }

    [Fact]
    public void CapsLockIsAnOrdinaryKeyWhenItDoesNotDictate()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, LCtrl, VirtualKeys.LeftControl, 0);
        Assert.Empty(Key(recognizer, VirtualKeys.CapsLock, true, LCtrl | PhysicalModifiers.CapsLock, 0.05));
        Flags(recognizer, PhysicalModifiers.CapsLock, VirtualKeys.LeftControl, 0.1);
        Assert.False(recognizer.IsDictationKeyHeld);
    }

    [Fact]
    public void EscapeIsReported()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Assert.Equal(new[] { EscapePressed }, Key(recognizer, VirtualKeys.Escape, true, None, 0));
    }

    [Fact]
    public void TheRightAltKeyCanBeTheDictationKey()
    {
        var recognizer = new CompanionShortcutRecognizer(DictationHotkey.RightAlt);
        // The left Alt key folds into the same modifier but is not the dictation key.
        Assert.Empty(Flags(recognizer, PhysicalModifiers.LeftAlt, VirtualKeys.LeftAlt, 0));
        Assert.Empty(Flags(recognizer, None, VirtualKeys.LeftAlt, 0.1));
        Assert.Equal(new[] { DictationPressed }, Flags(recognizer, PhysicalModifiers.RightAlt, VirtualKeys.RightAlt, 1));
        Assert.Equal(new[] { DictationReleased(wasTap: false) }, Flags(recognizer, None, VirtualKeys.RightAlt, 2));
    }

    [Fact]
    public void ChangingTheKeyResetsTheDictationState()
    {
        var recognizer = new CompanionShortcutRecognizer();
        Flags(recognizer, RCtrl, VirtualKeys.RightControl, 0);
        Assert.True(recognizer.IsDictationKeyHeld);
        recognizer.DictationKey = DictationHotkey.RightShift;
        Assert.False(recognizer.IsDictationKeyHeld);
    }
}

public class DictationHotkeyTests
{
    [Fact]
    public void RawValuesRoundTrip()
    {
        foreach (var hotkey in DictationHotkeys.AllCases)
            Assert.Equal(hotkey, DictationHotkeys.FromRawValue(hotkey.RawValue()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fn")]
    [InlineData("rightOption")]
    [InlineData("rightCommand")]
    public void UnknownStoredValuesFallBackToRightControl(string? rawValue)
    {
        Assert.Equal(DictationHotkey.RightControl, DictationHotkeys.FromRawValue(rawValue));
    }

    [Fact]
    public void LabelsAndKeys()
    {
        Assert.Equal("right Ctrl", DictationHotkey.RightControl.KeycapLabel());
        Assert.Equal("Caps Lock", DictationHotkey.CapsLock.KeycapLabel());
        Assert.Equal("right alt", DictationHotkey.RightAlt.DisplayName());
        Assert.Equal(VirtualKeys.RightShift, DictationHotkey.RightShift.VirtualKey());
        Assert.Null(DictationHotkey.RightControl.IsDown(VirtualKeys.LeftControl, PhysicalModifiers.LeftControl));
        Assert.True(DictationHotkey.RightControl.IsDown(VirtualKeys.RightControl, PhysicalModifiers.RightControl));
        Assert.False(DictationHotkey.RightControl.IsDown(VirtualKeys.RightControl, PhysicalModifiers.LeftControl));
    }
}

// every dictation key × every gesture

/// <summary>A physical key as the low-level hook reports it: its side-specific virtual key and its bit
/// in the modifier state — left and right Ctrl fold into the same modifier but are different keys.</summary>
public enum PhysicalKey { LeftControl, RightControl, LeftWindows, LeftAlt, RightAlt, LeftShift, RightShift, CapsLock }

internal static class PhysicalKeyExtensions
{
    public static int VirtualKey(this PhysicalKey key) => key switch
    {
        PhysicalKey.LeftControl => VirtualKeys.LeftControl,
        PhysicalKey.RightControl => VirtualKeys.RightControl,
        PhysicalKey.LeftWindows => VirtualKeys.LeftWindows,
        PhysicalKey.LeftAlt => VirtualKeys.LeftAlt,
        PhysicalKey.RightAlt => VirtualKeys.RightAlt,
        PhysicalKey.LeftShift => VirtualKeys.LeftShift,
        PhysicalKey.RightShift => VirtualKeys.RightShift,
        _ => VirtualKeys.CapsLock,
    };

    public static PhysicalKey ForDictationKey(DictationHotkey hotkey) => hotkey switch
    {
        DictationHotkey.RightControl => PhysicalKey.RightControl,
        DictationHotkey.RightAlt => PhysicalKey.RightAlt,
        DictationHotkey.RightShift => PhysicalKey.RightShift,
        _ => PhysicalKey.CapsLock,
    };
}

/// <summary>Plays key presses into a recogniser and collects every event it reports.</summary>
internal sealed class SimulatedKeyboard
{
    public CompanionShortcutRecognizer Recognizer { get; }
    private readonly List<PhysicalKey> heldKeys = new();
    public double Time { get; private set; }
    public List<CompanionShortcutEvent> Events { get; } = new();

    public SimulatedKeyboard(DictationHotkey dictationKey) => Recognizer = new CompanionShortcutRecognizer(dictationKey);

    private PhysicalModifiers CurrentModifiers
    {
        get
        {
            var modifiers = PhysicalModifiers.None;
            foreach (var key in heldKeys) modifiers |= VirtualKeys.ModifierFor(key.VirtualKey());
            return modifiers;
        }
    }

    public void Wait(double seconds) => Time += seconds;

    public void Press(PhysicalKey key)
    {
        heldKeys.Add(key);
        Events.AddRange(Recognizer.Handle(new ShortcutKeyEvent(key.VirtualKey(), true, CurrentModifiers, Time)));
    }

    public void Release(PhysicalKey key)
    {
        heldKeys.RemoveAll(held => held == key);
        Events.AddRange(Recognizer.Handle(new ShortcutKeyEvent(key.VirtualKey(), false, CurrentModifiers, Time)));
    }

    /// <summary>A letter (C) pressed and released with whatever modifiers are held.</summary>
    public void TypeLetter(int virtualKey = 'C')
    {
        Events.AddRange(Recognizer.Handle(new ShortcutKeyEvent(virtualKey, true, CurrentModifiers, Time)));
        Wait(0.03);
        Events.AddRange(Recognizer.Handle(new ShortcutKeyEvent(virtualKey, false, CurrentModifiers, Time)));
    }
}

/// <summary>Each gesture must produce exactly the events of its own shortcut, whichever key dictates: a
/// right Ctrl hold is a dictation and not a Hey Clicky edit, right Alt then Ctrl is an edit and not a
/// Ctrl+Win question as well, a shortcut typed while the key is held says so when the key comes up.</summary>
public class DictationKeyGestureMatrixTests
{
    public static IEnumerable<object[]> AllKeys => DictationHotkeys.AllCases.Select(hotkey => new object[] { hotkey });

    [Theory, MemberData(nameof(AllKeys))]
    public void HoldingTheKeyIsOneDictationTake(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Wait(2);
        keyboard.Release(key);
        Assert.Equal(new[] { DictationPressed, DictationReleased(wasTap: false) }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void TappingTheKeyIsATapAndNothingElse(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Wait(0.1);
        keyboard.Release(key);
        Assert.Equal(new[] { DictationPressed, DictationReleased(wasTap: true) }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void DoubleTappingTheKeyCancelsAndOpensNothingElse(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Wait(0.1);
        keyboard.Release(key);
        keyboard.Wait(0.15);
        keyboard.Press(key);
        keyboard.Wait(0.1);
        keyboard.Release(key);
        Assert.Equal(new[]
        {
            DictationPressed, DictationReleased(wasTap: true),
            DictationPressed, DictationReleased(wasTap: true), DictationDoubleTapped,
        }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void ControlJoiningTheHeldKeyIsAnEditAndNotTalk(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Wait(0.2);
        keyboard.Press(PhysicalKey.LeftControl);
        Assert.False(keyboard.Recognizer.IsTalkHeld);
        keyboard.Wait(2);
        keyboard.Release(PhysicalKey.LeftControl);
        keyboard.Wait(0.05);
        keyboard.Release(key);
        Assert.Equal(new[] { DictationPressed, DictationEditModifierJoined, DictationReleased(wasTap: false) }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void TheKeyPressedWithControlAlreadyDownIsAnEditFromTheStart(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(0.05);
        keyboard.Press(key);
        keyboard.Wait(2);
        keyboard.Release(key);
        keyboard.Wait(0.05);
        keyboard.Release(PhysicalKey.LeftControl);
        Assert.Equal(new[] { DictationEditPressed, DictationReleased(wasTap: false) }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void TappingTheKeyTwiceWithControlHeldTogglesHandsFreeOnly(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(0.02);
        keyboard.Press(key);
        keyboard.Wait(0.08);
        keyboard.Release(key);
        keyboard.Wait(0.15);
        keyboard.Press(key);
        keyboard.Wait(0.08);
        keyboard.Release(key);
        keyboard.Wait(0.05);
        keyboard.Release(PhysicalKey.LeftControl);
        Assert.Equal(new[]
        {
            DictationEditPressed, DictationReleased(wasTap: true),
            DictationEditPressed, DictationReleased(wasTap: true), HandsFreeToggleRequested,
        }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void HoldingControlAndWindowsIsTalkWhateverTheDictationKey(DictationHotkey hotkey)
    {
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(0.05);
        keyboard.Press(PhysicalKey.LeftWindows);
        keyboard.Wait(2);
        keyboard.Release(PhysicalKey.LeftWindows);
        keyboard.Wait(0.05);
        keyboard.Release(PhysicalKey.LeftControl);
        Assert.Equal(new[] { TalkPressed, TalkReleased }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void TappingControlTwiceOpensTheComposerOnly(DictationHotkey hotkey)
    {
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(0.1);
        keyboard.Release(PhysicalKey.LeftControl);
        keyboard.Wait(0.15);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(0.1);
        keyboard.Release(PhysicalKey.LeftControl);
        Assert.Equal(new[] { TextComposerRequested }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void AShortcutTypedWithTheKeyHeldIsReportedOnRelease(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Wait(0.05);
        keyboard.TypeLetter();
        keyboard.Wait(0.05);
        keyboard.Release(key);
        Assert.Equal(new[] { DictationPressed, DictationReleased(wasTap: false, keyWasPressedDuringHold: true) }, keyboard.Events);
    }

    /// <summary>A letter typed just before the key went down whose key-up lands after it is not a shortcut.</summary>
    [Theory, MemberData(nameof(AllKeys))]
    public void ALateKeyUpFromTypingIsNotAShortcut(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Events.AddRange(keyboard.Recognizer.Handle(new ShortcutKeyEvent('A', false, PhysicalModifiers.None, 0.02)));
        keyboard.Wait(2);
        keyboard.Release(key);
        Assert.Equal(new[] { DictationPressed, DictationReleased(wasTap: false) }, keyboard.Events);
    }

    /// <summary>One gesture, one lane: Ctrl+Win completed while the key is held is not also a question.</summary>
    [Theory, MemberData(nameof(AllKeys))]
    public void TalkNeverStartsWhileTheKeyIsHeld(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Wait(0.2);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(0.05);
        keyboard.Press(PhysicalKey.LeftWindows);
        keyboard.Wait(1);
        keyboard.Release(key);
        keyboard.Wait(0.5);
        keyboard.Release(PhysicalKey.LeftWindows);
        keyboard.Release(PhysicalKey.LeftControl);
        Assert.Equal(new[] { DictationPressed, DictationEditModifierJoined, DictationReleased(wasTap: false) }, keyboard.Events);
    }

    [Theory, MemberData(nameof(AllKeys))]
    public void TheKeyPressedDuringTalkIsIgnored(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Press(PhysicalKey.LeftWindows);
        keyboard.Wait(0.5);
        keyboard.Press(key);
        keyboard.Wait(0.5);
        keyboard.Release(key);
        keyboard.Wait(0.5);
        keyboard.Release(PhysicalKey.LeftWindows);
        keyboard.Release(PhysicalKey.LeftControl);
        Assert.Equal(new[] { TalkPressed, TalkReleased }, keyboard.Events);
    }

    /// <summary>The Mac's "with right ⌥ dictating, the left ⌥ still talks": with right Ctrl dictating,
    /// the left Ctrl keeps its job — Win then Ctrl is still talk.</summary>
    [Fact]
    public void WithRightControlDictatingTheLeftControlStillTalks()
    {
        var keyboard = new SimulatedKeyboard(DictationHotkey.RightControl);
        keyboard.Press(PhysicalKey.LeftWindows);
        keyboard.Wait(0.05);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(1);
        keyboard.Release(PhysicalKey.LeftControl);
        keyboard.Release(PhysicalKey.LeftWindows);
        Assert.Equal(new[] { TalkPressed, TalkReleased }, keyboard.Events);
    }

    /// <summary>Both Ctrls down, then the right one (the dictation key) comes up: Ctrl is still held by the
    /// left one, but the take ends.</summary>
    [Fact]
    public void WithRightControlDictatingReleasingItWhileTheLeftIsHeldEndsTheTake()
    {
        var keyboard = new SimulatedKeyboard(DictationHotkey.RightControl);
        keyboard.Press(PhysicalKey.RightControl);
        keyboard.Wait(0.5);
        keyboard.Press(PhysicalKey.LeftControl);
        keyboard.Wait(1);
        keyboard.Release(PhysicalKey.RightControl);
        Assert.Equal(new[] { DictationPressed, DictationEditModifierJoined, DictationReleased(wasTap: false) }, keyboard.Events);
        Assert.False(keyboard.Recognizer.IsDictationKeyHeld);
    }

    /// <summary>Windows repeats key-downs of a held key; a repeated dictation key changes nothing.</summary>
    [Theory, MemberData(nameof(AllKeys))]
    public void AutoRepeatedKeyDownsOfTheHeldKeyAreIgnored(DictationHotkey hotkey)
    {
        var key = PhysicalKeyExtensions.ForDictationKey(hotkey);
        var keyboard = new SimulatedKeyboard(hotkey);
        keyboard.Press(key);
        keyboard.Wait(0.5);
        keyboard.Events.AddRange(keyboard.Recognizer.Handle(new ShortcutKeyEvent(key.VirtualKey(), true, VirtualKeys.ModifierFor(key.VirtualKey()), keyboard.Time)));
        keyboard.Wait(0.5);
        keyboard.Release(key);
        Assert.Equal(new[] { DictationPressed, DictationReleased(wasTap: false) }, keyboard.Events);
    }
}
