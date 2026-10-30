#!/usr/bin/env python3
"""Draws the app icon into Assets.xcassets/AppIcon.appiconset: the white mark (the ring with its
square top-right corner, and the pointer) on a pointer-red tile, hotter at the top-left. From the
Claude Design "OpenClicky Icon Directions", direction 1 "Signal" (2026-10-30): the tile carries a
soft diagonal sheen and a drop shadow, the mark a white glow. Small sizes are drawn on their own,
not scaled down: 32 and 64 px get a slightly bigger, bolder mark and no sheen, 16 px a tile that
fills more of its square and a mark that fills more of the tile, so the ring and pointer still
read. The mark's proportions match OpenClickyMark.swift. Needs Pillow.

Run from macos/OpenClicky: python3 scripts/make-app-icon.py [--preview path.png] [--site dir]
--site also writes the website's icons (app-icon.png, apple-touch-icon.png, favicon-32.png,
favicon-192.png into dir/assets, favicon.ico into dir): a full-bleed tile, no canvas margin.
--windows path writes only the Windows .ico (16-256 px, each tier cropped to its tile):
  python3 scripts/make-app-icon.py --windows ../../windows/src/OpenClicky.Windows/Assets/OpenClicky.ico
Every run also writes the colours people can pick in Settings → General (AppIconColor.swift) as
AppIcon-<name>.imageset, 512 px: the Dock shows the picked one while OpenClicky runs."""
import sys
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw, ImageFilter

SCALE = 4                      # supersampling
CANVAS = 1024 * SCALE
WHITE = (255, 255, 255, 255)


class Colours:
    """A tile's gradient (top-left to bottom-right), the mark's colour and its glow's."""

    def __init__(self, top_left, bottom_right, mark=WHITE, glow=(255, 255, 255, 140)):
        self.top_left, self.bottom_right, self.mark, self.glow = top_left, bottom_right, mark, glow


# The five directions from the design; Signal is the bundle's icon, the rest are picked in Settings.
# Keep the names in step with AppIconColor.swift.
COLOURS = {
    "signal": Colours((0xFF, 0x6A, 0x3F), (0xD4, 0x16, 0x3A)),
    "ultraviolet": Colours((0x8E, 0x6B, 0xFF), (0x34, 0x17, 0xC9)),
    "sunset": Colours((0xFF, 0xBE, 0x3D), (0xFF, 0x1F, 0x7A)),
    "paper": Colours((0xFF, 0xFB, 0xF2), (0xEF, 0xE6, 0xD2), mark=(0xF0, 0x45, 0x2B, 255), glow=(0xF0, 0x45, 0x2B, 115)),
    "aqua": Colours((0x3C, 0xF0, 0xD8), (0x0A, 0x5B, 0xE6)),
}
SIGNAL = COLOURS["signal"]

ARROW = [(0.3125, 0.3125), (0.7475, 0.475), (0.505, 0.52), (0.4775, 0.7525)]
INNER = 0.4125                 # the ring's hole, as a share of the mark's side (OpenClickyMark.innerRadius)


class Size:
    """How one tier is drawn, in 1024 units: the tile, the mark and which effects it carries."""

    def __init__(self, tile, radius, mark, inner, sheen=True, shadow=True, glow=True):
        self.tile, self.radius, self.mark, self.inner = tile, radius, mark, inner
        self.sheen, self.shadow, self.glow = sheen, shadow, glow


# Apple's macOS icon grid: an 824 tile on the 1024 canvas.
LARGE = Size(824, 185, 824 * 0.60, INNER)                                        # 256 and up
MEDIUM = Size(824, 185, 824 * 0.60, INNER, glow=False)                           # 128
SMALL = Size(824, 185, 824 * 0.64, INNER - 0.03, sheen=False, glow=False)        # 32, 64
TINY = Size(904, 200, 904 * 0.78, INNER - 0.08, sheen=False, shadow=False, glow=False)  # 16
# The website's icons fill their square.
FAVICON = Size(1024, 230, 1024 * 0.66, INNER - 0.03, shadow=False, glow=False)
FAVICON_SMALL = Size(1024, 230, 1024 * 0.66, INNER - 0.03, sheen=False, shadow=False, glow=False)


def px(value):
    return round(value * SCALE)


def tile_mask(size, offset=0):
    inset = px((1024 - size.tile) / 2)
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (inset, inset + offset, CANVAS - inset, CANVAS - inset + offset), px(size.radius), fill=255)
    return mask


def diagonal(stops, box):
    """A gradient along the box's top-left to bottom-right diagonal: stops are (t, rgba)."""
    x0, y0, x1, y1 = box
    small = 256
    image = Image.new("RGBA", (small, small))
    for y in range(small):
        for x in range(small):
            t = (x + y) / (2 * (small - 1))
            for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
                if t <= t1 or (t1, c1) == stops[-1]:
                    f = min(max((t - t0) / (t1 - t0), 0), 1)
                    image.putpixel((x, y), tuple(round(a + (b - a) * f) for a, b in zip(c0, c1)))
                    break
    full = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    full.paste(image.resize((x1 - x0, y1 - y0), Image.BILINEAR), (x0, y0))
    return full


def mark_mask(size):
    side = px(size.mark)
    x0 = y0 = (CANVAS - side) // 2
    x1 = y1 = x0 + side
    cx, cy = x0 + side / 2, y0 + side / 2
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    draw = ImageDraw.Draw(mask)
    # Outer edge: the circle without its top-right quadrant, plus that quadrant as a square.
    draw.pieslice((x0, y0, x1, y1), 0, 270, fill=255)
    draw.rectangle((cx, y0, x1, cy), fill=255)
    r = size.inner * side
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=0)
    draw.polygon([(x0 + fx * side, y0 + fy * side) for fx, fy in ARROW], fill=255)
    return mask


def scaled(mask, factor):
    return mask.point(lambda v: round(v * factor))


def paint(base, colour, mask):
    layer = Image.new("RGBA", (CANVAS, CANVAS), colour)
    layer.putalpha(mask)
    return Image.alpha_composite(base, layer)


def draw(size, colours=SIGNAL):
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    tile = tile_mask(size)
    inset = px((1024 - size.tile) / 2)
    box = (inset, inset, CANVAS - inset, CANVAS - inset)

    if size.shadow:
        icon = paint(icon, (0, 0, 0, 255), scaled(tile_mask(size, offset=px(14)).filter(ImageFilter.GaussianBlur(px(14))), 0.28))
    body = diagonal([(0, colours.top_left + (255,)), (1, colours.bottom_right + (255,))], box)
    body.putalpha(ImageChops.multiply(body.getchannel("A"), tile))
    icon = Image.alpha_composite(icon, body)
    if size.sheen:
        sheen = diagonal([(0, (255, 255, 255, 87)), (0.55, (255, 255, 255, 0)), (1, (255, 255, 255, 0))], box)
        sheen.putalpha(ImageChops.multiply(sheen.getchannel("A"), tile))
        icon = Image.alpha_composite(icon, sheen)

    mark = mark_mask(size)
    if size.glow:
        icon = paint(icon, colours.glow[:3] + (255,), scaled(mark.filter(ImageFilter.GaussianBlur(px(18))), colours.glow[3] / 255))
        icon = paint(icon, (0, 0, 0, 255), scaled(ImageChops.offset(mark, 0, px(10)).filter(ImageFilter.GaussianBlur(px(10))), 0.18))
    return paint(icon, colours.mark, mark)


def render(size, pixels):
    return draw(size).resize((pixels, pixels), Image.LANCZOS)


def mac_icon(pixels):
    if pixels <= 16:
        return render(TINY, pixels)
    if pixels <= 64:
        return render(SMALL, pixels)
    if pixels <= 128:
        return render(MEDIUM, pixels)
    return render(LARGE, pixels)


def write_site(site):
    assets = Path(site) / "assets"
    mac_icon(64).save(assets / "app-icon.png")
    favicon = draw(FAVICON)
    for name, pixels in (("apple-touch-icon.png", 180), ("favicon-192.png", 192)):
        favicon.resize((pixels, pixels), Image.LANCZOS).save(assets / name)
    small = draw(FAVICON_SMALL)
    small.resize((32, 32), Image.LANCZOS).save(assets / "favicon-32.png")
    small.resize((48, 48), Image.LANCZOS).save(Path(site) / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)])
    print("wrote", assets, "and", Path(site) / "favicon.ico")


def windows_icon(pixels):
    """Windows icons fill their square: the Mac tier cropped to its tile (no grid margin or shadow)."""
    image = draw({16: TINY}.get(pixels, SMALL if pixels <= 64 else MEDIUM if pixels <= 128 else LARGE))
    size = {16: TINY}.get(pixels, LARGE)
    inset = px((1024 - size.tile) / 2)
    return image.crop((inset, inset, CANVAS - inset, CANVAS - inset)).resize((pixels, pixels), Image.LANCZOS)


def write_windows(path):
    sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    images = [windows_icon(pixels) for pixels in sizes]
    images[-1].save(path, sizes=[(s, s) for s in sizes], append_images=images[:-1])
    print("wrote", path)


def main():
    args = sys.argv[1:]
    if args[:1] == ["--windows"]:
        write_windows(args[1])
        return
    if args[:1] == ["--preview"]:
        render(LARGE, 1024).save(args[1])
        print("preview", args[1])
        return
    out = Path(__file__).resolve().parent.parent / "OpenClicky/Assets.xcassets/AppIcon.appiconset"
    for pixels in (16, 32, 64, 128, 256, 512, 1024):
        mac_icon(pixels).save(out / f"{pixels}-mac.png")
    print("wrote", out)
    for name, colours in COLOURS.items():
        imageset = out.parent / f"AppIcon-{name}.imageset"
        imageset.mkdir(exist_ok=True)
        draw(LARGE, colours).resize((512, 512), Image.LANCZOS).save(imageset / "icon.png")
        (imageset / "Contents.json").write_text(
            '{\n  "images" : [\n    {\n      "filename" : "icon.png",\n      "idiom" : "universal"\n    }\n  ],\n'
            '  "info" : {\n    "author" : "xcode",\n    "version" : 1\n  }\n}\n')
    print("wrote", ", ".join(f"AppIcon-{name}" for name in COLOURS))
    if args[:1] == ["--site"]:
        write_site(args[1])


if __name__ == "__main__":
    main()
