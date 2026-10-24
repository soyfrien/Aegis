#!/usr/bin/env python3
"""Generates the Aegis PWA icons (simple shield on a dark tile) into src/Aegis.Web/wwwroot/icons.

Usage: python3 tools/make_icons.py   (needs Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "src" / "Aegis.Web" / "wwwroot" / "icons"
BG, SHIELD, INNER, MARK = (11, 16, 32), (79, 140, 255), (52, 104, 214), (255, 255, 255)
SS = 4  # supersampling factor for smooth edges


def shield_points(cx, cy, w, h):
    """Polygon approximating a heater shield centred on (cx, cy): flat top, straight sides, rounded (convex) bottom to a tip."""
    top, left, right = cy - h / 2, cx - w / 2, cx + w / 2
    y0, depth, n = top + h * 0.50, h * 0.50, 24
    pts = [(cx, top), (right, top + h * 0.12), (right, y0)]
    for i in range(1, n):  # right side -> tip
        u = 1 - i / n
        pts.append((cx + u * w / 2, y0 + depth * (1 - u * u) ** 0.5))
    pts.append((cx, top + h))
    for i in range(n - 1, 0, -1):  # tip -> left side
        u = 1 - i / n
        pts.append((cx - u * w / 2, y0 + depth * (1 - u * u) ** 0.5))
    pts += [(left, y0), (left, top + h * 0.12)]
    return pts


def render(size, scale):
    s = size * SS
    img = Image.new("RGB", (s, s), BG)
    d = ImageDraw.Draw(img)
    cx = cy = s / 2
    w, h = s * 0.56 * scale, s * 0.66 * scale
    d.polygon(shield_points(cx, cy, w, h), fill=SHIELD)
    d.polygon(shield_points(cx, cy, w * 0.82, h * 0.84), fill=INNER)
    # down-arrow ("download") inside the shield
    aw = w * 0.24
    shaft = aw * 0.55
    top = cy - h * 0.22
    mid = cy + h * 0.02
    d.rectangle([cx - shaft / 2, top, cx + shaft / 2, mid], fill=MARK)
    d.polygon([(cx - aw, mid), (cx + aw, mid), (cx, mid + aw * 0.95)], fill=MARK)
    d.rectangle([cx - aw, cy + h * 0.22, cx + aw, cy + h * 0.22 + shaft * 0.6], fill=MARK)
    return img.resize((size, size), Image.LANCZOS)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    render(180, 1.0).save(OUT / "apple-touch-icon.png", optimize=True)
    render(192, 1.0).save(OUT / "icon-192.png", optimize=True)
    render(512, 1.0).save(OUT / "icon-512.png", optimize=True)
    render(512, 0.8).save(OUT / "icon-maskable-512.png", optimize=True)  # keeps art inside the 80% safe zone
    render(32, 1.0).save(OUT / "favicon-32.png", optimize=True)
    print("wrote icons to", OUT)


if __name__ == "__main__":
    main()
