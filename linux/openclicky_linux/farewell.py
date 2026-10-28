"""What OpenClicky says on its way out (the Mac's `Farewell.swift`), in the orb under a short chime:
every line says how to bring it back, since an app without a window is easy to lose.
"""

from __future__ import annotations

import random

FAREWELLS = (
    "bye for now. openclicky in your applications menu, and i'm back.",
    "off to nap in your applications menu. open openclicky to wake me.",
    "fine, i'll go. you'll miss me by lunch. applications → openclicky",
    "exit, pursued by a bear. openclicky in your applications menu brings me back.",
    "it was a pleasure hovering with you. search “openclicky” to call me back.",
    "clocking out. your pointer is all yours. applications → openclicky when you need a hand.",
)
# Long enough to read a line; short enough that quitting still feels like quitting.
SHOWN_FOR_SECONDS = 1.8


def farewell() -> str:
    return random.choice(FAREWELLS)
