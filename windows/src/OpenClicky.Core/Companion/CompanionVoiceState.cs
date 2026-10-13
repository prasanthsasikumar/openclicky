//
//  CompanionVoiceState.cs
//  OpenClicky (Windows port of CompanionManager.swift's CompanionVoiceState)
//

namespace OpenClicky.Core.Companion;

/// <summary>Where a spoken question is: nothing going on, the microphone open, waiting for the answer, the answer
/// being spoken. The overlay swaps the pointer for a waveform (listening) or a spinner (processing).</summary>
public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding,
}
