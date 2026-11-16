# LiteView's own icon (src/app/liteview.ico), drawn here so it can be changed and re-made:
#   python3 tools/make-icon.py src/app/liteview.ico      (needs Pillow)
# A purple tile, a white chat bubble (a stream and its chat), a purple play triangle in it. Twitch-like colours, but
# not Twitch's logo, and not mpv's icon (which LiteView's window used to borrow from mpv.exe).
import sys
from PIL import Image, ImageDraw
S = 1024
def render(S):
    big = S * 4
    im = Image.new('RGBA', (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    r = int(big * 0.22)
    # tile with a vertical gradient
    grad = Image.new('RGBA', (big, big))
    top, bot = (169, 112, 255), (119, 44, 232)
    gd = ImageDraw.Draw(grad)
    for y in range(big):
        t = y / (big - 1)
        gd.line([(0, y), (big, y)], fill=tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new('L', (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=r, fill=255)
    im.paste(grad, (0, 0), mask)
    # bubble
    x0, y0, x1, y1 = big * 0.17, big * 0.20, big * 0.83, big * 0.70
    d.rounded_rectangle([x0, y0, x1, y1], radius=int(big * 0.09), fill=(255, 255, 255, 255))
    d.polygon([(big * 0.27, y1 - big * 0.02), (big * 0.27, big * 0.84), (big * 0.45, y1 - big * 0.02)], fill=(255, 255, 255, 255))
    # play triangle (purple), centred in the bubble
    cx, cy = (x0 + x1) / 2 + big * 0.02, (y0 + y1) / 2
    h = big * 0.25
    d.polygon([(cx - h * 0.42, cy - h / 2), (cx - h * 0.42, cy + h / 2), (cx + h * 0.5, cy)], fill=(145, 70, 255, 255))
    return im.resize((S, S), Image.LANCZOS)
master = render(1024)
sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
imgs = [master.resize((s, s), Image.LANCZOS) for s in sizes]
imgs[0].save(sys.argv[1], format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[1:])
