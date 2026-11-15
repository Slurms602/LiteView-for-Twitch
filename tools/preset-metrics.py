#!/usr/bin/env python3
"""Compares the picture presets' output with the original (CI fixture scene "presets").

tests/FakeStreamlink plays a known test picture (text, a Siemens star, fine lines, coloured edges, texture) at half
size after a JPEG round trip (blocks and ringing, like a stream); LiteView shows it 2x in fullscreen with each preset
and mpv saves what it shows (preset-<name>.png). This compares each with the full-size original (preset-truth.png),
on brightness (luma, BT.709 weights) inside the video area:

  PSNR, SSIM   closeness to the original (higher is better)
  overshoot    how far pixels go beyond the original's local brightest/darkest (3x3): the bright and dark halos of
               oversharpening, in luma levels per pixel (lower is better)
  detail       edge energy relative to the original (1.00 = as detailed as the original; below: softer; above:
               sharper than the original, i.e. oversharpened or noisy)

Needs only Pillow:  python3 tools/preset-metrics.py <folder with preset-*.png> [--markdown]
"""
import sys, os
from PIL import Image, ImageChops, ImageFilter, ImageMath, ImageStat


def luma(img):
    return img.convert("RGB").convert("L", (0.2126, 0.7152, 0.0722, 0))


def video_area(shot, truth_size):
    """The 16:9 picture inside the window screenshot (letterboxed), resized to the original's size if needed."""
    w, h = shot.size
    tw, th = truth_size
    vw, vh = (w, round(w * th / tw)) if w * th <= h * tw else (round(h * tw / th), h)
    x, y = (w - vw) // 2, (h - vh) // 2
    area = shot.crop((x, y, x + vw, y + vh))
    return area if area.size == truth_size else area.resize(truth_size, Image.LANCZOS)


def psnr(a, b):
    rms = ImageStat.Stat(ImageChops.difference(a, b)).rms[0]
    return 99.0 if rms == 0 else 20 * __import__("math").log10(255.0 / rms)


def ssim(a, b):
    # SSIM over 8x8 blocks (box means by resizing; Pillow's blur filters do not take float images)
    fa, fb = a.convert("F"), b.convert("F")
    size = (max(1, a.size[0] // 8), max(1, a.size[1] // 8))
    blur = lambda im: im.resize(size, Image.BOX)
    ma, mb = blur(fa), blur(fb)
    aa = blur(ImageMath.lambda_eval(lambda d: d["x"] * d["x"], x=fa))
    bb = blur(ImageMath.lambda_eval(lambda d: d["x"] * d["x"], x=fb))
    ab = blur(ImageMath.lambda_eval(lambda d: d["x"] * d["y"], x=fa, y=fb))
    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    m = ImageMath.lambda_eval(lambda d: ((2 * d["ma"] * d["mb"] + c1) * (2 * (d["ab"] - d["ma"] * d["mb"]) + c2)) /
                              ((d["ma"] * d["ma"] + d["mb"] * d["mb"] + c1) * ((d["aa"] - d["ma"] * d["ma"]) + (d["bb"] - d["mb"] * d["mb"]) + c2)),
                              ma=ma, mb=mb, aa=aa, bb=bb, ab=ab)
    vals = list(m.getdata())   # not ImageStat: it reads float images through a 256-bin histogram
    return sum(vals) / len(vals)


def overshoot(out, truth):
    hi, lo = truth.filter(ImageFilter.MaxFilter(3)), truth.filter(ImageFilter.MinFilter(3))
    above, below = ImageChops.subtract(out, hi), ImageChops.subtract(lo, out)
    return ImageStat.Stat(above).mean[0] + ImageStat.Stat(below).mean[0]


def detail(img):
    return ImageStat.Stat(img.filter(ImageFilter.FIND_EDGES)).mean[0]


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    folder = args[0] if args else "."
    truth_rgb = Image.open(os.path.join(folder, "preset-truth.png"))
    truth = luma(truth_rgb)
    order = ["off", "enhance", "high-soft", "high", "high-strong", "ultra-nosharpen", "ultra-soft", "ultra", "ultra-old"]
    names = [n for n in order if os.path.exists(os.path.join(folder, "preset-" + n + ".png"))]
    rows = []
    for n in names:
        out = luma(video_area(Image.open(os.path.join(folder, "preset-" + n + ".png")), truth.size))
        rows.append((n, psnr(out, truth), ssim(out, truth), overshoot(out, truth), detail(out) / detail(truth)))
    if "--markdown" in sys.argv:
        print("| Preset | PSNR (dB) | SSIM | Overshoot | Detail |")
        print("|---|---|---|---|---|")
        for r in rows: print("| %s | %.2f | %.4f | %.2f | %.2f |" % r)
    else:
        print("%-16s %9s %7s %10s %7s" % ("preset", "PSNR dB", "SSIM", "overshoot", "detail"))
        for r in rows: print("%-16s %9.2f %7.4f %10.2f %7.2f" % r)
    same = [(a[0], b[0]) for i, a in enumerate(rows) for b in rows[i + 1:] if abs(a[1] - b[1]) < 1e-9 and abs(a[2] - b[2]) < 1e-9]
    if same: print("note: identical results (a shader may not have run):", same)


if __name__ == "__main__":
    main()
