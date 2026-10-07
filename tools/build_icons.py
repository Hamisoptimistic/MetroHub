#!/usr/bin/env python3
"""Regenerate the MetroHub application icon assets.

The icon is composed of two ingredients:

  * ``Assets/metrohub_mark.png`` - the "m." brand mark, white on transparent
    (checked in, tight bounding box, never rescaled in place).
  * a framed plate that is drawn parametrically.

Everything is redrawn *per target size* instead of downscaling one bitmap, so
each size in the .ico gets strokes that land on whole pixels.  That is what
makes the icon read at 16/24 px in the taskbar, Start menu and system tray.

Usage::

    python tools/build_icons.py             # rewrite app.png / metrohub_icon.png / app.ico
    python tools/build_icons.py --preview   # ASCII dump of every size, no files touched
"""

from __future__ import annotations

import argparse
import io
import struct
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw
import numpy as np

REPO_ROOT = Path(__file__).resolve().parents[1]
ASSETS = REPO_ROOT / "src" / "MetroHub" / "Assets"
MARK_PATH = ASSETS / "metrohub_mark.png"
APP_PNG = ASSETS / "app.png"
MIRROR_PNG = ASSETS / "metrohub_icon.png"
APP_ICO = ASSETS / "app.ico"

# Windows asks for these logical sizes (100 %-200 % DPI).
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
MASTER_SIZE = 4096


STOPS_DIAGONAL = [
    (0.0,  (245, 158, 30)),   # Bright Gold Amber (#F59E1E)
    (0.30, (235, 68, 88)),    # Vivid Coral Rose (#EB445A)
    (0.65, (120, 35, 145)),   # Rich Velvet Violet (#7C2D92)
    (1.0,  (65, 85, 150)),    # Midnight Frost Slate (#415596)
]


def create_soft_gradient_diagonal(w: int, h: int) -> Image.Image:
    """Create diagonal gradient from top-left Amber/Coral to bottom-right Violet/Slate."""
    gw = min(w, 2048)
    gh = min(h, 2048)
    y, x = np.mgrid[0:gh, 0:gw]
    t = (x + y) / (gw + gh)

    r = np.zeros((gh, gw), dtype=np.float32)
    g = np.zeros((gh, gw), dtype=np.float32)
    b = np.zeros((gh, gw), dtype=np.float32)

    for i in range(len(STOPS_DIAGONAL) - 1):
        pos0, c0 = STOPS_DIAGONAL[i]
        pos1, c1 = STOPS_DIAGONAL[i + 1]
        mask = (t >= pos0) & (t <= pos1)
        local_t = (t[mask] - pos0) / (pos1 - pos0)
        r[mask] = (1.0 - local_t) * c0[0] + local_t * c1[0]
        g[mask] = (1.0 - local_t) * c0[1] + local_t * c1[1]
        b[mask] = (1.0 - local_t) * c0[2] + local_t * c1[2]

    rgb = np.stack([r, g, b], axis=2).clip(0, 255).astype(np.uint8)
    im = Image.fromarray(rgb, mode="RGB")
    if w > gw or h > gh:
        im = im.resize((w, h), Image.Resampling.BICUBIC)
    return im


def spec_for(size: int) -> dict:
    """Return the per-size layout for *size* pixels with enlarged box and breathing room."""
    s = float(size)
    if size <= 16:
        thickness = 1
        inset = 0
    elif size <= 20:
        thickness = 1
        inset = 0
    elif size <= 24:
        thickness = 2
        inset = 0       # Full 24px bleed: fills taskbar height
    elif size <= 32:
        thickness = 2
        inset = 0       # Full 32px bleed
    elif size <= 48:
        thickness = 3
        inset = 1       # 46px box
    else:
        thickness = max(2, round(0.055 * s))
        inset = max(1, round(0.015 * s))  # 97% width for 4K master

    box_sz = size - 2 * inset
    opening = box_sz - 2 * thickness
    glyph_frac = 0.77   # Generous padding so 'm' never touches the border

    return {
        "inset": int(inset),
        "thickness": int(thickness),
        "box_sz": int(box_sz),
        "opening": int(opening),
        "glyph_frac": glyph_frac,
    }


def load_mark() -> dict[str, Image.Image]:
    """Load the brand mark (clean 'm' mark)."""
    mark = Image.open(MARK_PATH).convert("RGBA")
    alpha = mark.getchannel("A")
    bbox = alpha.point(lambda v: 255 if v > 24 else 0).getbbox()
    if bbox is None:
        raise SystemExit(f"{MARK_PATH} is empty")
    alpha = alpha.crop(bbox)
    return {
        "full": alpha,
    }


def render_icon(size: int, marks: dict[str, Image.Image], spec: dict | None = None) -> Image.Image:
    """Draw the icon as an RGBA image at *size* px with chromatic frame and white 'm'."""
    spec = spec or spec_for(size)
    if size <= 64:
        ss = 8
    elif size <= 256:
        ss = 4
    else:
        ss = 1
    plate_sz = size * ss

    # 1. Outer square frame mask
    frame_mask = Image.new("L", (plate_sz, plate_sz), 0)
    draw = ImageDraw.Draw(frame_mask)

    t_ss = spec["thickness"] * ss
    i_ss = spec["inset"] * ss
    box_sz_ss = spec["box_sz"] * ss

    x0 = i_ss
    y0 = i_ss
    x1 = x0 + box_sz_ss - 1
    y1 = y0 + box_sz_ss - 1

    draw.rectangle((x0, y0, x1, y0 + t_ss - 1), fill=255)          # top
    draw.rectangle((x0, y1 - t_ss + 1, x1, y1), fill=255)          # bottom
    draw.rectangle((x0, y0, x0 + t_ss - 1, y1), fill=255)          # left
    draw.rectangle((x1 - t_ss + 1, y0, x1, y1), fill=255)          # right

    # 2. Diagonal soft gradient frame layer
    grad = create_soft_gradient_diagonal(plate_sz, plate_sz)
    frame_rgba = Image.new("RGBA", (plate_sz, plate_sz), (0, 0, 0, 0))
    frame_rgba.paste(grad, (0, 0), frame_mask)

    # 3. Pure white glyph layer
    opening_ss = spec["opening"] * ss
    part = marks["full"]
    target_w = round(opening_ss * spec["glyph_frac"])
    scale = target_w / part.width
    target_h = round(part.height * scale)

    glyph = part.resize((target_w, target_h), Image.Resampling.LANCZOS, reducing_gap=2.0)
    if size <= 48:
        glyph = glyph.point(lambda v: int(255 * (v / 255.0) ** 0.80))

    left = x0 + t_ss + (opening_ss - target_w) // 2
    top = y0 + t_ss + (opening_ss - target_h) // 2

    glyph_full = Image.new("RGBA", (plate_sz, plate_sz), (0, 0, 0, 0))
    white_g = Image.new("RGBA", (target_w, target_h), (255, 255, 255, 255))
    glyph_full.paste(white_g, (left, top), glyph)

    # 4. Composite frame + glyph and box filter down
    comp = Image.alpha_composite(frame_rgba, glyph_full)
    return comp.resize((size, size), Image.Resampling.BOX)


def build_frames(marks: dict[str, Image.Image], sizes=ICO_SIZES) -> list[Image.Image]:
    return [render_icon(size, marks) for size in sizes]


# --------------------------------------------------------------------------- #
# ICO container
# --------------------------------------------------------------------------- #
def _dib(frame: Image.Image) -> bytes:
    """Encode *frame* as a classic 32 bpp BGRA DIB (maximum shell compatibility)."""
    w, h = frame.size
    # Swap to BGRA and flip: DIB scanlines run bottom-up.
    b, g, r, a = frame.split()
    bgra = Image.merge("RGBA", (b, g, r, a)).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
    pixels = bgra.tobytes()

    stride = ((w + 31) // 32) * 4
    mask = bytearray(stride * h)
    for y in range(h):
        for x in range(w):
            if a.getpixel((x, y)) < 128:
                mask[y * stride + x // 8] |= 0x80 >> (x % 8)

    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        w,           # biWidth
        h * 2,       # biHeight (XOR + AND)
        1,           # biPlanes
        32,          # biBitCount
        0,           # biCompression = BI_RGB
        len(pixels) + len(mask),  # biSizeImage
        0, 0, 0, 0,  # resolution / palette
    )
    return header + pixels + bytes(mask)


def _png(frame: Image.Image) -> bytes:
    buf = io.BytesIO()
    frame.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: Path, frames: list[Image.Image]) -> None:
    payloads = []
    for frame in frames:
        # Modern Windows (Vista, 7, 8, 10, 11) natively supports PNG frames in ICO,
        # ensuring 100% crisp 32-bit alpha transparency with zero GDI DIB mask corruption.
        payloads.append(_png(frame))

    offset = 6 + 16 * len(frames)
    entries = bytearray()
    blob = bytearray()
    for frame, payload in zip(frames, payloads):
        dim = 0 if frame.width >= 256 else frame.width
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(payload), offset)
        blob += payload
        offset += len(payload)

    path.write_bytes(struct.pack("<HHH", 0, 1, len(frames)) + bytes(entries) + bytes(blob))


# --------------------------------------------------------------------------- #
# Preview
# --------------------------------------------------------------------------- #
def preview(marks: dict[str, Image.Image], sizes=(16, 20, 24, 32, 48, 64, 256)) -> None:
    ramp = " .:-=+*#%@"
    for size in sizes:
        spec = spec_for(size)
        frame = render_icon(size, marks, spec)
        alpha = frame.getchannel("A")
        desc = f"box {spec['thickness']}px @ {spec['inset']}px, opening={spec['opening']}px"
        print(f"=== {size}x{size}: {desc}")
        for y in range(size):
            row = ""
            for x in range(size):
                row += ramp[min(9, alpha.getpixel((x, y)) * 10 // 256)] * 1
            print("   " + row)
        print()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--preview", action="store_true", help="print ASCII previews and exit")
    args = parser.parse_args()

    marks = load_mark()
    print(f"mark: {marks['full'].width}x{marks['full'].height}")

    if args.preview:
        preview(marks)
        return

    master = render_icon(MASTER_SIZE, marks)
    master.save(APP_PNG, optimize=True)
    master.save(MIRROR_PNG, optimize=True)

    frames = build_frames(marks)
    write_ico(APP_ICO, frames)

    print(f"wrote {APP_PNG.name} ({master.width}x{master.height})")
    print(f"wrote {MIRROR_PNG.name} (copy of {APP_PNG.name})")
    print(f"wrote {APP_ICO.name}: " + ", ".join(f"{f.width}x{f.height}" for f in frames))


if __name__ == "__main__":
    main()
