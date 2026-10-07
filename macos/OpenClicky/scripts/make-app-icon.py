#!/usr/bin/env python3
"""Draws the app icon into Assets.xcassets/AppIcon.appiconset: a dark slate tile with the coral
mark (the ring with its square top-right corner, and the pointer). The mark is raised off the
tile — a lit top-left bevel, a shaded bottom-right one, a soft cast shadow and a sheen across its
upper half — and the tile carries a faint glass reflection, so the icon reads as an object, not a
flat sticker. The mark's proportions match OpenClickyMark.swift. Needs Pillow.
Run from macos/OpenClicky: python3 scripts/make-app-icon.py [--preview path.png]"""
import sys
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw, ImageFilter

SCALE = 4                      # supersampling
CANVAS = 1024 * SCALE
BODY = 824 * SCALE             # Apple's macOS icon grid: 824 pt tile on a 1024 canvas
INSET = (CANVAS - BODY) // 2
RADIUS = 185 * SCALE
TILE_TOP, TILE_BOTTOM = (0x3A, 0x44, 0x4D), (0x12, 0x17, 0x20)
CORAL_LIGHT, CORAL_DARK = (0xF6, 0xAE, 0x88), (0xD4, 0x70, 0x48)
MARK = int(BODY * 0.62)

INNER = 0.4125
ARROW = [(0.3125, 0.3125), (0.7475, 0.475), (0.505, 0.52), (0.4775, 0.7525)]


def vertical_gradient(top, bottom, start, span, size=CANVAS):
    column = Image.new("RGBA", (1, size))
    for y in range(size):
        t = min(max((y - start) / span, 0), 1)
        column.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return column.resize((size, size))


def diagonal_gradient(light, dark, box):
    """Light at the box's top-left corner, dark at its bottom-right."""
    x0, y0, x1, y1 = box
    small = 256
    image = Image.new("RGBA", (small, small))
    for y in range(small):
        for x in range(small):
            t = (x + y) / (2 * (small - 1))
            image.putpixel((x, y), tuple(round(a + (b - a) * t) for a, b in zip(light, dark)) + (255,))
    full = Image.new("RGBA", (CANVAS, CANVAS))
    full.paste(image.resize((x1 - x0, y1 - y0), Image.BILINEAR), (x0, y0))
    return full


def tile_mask(offset=0):
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (INSET, INSET + offset, INSET + BODY, INSET + BODY + offset), RADIUS, fill=255)
    return mask


def mark_box():
    x0 = y0 = (CANVAS - MARK) // 2
    return x0, y0, x0 + MARK, y0 + MARK


def mark_mask():
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    draw = ImageDraw.Draw(mask)
    x0, y0, x1, y1 = mark_box()
    cx, cy = x0 + MARK / 2, y0 + MARK / 2
    # Outer edge: the circle without its top-right quadrant, plus that quadrant as a square.
    draw.pieslice((x0, y0, x1, y1), 0, 270, fill=255)
    draw.rectangle((cx, y0, x1, cy), fill=255)
    r = INNER * MARK
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=0)
    draw.polygon([(x0 + fx * MARK, y0 + fy * MARK) for fx, fy in ARROW], fill=255)
    return mask


def shifted(mask, dx, dy):
    return ImageChops.offset(mask, dx, dy)


def scaled(mask, factor):
    return mask.point(lambda v: round(v * factor))


def paint(base, colour, mask):
    layer = Image.new("RGBA", (CANVAS, CANVAS), colour)
    layer.putalpha(mask)
    return Image.alpha_composite(base, layer)


def main():
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    tile = tile_mask()

    # The tile's drop shadow, then the tile: slate, lit from above.
    icon = paint(icon, (0, 0, 0, 255), scaled(tile_mask(offset=12 * SCALE).filter(ImageFilter.GaussianBlur(16 * SCALE)), 0.5))
    body = vertical_gradient(TILE_TOP, TILE_BOTTOM, INSET, BODY)
    body.putalpha(tile)
    icon = Image.alpha_composite(icon, body)

    # Tile edges: a bright lip along the top, a darker one along the bottom (a shallow bevel).
    top_lip = ImageChops.subtract(tile, shifted(tile, 0, 5 * SCALE)).filter(ImageFilter.GaussianBlur(2 * SCALE))
    icon = paint(icon, (255, 255, 255, 255), scaled(top_lip, 0.22))
    bottom_lip = ImageChops.subtract(tile, shifted(tile, 0, -6 * SCALE)).filter(ImageFilter.GaussianBlur(2 * SCALE))
    icon = paint(icon, (0, 0, 0, 255), scaled(bottom_lip, 0.45))

    # Glass reflection: a broad, soft sheen across the tile's upper part, cut off by a gentle curve.
    sheen = Image.new("L", (CANVAS, CANVAS), 0)
    sheen_draw = ImageDraw.Draw(sheen)
    for step in range(64):
        t = step / 63
        sheen_draw.rectangle((0, INSET + int(t * BODY * 0.55), CANVAS, INSET + int((t + 1 / 63) * BODY * 0.55)), fill=round(62 * (1 - t) ** 1.3))
    curve = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(curve).ellipse((INSET - BODY * 0.6, INSET - BODY * 1.05, INSET + BODY * 1.6, INSET + BODY * 0.52), fill=255)
    sheen = ImageChops.multiply(ImageChops.multiply(sheen, curve.filter(ImageFilter.GaussianBlur(2 * SCALE))), tile)
    icon = paint(icon, (255, 255, 255, 255), sheen)

    mark = mark_mask()

    # The mark sits proud of the tile: a soft cast shadow down and to the right, and a tight contact shadow.
    icon = paint(icon, (0, 0, 0, 255), scaled(shifted(mark, 6 * SCALE, 14 * SCALE).filter(ImageFilter.GaussianBlur(14 * SCALE)), 0.55))
    icon = paint(icon, (0, 0, 0, 255), scaled(shifted(mark, 0, 3 * SCALE).filter(ImageFilter.GaussianBlur(2 * SCALE)), 0.6))

    # The coral body, lit from the top-left.
    coral = diagonal_gradient(CORAL_LIGHT, CORAL_DARK, mark_box())
    coral.putalpha(mark)
    icon = Image.alpha_composite(icon, coral)

    # Bevels: the edges facing the light catch it, the edges facing away fall into shade.
    bevel = 7 * SCALE
    lit_edge = ImageChops.subtract(mark, shifted(mark, bevel, bevel)).filter(ImageFilter.GaussianBlur(3 * SCALE))
    icon = paint(icon, (255, 236, 222, 255), ImageChops.multiply(scaled(lit_edge, 0.7), mark))
    shade_edge = ImageChops.subtract(mark, shifted(mark, -bevel, -bevel)).filter(ImageFilter.GaussianBlur(3 * SCALE))
    icon = paint(icon, (110, 40, 18, 255), ImageChops.multiply(scaled(shade_edge, 0.6), mark))

    # Specular reflection on the mark: a glossy band over its upper half with a crisp, curved lower
    # edge (the reflected horizon), brightest at the top.
    x0, y0, x1, y1 = mark_box()
    gloss = Image.new("L", (CANVAS, CANVAS), 0)
    gloss_draw = ImageDraw.Draw(gloss)
    for step in range(48):
        t = step / 47
        gloss_draw.rectangle((x0, y0 + int(t * MARK * 0.55), x1, y0 + int((t + 1 / 47) * MARK * 0.55)), fill=round(120 * (1 - t) ** 1.4))
    horizon = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(horizon).ellipse((x0 - MARK * 0.5, y0 - MARK * 0.9, x1 + MARK * 0.3, y0 + MARK * 0.5), fill=255)
    gloss = ImageChops.multiply(gloss, horizon.filter(ImageFilter.GaussianBlur(1.5 * SCALE)))
    icon = paint(icon, (255, 255, 255, 255), ImageChops.multiply(gloss, mark))

    # A small specular glint where the light hits the ring's upper-left shoulder.
    glint = Image.new("L", (CANVAS, CANVAS), 0)
    gx, gy = x0 + MARK * 0.17, y0 + MARK * 0.17
    ImageDraw.Draw(glint).ellipse((gx - MARK * 0.07, gy - MARK * 0.03, gx + MARK * 0.07, gy + MARK * 0.03), fill=255)
    glint = glint.rotate(45, center=(gx, gy)).filter(ImageFilter.GaussianBlur(6 * SCALE))
    icon = paint(icon, (255, 255, 255, 255), ImageChops.multiply(scaled(glint, 0.75), mark))

    full = icon.resize((1024, 1024), Image.LANCZOS)
    if len(sys.argv) == 3 and sys.argv[1] == "--preview":
        full.save(sys.argv[2])
        print("preview", sys.argv[2])
        return
    out = Path(__file__).resolve().parent.parent / "OpenClicky/Assets.xcassets/AppIcon.appiconset"
    for size in (16, 32, 64, 128, 256, 512, 1024):
        full.resize((size, size), Image.LANCZOS).save(out / f"{size}-mac.png")
    print("wrote", out)


if __name__ == "__main__":
    main()
