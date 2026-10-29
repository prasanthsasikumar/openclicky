//
//  CompanionManager.NoRightControl.cs
//  OpenClicky (Windows only)
//
//  Many laptops have no right Ctrl, the default dictation key. The tour's dictate step finds out
//  (FirstRunTourController.AdoptDictationKey); a skipped tour never did, and right Alt then did
//  nothing (2026-10-28). So until this PC has seen right Ctrl pressed, right Alt pressed alone
//  becomes the dictation key on that very press, and a notice says so. One right Ctrl press proves
//  the keyboard has one, and right Alt is left alone from then on.
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Shortcuts;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>True once right Ctrl has been pressed on this PC.</summary>
    public const string RightControlSeenKey = "openclicky.rightControlSeen";

    /// <summary>Right Alt takes over only while right Ctrl dictates and has never been pressed here.</summary>
    private void UpdateRightAltAdoption() =>
        services.Shortcuts.AdoptsRightAlt = DictationSettings.DictationKey == DictationHotkey.RightControl && !preferences.GetBool(RightControlSeenKey);

    /// <summary>A press of the dictation key while it is right Ctrl: the keyboard has one.</summary>
    private void NoteDictationKeyPressed()
    {
        if (DictationSettings.DictationKey != DictationHotkey.RightControl || preferences.GetBool(RightControlSeenKey)) return;
        preferences.Set(RightControlSeenKey, true);
        UpdateRightAltAdoption();
    }

    /// <summary>Another key that could dictate was held: the tour's dictate step decides; outside it, only right
    /// Alt on a PC that never pressed right Ctrl (already dictating: the recognizer took it on its press).</summary>
    private void OtherDictationKeyHeld(DictationHotkey key)
    {
        if (FirstRunTour.Step == FirstRunTourStep.Dictate)
        {
            FirstRunTour.AdoptDictationKey(key);
            return;
        }
        if (key != DictationHotkey.RightAlt || DictationSettings.DictationKey != DictationHotkey.RightControl || preferences.GetBool(RightControlSeenKey)) return;
        AppLog.Write("shortcuts: right Alt pressed on a PC that never pressed right Ctrl; it dictates now");
        DictationSettings.DictationKey = DictationHotkey.RightAlt;
        ShowQuietNotice("right alt dictates now. change it in settings → dictation.");
    }
}
