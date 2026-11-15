#!/usr/bin/env python3
"""Draw a notifier node tree (the /notify schema) as a PNG, approximately as the TV would.

    preview.py tree.json out.png [--bg art.jpg] [--fetch]

Needs Pillow, and cairosvg (with libcairo) for icons and graphs. Text uses a system sans-serif, so widths are close
but not exact. http(s) images are drawn as labelled placeholders unless --fetch is given (no auth is sent).
"""
import base64, io, json, sys, urllib.request

from PIL import Image, ImageDraw, ImageFont

try:
    import cairosvg
except Exception:  # noqa: BLE001 - optional
    cairosvg = None

FONTS = ["/System/Library/Fonts/HelveticaNeue.ttc", "/System/Library/Fonts/Helvetica.ttc",
         "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"]


def font(px, bold=False):
    for f in FONTS:
        try:
            return ImageFont.truetype(f, max(1, int(px)), index=1 if bold and f.endswith(".ttc") else 0)
        except Exception:  # noqa: BLE001
            continue
    return ImageFont.load_default()


def color(c, default=(0, 0, 0, 0)):
    if c is None:
        return default
    if isinstance(c, list):
        v = [float(x) for x in c]
        if any(x > 1 for x in v[:3]):
            return tuple(int(x) for x in v[:3]) + (int(v[3] if len(v) > 3 and v[3] > 1 else (v[3] if len(v) > 3 else 1) * 255),)
        return tuple(int(x * 255) for x in v[:3]) + (int((v[3] if len(v) > 3 else 1) * 255),)
    s = str(c).strip().lower()
    named = {"white": "#ffffff", "black": "#000000", "red": "#ff0000", "green": "#00ff00", "blue": "#0000ff",
             "yellow": "#ffff00", "transparent": "#00000000"}
    s = named.get(s, s).lstrip("#")
    if len(s) in (3, 4):
        s = "".join(ch * 2 for ch in s)
    if len(s) == 6:
        s += "ff"
    try:
        return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4, 6))
    except ValueError:
        return default


def wrap(draw, text, f, w):
    lines = []
    for para in str(text).split("\n"):
        cur = ""
        for word in para.split(" "):
            t = (cur + " " + word).strip()
            if draw.textlength(t, font=f) <= w or not cur:
                cur = t
            else:
                lines.append(cur)
                cur = word
        lines.append(cur)
    return lines


def ellipsize(draw, s, f, w):
    if draw.textlength(s, font=f) <= w:
        return s
    while s and draw.textlength(s + "…", font=f) > w:
        s = s[:-1]
    return s + "…"


def load_src(src, w, h, fetch):
    if src.startswith("data:image/svg+xml;base64,") and cairosvg:
        svg = base64.b64decode(src[26:])
        png = cairosvg.svg2png(bytestring=svg, output_width=max(1, int(w)), output_height=max(1, int(h)))
        return Image.open(io.BytesIO(png)).convert("RGBA")
    if src.startswith("data:image/"):
        return Image.open(io.BytesIO(base64.b64decode(src.split(",", 1)[1]))).convert("RGBA")
    if fetch and src.startswith("http"):
        try:
            return Image.open(io.BytesIO(urllib.request.urlopen(src, timeout=5).read())).convert("RGBA")
        except Exception:  # noqa: BLE001
            return None
    return None


def rounded_mask(w, h, r):
    m = Image.new("L", (max(1, int(w)), max(1, int(h))), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, int(w) - 1, int(h) - 1], radius=int(r), fill=255)
    return m


def draw_node(canvas, n, ox, oy, pw, ph, fetch):
    if n.get("visible") is False:
        return
    t = n.get("type", "box")
    x, y = ox + n.get("x", 0), oy + n.get("y", 0)
    w = n.get("w", pw - n.get("x", 0))
    h = n.get("h", ph - n.get("y", 0))
    w, h = max(1, w), max(1, h)
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    r = n.get("radius", 0)
    if n.get("bg"):
        c = color(n["bg"])
        if r:
            d.rounded_rectangle([x, y, x + w - 1, y + h - 1], radius=r, fill=c)
        else:
            d.rectangle([x, y, x + w - 1, y + h - 1], fill=c)
    if n.get("gradient"):
        g = n["gradient"]["colors"]
        c1, c2 = color(g[0]), color(g[-1])
        for i in range(int(w)):
            f = i / max(1, w - 1)
            d.line([x + i, y, x + i, y + h - 1], fill=tuple(int(c1[k] + (c2[k] - c1[k]) * f) for k in range(4)))
    if t == "text":
        px = n.get("pixel_size") or n.get("size", 20) * 1.33
        f = font(px, n.get("bold") or n.get("weight") in ("bold", "medium", "semiBold", "demiBold"))
        col = color(n.get("color"), (0, 0, 0, 255))
        lines = wrap(d, n.get("text", ""), f, w) if n.get("multiline") else [str(n.get("text", ""))]
        lh = px * 1.25
        if "h" in n and not n.get("multiline"):
            lines = [ellipsize(d, lines[0], f, w)] if n.get("ellipsis", True) else lines
        total = lh * len(lines)
        va = n.get("valign", "top")
        ty = y + (h - total) / 2 if va in ("center", "middle") and "h" in n else y + h - total if va == "bottom" and "h" in n else y
        for line in lines:
            if "h" in n and ty + lh > y + h + 2:
                break
            tw = d.textlength(line, font=f)
            al = n.get("align", "left")
            tx = x + (w - tw) / 2 if al == "center" else x + w - tw if al in ("right", "end") else x
            d.text((tx, ty + (lh - px) / 2), line, font=f, fill=col)
            ty += lh
    if t in ("image", "gif", "lottie", "video") and n.get("src"):
        img = load_src(n["src"], w, h, fetch)
        if img is None:
            d.rectangle([x, y, x + w - 1, y + h - 1], fill=(60, 60, 70, 200))
            label = ("camera " if "camera_proxy" in n["src"] else "image ") + ("(refresh %d ms)" % n["refresh_ms"] if n.get("refresh_ms") else "")
            d.text((x + 8, y + 8), label, font=font(14), fill=(220, 220, 220, 255))
        else:
            fit = n.get("fit", "contain")
            iw, ih = img.size
            if fit == "fill" or n["src"].startswith("data:image/svg"):
                img = img.resize((int(w), int(h)))
            else:
                s = max(w / iw, h / ih) if fit == "cover" else min(w / iw, h / ih)
                img = img.resize((max(1, int(iw * s)), max(1, int(ih * s))))
                if fit == "cover":
                    l, tp = (img.size[0] - int(w)) // 2, (img.size[1] - int(h)) // 2
                    img = img.crop((l, tp, l + int(w), tp + int(h)))
            mask = img.split()[3]
            if r:
                rm = rounded_mask(img.size[0], img.size[1], r)
                mask = Image.composite(mask, Image.new("L", img.size, 0), rm)
            layer.paste(img, (int(x + (w - img.size[0]) / 2), int(y + (h - img.size[1]) / 2)), mask)
    canvas.alpha_composite(layer)
    op = n.get("opacity")
    for c in n.get("children", []) or []:
        draw_node(canvas, c, x, y, w, h, fetch)
    _ = op


def background(size, path=None):
    if path:
        return Image.open(path).convert("RGBA").resize(size)
    # a stand-in for art: warm-to-cool diagonal wash with some shapes, so transparency is visible
    w, h = size
    bg = Image.new("RGBA", size)
    d = ImageDraw.Draw(bg)
    for i in range(h):
        f = i / h
        d.line([0, i, w, i], fill=(int(180 - 90 * f), int(150 - 40 * f), int(110 + 60 * f), 255))
    d.ellipse([w * 0.55, h * 0.15, w * 0.85, h * 0.7], fill=(230, 200, 120, 255))
    d.rectangle([w * 0.1, h * 0.55, w * 0.4, h * 0.95], fill=(70, 90, 120, 255))
    return bg


def main():
    a = sys.argv[1:]
    if len(a) < 2:
        print(__doc__)
        return 2
    tree = json.load(open(a[0]))
    bgp = a[a.index("--bg") + 1] if "--bg" in a else None
    canvas = background((1920, 1080), bgp)
    root = tree.get("root", tree)
    draw_node(canvas, root, tree.get("x", 0), tree.get("y", 0), tree.get("w", 1920), tree.get("h", 1080), "--fetch" in a)
    canvas.convert("RGB").save(a[1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
