"""Builds the WuxAuto app icon from the Wuxen 2 key art.

    python branding/make_icon.py

Covers the "ONLINE" word at the bottom of wuxen2-source.png with "AUTO" (gold serif in the
same slot, between the two ornament lines) and writes:
  branding/WuxAuto-logo.png     512x512, the README picture
  src/WuxAuto/WuxAuto.ico       16..256 px, the exe + window icon (16-48 px are cropped to the circle)
Needs Pillow and the Windows font Constantia Bold (C:/Windows/Fonts/constanb.ttf).
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
SRC = HERE / "wuxen2-source.png"
LOGO = HERE / "WuxAuto-logo.png"
ICO = HERE.parent / "src" / "WuxAuto" / "WuxAuto.ico"
FONT = Path(r"C:\Windows\Fonts\constanb.ttf")

TEXT = "AUTO"
SLOT = (180, 388, 335, 420)   # the ONLINE word's black band (source pixels)
CENTER = (257, 404)           # where the word is centred
TRACKING = 0.42               # letter spacing as a fraction of the font size
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
SMALL_CROP = (56, 50, 456, 450)   # sizes <= 48 px zoom into the ink circle so WUXEN 2 still reads


def cover_online(im: Image.Image) -> Image.Image:
    """Fades the ONLINE word into the black ink band it sits on."""
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle(SLOT, radius=10, fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(3))
    ink = Image.new("RGBA", im.size, (9, 8, 8, 255))
    return Image.composite(ink, im, mask)


def draw_word(im: Image.Image) -> Image.Image:
    """Draws TEXT, gold with a dark edge, at 4x and downsamples for clean edges."""
    s = 4
    big = (im.width * s, im.height * s)
    font = ImageFont.truetype(str(FONT), 33 * s)
    gap = int(TRACKING * 33 * s)
    widths = [font.getlength(c) for c in TEXT]
    total = sum(widths) + gap * (len(TEXT) - 1)
    x = CENTER[0] * s - total / 2
    # cap-height centring: use the bbox of the whole word
    bbox = font.getbbox(TEXT)
    y = CENTER[1] * s - (bbox[1] + bbox[3]) / 2

    fill_mask = Image.new("L", big, 0)
    edge_mask = Image.new("L", big, 0)
    df, de = ImageDraw.Draw(fill_mask), ImageDraw.Draw(edge_mask)
    cx = x
    for c, w in zip(TEXT, widths):
        de.text((cx, y), c, font=font, fill=255, stroke_width=2 * s, stroke_fill=255)
        df.text((cx, y), c, font=font, fill=255)
        cx += w + gap

    # vertical gold gradient: pale at the top, deeper at the bottom (like the WUXEN wordmark)
    grad = Image.new("RGBA", big)
    gd = ImageDraw.Draw(grad)
    top, bottom = bbox[1] + y, bbox[3] + y
    stops = [(250, 236, 190), (232, 196, 110), (196, 148, 52), (240, 212, 140)]
    for row in range(int(top) - 8 * s, int(bottom) + 8 * s):
        t = min(1.0, max(0.0, (row - top) / max(1.0, bottom - top)))
        seg = min(len(stops) - 2, int(t * (len(stops) - 1)))
        f = t * (len(stops) - 1) - seg
        col = tuple(int(stops[seg][i] + (stops[seg + 1][i] - stops[seg][i]) * f) for i in range(3))
        gd.line([(0, row), (big[0], row)], fill=col + (255,))

    layer = Image.new("RGBA", big, (0, 0, 0, 0))
    shadow = Image.new("RGBA", big, (0, 0, 0, 0))
    shadow.paste((0, 0, 0, 200), mask=edge_mask)
    shadow = shadow.filter(ImageFilter.GaussianBlur(3 * s))
    layer.alpha_composite(shadow, (0, 2 * s))
    layer.paste((28, 16, 8, 255), mask=edge_mask)
    layer.paste(grad, mask=fill_mask)
    layer = layer.resize(im.size, Image.LANCZOS)
    out = im.copy()
    out.alpha_composite(layer)
    return out


def main() -> None:
    im = Image.open(SRC).convert("RGBA")
    if im.size != (512, 512):
        im = im.resize((512, 512), Image.LANCZOS)
    logo = draw_word(cover_online(im))
    logo.save(LOGO)
    small = logo.crop(SMALL_CROP)
    frames = [(small if n <= 48 else logo).resize((n, n), Image.LANCZOS) for n in ICO_SIZES]
    frames[-1].save(ICO, format="ICO", sizes=[(n, n) for n in ICO_SIZES], append_images=frames[:-1])
    print(f"wrote {LOGO}\nwrote {ICO} ({ICO.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
