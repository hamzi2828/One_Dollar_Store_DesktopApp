# Builds the DOLLAR & Co brand files from the website logo files (needs Pillow):
#   python tools/make_brand.py ../pos_frontend/public/images/logo.png ../pos_frontend/public/apple-touch-icon.png assets
# -> assets/logo.png (header logo), assets/icon-1024.png, app.ico, assets/installer-*.bmp
import sys
from PIL import Image, ImageDraw, ImageFont
src_logo, src_icon, out = sys.argv[1], sys.argv[2], sys.argv[3]
BG = (10, 10, 10)
FG = [(255, 214, 10), (34, 197, 94), (255, 255, 255)]

# 1) Wordmark with a transparent background (un-blend each pixel from the dark bg).
lg = Image.open(src_logo).convert('RGB')
lg = lg.crop((69 - 6, 44 - 6, 941 + 7, 131 + 7))
o = Image.new('RGBA', lg.size)
px, opx = lg.load(), o.load()
for y in range(lg.size[1]):
    for x in range(lg.size[0]):
        p = px[x, y]
        best = None
        for f in FG:
            d = [f[i] - BG[i] for i in range(3)]
            a = sum((p[i] - BG[i]) * d[i] for i in range(3)) / sum(v * v for v in d)
            a = max(0.0, min(1.0, a))
            err = sum((BG[i] + a * d[i] - p[i]) ** 2 for i in range(3))
            if best is None or err < best[0]: best = (err, f, a)
        _, f, a = best
        opx[x, y] = (f[0], f[1], f[2], int(round(a * 255)))
o.save(out + '/logo.png')

# 2) Square icon matching the website's apple-touch-icon ("D" yellow, "&" green on black).
def glyph(ch, colour, box, S):
    font = ImageFont.truetype('C:/Windows/Fonts/ariblk.ttf', S)
    tmp = Image.new('L', (S * 2, S * 2), 0)
    ImageDraw.Draw(tmp).text((S // 2, S // 4), ch, font=font, fill=255)
    tmp = tmp.crop(tmp.getbbox())
    w, h = box[2] - box[0], box[3] - box[1]
    tmp = tmp.resize((w, h), Image.LANCZOS)
    layer = Image.new('RGBA', (w, h), colour + (0,))
    layer.putalpha(tmp)
    return layer
S = 1024
k = S / 180.0
icon = Image.new('RGBA', (S, S), (0, 0, 0, 0))
ImageDraw.Draw(icon).rounded_rectangle((0, 0, S - 1, S - 1), radius=int(0.2 * S), fill=BG + (255,))
for ch, col, b in (('D', FG[0], (26, 52, 95, 127)), ('&', FG[1], (109, 80, 158, 127))):
    box = tuple(int(round(v * k)) for v in b)
    g = glyph(ch, col, box, 900)
    icon.alpha_composite(g, (box[0], box[1]))
icon.save(out + '/icon-1024.png')
sizes = [16, 24, 32, 48, 64, 128, 256]
icon.resize((256, 256), Image.LANCZOS).save(out + '/../app.ico', sizes=[(s, s) for s in sizes])

# 3) Installer pictures (BMP, what Inno Setup reads everywhere).
small = Image.new('RGB', (138, 140), (255, 255, 255))
ic = icon.resize((120, 120), Image.LANCZOS)
small.paste(ic, (9, 10), ic)
small.save(out + '/installer-small.bmp')
side = Image.new('RGB', (410, 797), BG)   # 164x314 at 250% for sharp high-DPI
ic = icon.resize((220, 220), Image.LANCZOS)
side.paste(ic, ((410 - 220) // 2, 170), ic)
wm = o.resize((350, int(350 * o.size[1] / o.size[0])), Image.LANCZOS)
side.paste(wm, ((410 - 350) // 2, 440), wm)
d = ImageDraw.Draw(side)
f = ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf', 30)
t = 'Attendance'
w = d.textlength(t, font=f)
d.text(((410 - w) / 2, 500), t, font=f, fill=(200, 200, 200))
side.save(out + '/installer-side.bmp')
print('ok', o.size)
