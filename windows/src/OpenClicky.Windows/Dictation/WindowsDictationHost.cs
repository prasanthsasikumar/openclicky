// The DictationHost (Core/Dictation's seam) built from the Platform layer: the Mac's default
// DictationHost (FocusedFieldReader, FrontAppTextInserter, NSWorkspace, IsSecureEventInputEnabled,
// CGEventSource flags) in Windows terms.
//
//   focused field / target / nearby captions → IFocusedElementReader (UI Automation, bounded)
//   paste / live typing / copy               → ITextInserter (Ctrl+V, KEYEVENTF_UNICODE, the clipboard)
//   paste verification                       → PasteLanding.VerifyAsync over the reader and inserter
//   front app                                → the foreground window's process image name (OpenClicky's own included)
//   secure input                             → the focused element's IsPassword (Windows has no global switch)
//   held modifiers                           → the keyboard hook's key state
//   accessibility trusted                    → always: UI Automation needs no permission

using System.IO;
using OpenClicky.Core.Dictation;
using OpenClicky.Core.Platform;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Dictation;

public static class WindowsDictationHost
{
    /// <summary>
    /// The take controller with everything real: a BuddyDictationManager on the platform's
    /// microphone, the engines DictationProviderFactory builds (Windows speech recognition for
    /// `offline`), the earcons, and this host. TODO(integration): the composition root calls this
    /// and feeds Handle(...) from the CompanionShortcutRecognizer's dictation events.
    /// </summary>
    public static DictationTakeController CreateTakeController(PlatformServices platform, DictationSettings settings, DictationSpaceStore spaceStore,
                                                               TakeStore? takeStore, IDictationOrb orb)
    {
        var providerFactory = new Core.Speech.DictationProviderFactory(platform.OfflineSpeech);
        var capture = new Core.Speech.BuddyDictationManager(
            DictationEngineResolver.MakeProvider(settings.Engine, settings, providerFactory),
            platform.MainThread, platform.CreateAudioCapture, platform.MicrophonePermission)
        {
            PreferredMicrophoneId = settings.PreferredMicrophoneUID,
        };
        return new DictationTakeController(settings, spaceStore, takeStore, orb, capture, Create(platform), providerFactory,
                                           platform.MainThread, platform.Earcons);
    }

    public static DictationHost Create(PlatformServices platform) =>
        Create(platform.FocusedElements, platform.TextInserter, platform.KeyboardMonitor);

    public static DictationHost Create(IFocusedElementReader focusedElements, ITextInserter textInserter, IGlobalKeyboardMonitor keyboard) => new()
    {
        ReadFocusedFieldAsync = cancellationToken => focusedElements.SnapshotAsync(cancellationToken),
        ReadNearbyTermsAsync = cancellationToken => focusedElements.NearbyTermsAsync(cancellationToken: cancellationToken),
        FrontAppProcessName = ForegroundProcessName,
        IsSecureInputOnAsync = async cancellationToken =>
            (await focusedElements.FocusedTextTargetAsync(cancellationToken).ConfigureAwait(false))?.IsSecure ?? false,
        IsAccessibilityTrusted = () => true,
        Paste = textInserter.Insert,
        TypeText = textInserter.TypeAsKeystrokes,
        CopyToClipboard = textInserter.CopyToClipboard,
        Now = () => DateTimeOffset.Now,
        ReadFocusedTextTargetAsync = cancellationToken => focusedElements.FocusedTextTargetAsync(cancellationToken),
        HeldModifiers = () => HeldModifiers(keyboard),
        VerifyPasteAsync = (text, before) => PasteLanding.VerifyAsync(text, before, focusedElements, textInserter),
        MakePolisher = DictationEngineResolver.MakePolisher,
        EngineUnavailableReason = DictationEngineResolver.UnavailableReason,
        OwnProcessId = Environment.ProcessId,
        OwnProcessName = Path.GetFileName(Environment.ProcessPath) ?? LiveTypingExclusions.OwnProcessName,
    };

    /// <summary>The image name of the process that owns the foreground window ("chrome.exe"), OpenClicky's own included.</summary>
    public static string? ForegroundProcessName()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0) return null;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == 0 ? null : ProcessDescriber.ImageName((int)processId);
    }

    /// <summary>The modifier keys physically held now, either side, as live typing's guard reads them.</summary>
    public static LiveTypingModifiers HeldModifiers(IGlobalKeyboardMonitor keyboard)
    {
        var held = LiveTypingModifiers.None;
        if (keyboard.IsKeyDown(VirtualKeys.LeftShift) || keyboard.IsKeyDown(VirtualKeys.RightShift)) held |= LiveTypingModifiers.Shift;
        var rightAltDown = keyboard.IsKeyDown(VirtualKeys.RightAlt);
        // Right Alt as AltGr also reports a left Ctrl that nobody pressed: with right Alt down only
        // the right Ctrl counts, so AltGr (a Right Alt dictation key) reads as Alt alone.
        var controlDown = keyboard.IsKeyDown(VirtualKeys.RightControl) || (!rightAltDown && keyboard.IsKeyDown(VirtualKeys.LeftControl));
        if (controlDown) held |= LiveTypingModifiers.Control;
        if (keyboard.IsKeyDown(VirtualKeys.LeftAlt) || rightAltDown) held |= LiveTypingModifiers.Alt;
        if (keyboard.IsKeyDown(VirtualKeys.LeftWindows) || keyboard.IsKeyDown(VirtualKeys.RightWindows)) held |= LiveTypingModifiers.Windows;
        return held;
    }
}
