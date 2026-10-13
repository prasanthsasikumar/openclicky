// Port of macos/OpenClicky/OpenClicky/Farewell.swift
//
// What OpenClicky shows on its way out, typed into the island under a short chime (no voice).
// Every line says how to bring it back, because a tray app with no taskbar button is easy to lose.
// The Mac lines say "⌘ space, type openclicky" (Spotlight); on Windows that is the Windows key
// (Start search), so the lines say that instead.

namespace OpenClicky.Core.Answers;

public sealed record Farewell(string Text)
{
    public static IReadOnlyList<Farewell> All { get; } = new[]
    {
        new Farewell("bye for now. windows key, type openclicky, and i'm back."),
        new Farewell("off to nap in your start menu. windows key → openclicky wakes me."),
        new Farewell("fine, i'll go. you'll miss me by lunch. windows key → openclicky"),
        new Farewell("exit, pursued by a bear. windows key, type openclicky, and i'm back."),
        new Farewell("it was a pleasure hovering with you. search start for “openclicky” to call me back."),
        new Farewell("clocking out. your pointer is all yours. windows key → openclicky when you need a hand."),
    };

    public static Farewell Random() => All[System.Random.Shared.Next(All.Count)];
}
