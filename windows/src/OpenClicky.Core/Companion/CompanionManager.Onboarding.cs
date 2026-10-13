//
//  CompanionManager.Onboarding.cs
//  OpenClicky (Windows port of CompanionManager.swift: uses, model downloads, the microphone menu)
//

using OpenClicky.Core.Configuration;
using OpenClicky.Core.Speech;

namespace OpenClicky.Core.Companion;

public sealed partial class CompanionManager
{
    /// <summary>One use (a spoken answer, a dictation with words): counted for the open-at-sign-in offer and the pointer's default.</summary>
    private void NoteUse()
    {
        FirstRunTour.NoteUse();
        HidePointerUntilShakeOnceItIsFamiliar();
    }

    /// <summary>The models that download on their own once the welcome tour is done: Kokoro's voice and Parakeet's hearing.</summary>
    public void StartAutomaticModelDownloads(bool tourIsFinished)
    {
        KokoroVoice.Shared?.StartAutomaticSetup(tourIsFinished);
        ParakeetDictation.Shared?.StartAutomaticSetup(tourIsFinished, DictationSettings);
    }

    private async Task StartAutomaticModelDownloadsSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (!isStarted) return;
        StartAutomaticModelDownloads(FirstRunTour.IsFinished);
    }

    /// <summary>The microphone the dictate step names, for "listening through …".</summary>
    public string MicrophoneName()
    {
        var name = services.Microphones.EffectiveInputName(DictationSettings.PreferredMicrophoneUID);
        return string.IsNullOrEmpty(name) ? "your microphone" : name.ToLowerInvariant();
    }

    /// <summary>The tour's "change microphone": the menu at the pointer.</summary>
    public void PresentMicrophoneMenu(Action chosen) => host.PresentMicrophoneMenu(chosen);

    public bool IsOpenAtLogin => services.LoginItem.IsEnabled;

    public bool SetOpenAtLogin(bool enabled)
    {
        var isOn = services.LoginItem.SetEnabled(enabled);
        AppLog.Write($"open at login: {(isOn ? "switched on" : "refused")}");
        return isOn;
    }
}
