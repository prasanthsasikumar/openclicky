#!/usr/bin/env python3
"""Draws the app icon into Assets.xcassets/AppIcon.appiconset: a dark slate tile with the coral
mark (the ring with its square top-right corner, and the pointer). The mark's proportions match
OpenClickyMark.swift. Needs Pillow. Run from macos/OpenClicky: python3 scripts/make-app-icon.py"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

SCALE = 4                      # supersampling
CANVAS = 1024 * SCALE
BODY = 824 * SCALE             # Apple's macOS icon grid: 824 pt tile on a 1024 canvas
INSET = (CANVAS - BODY) // 2
RADIUS = 185 * SCALE
TOP, BOTTOM = (0x35, 0x3E, 0x46), (0x14, 0x1A, 0x24)
CORAL = (0xE8, 0x94, 0x6D)
MARK = int(BODY * 0.62)

INNER = 0.4125
ARROW = [(0.3125, 0.3125), (0.7475, 0.475), (0.505, 0.52), (0.4775, 0.7525)]


def tile_mask(offset=0):
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (INSET, INSET + offset, INSET + BODY, INSET + BODY + offset), RADIUS, fill=255)
    return mask


def mark_mask():
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    draw = ImageDraw.Draw(mask)
    x0 = y0 = (CANVAS - MARK) // 2
    cx, cy = x0 + MARK / 2, y0 + MARK / 2
    # Outer edge: the circle without its top-right quadrant, plus that quadrant as a square.
    draw.pieslice((x0, y0, x0 + MARK, y0 + MARK), 0, 270, fill=255)
    draw.rectangle((cx, y0, x0 + MARK, cy), fill=255)
    r = INNER * MARK
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=0)
    draw.polygon([(x0 + fx * MARK, y0 + fy * MARK) for fx, fy in ARROW], fill=255)
    return mask


def main():
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))

    # A soft drop shadow under the tile.
    shadow = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    shadow.putalpha(tile_mask(offset=10 * SCALE).point(lambda v: v * 0.45))
    icon = Image.alpha_composite(icon, shadow.filter(ImageFilter.GaussianBlur(14 * SCALE)))

    # The tile: a vertical slate gradient with a faint lighter rim at the top.
    gradient = Image.new("RGBA", (1, CANVAS))
    for y in range(CANVAS):
        t = min(max((y - INSET) / BODY, 0), 1)
        gradient.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,))
    gradient = gradient.resize((CANVAS, CANVAS))
    icon.paste(gradient, (0, 0), tile_mask())
    rim = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(rim).rounded_rectangle(
        (INSET, INSET, INSET + BODY, INSET + BODY), RADIUS, outline=255, width=3 * SCALE)
    icon.paste((255, 255, 255, 255), (0, 0), rim.point(lambda v: v * 0.10))

    # The mark, with a hairline of shade under it so it sits on the tile.
    mask = mark_mask()
    icon.paste((0, 0, 0, 255), (0, 3 * SCALE), mask.filter(ImageFilter.GaussianBlur(3 * SCALE)).point(lambda v: v * 0.5))
    icon.paste(CORAL + (255,), (0, 0), mask)

    out = Path(__file__).resolve().parent.parent / "OpenClicky/Assets.xcassets/AppIcon.appiconset"
    full = icon.resize((1024, 1024), Image.LANCZOS)
    for size in (16, 32, 64, 128, 256, 512, 1024):
        full.resize((size, size), Image.LANCZOS).save(out / f"{size}-mac.png")
    print("wrote", out)


if __name__ == "__main__":
    main()
