# Builds the DOLLAR & Co brand files for the desktop app (needs Pillow):
#
#   python tools/make_brand.py ../pos_frontend/public/images/logo.png assets
#
# -> assets/logo.png            the full "DOLLAR & Co" wordmark on its own dark
#                               rounded plate (embedded in the exe, drawn in every
#                               window's header). The plate is part of the image so
#                               the white "Co" shows on ANY background.
#    assets/icon-1024.png       the app icon: "D&" over "Co", centred, on the
#                               near-black rounded square.
#    app.ico                    that icon at 16, 24, 32, 48, 64, 128 and 256 px,
#                               each size drawn on its own (not one picture shrunk)
#                               so small sizes stay sharp.
#    assets/installer-*.bmp     Inno Setup's side picture (welcome page) and the
#                               small picture (top right of the other pages).
#
# Brand: yellow #FFD60A "DOLLAR", green #22C55E "&", white "Co" on #0B0B0B,
# in Arial Black (the website logo's face).
import os
import sys
from PIL import Image, ImageDraw, ImageFont

src_logo = sys.argv[1]
out = sys.argv[2]
root = os.path.dirname(os.path.abspath(out.rstrip('/\\')))

BG = (11, 11, 11)
YELLOW = (255, 214, 10)
GREEN = (34, 197, 94)
WHITE = (255, 255, 255)
FONT = 'C:/Windows/Fonts/ariblk.ttf'


def rounded_mask(size, radius, scale=4):
    """Anti-aliased rounded-rectangle alpha mask."""
    w, h = size
    big = Image.new('L', (w * scale, h * scale), 0)
    ImageDraw.Draw(big).rounded_rectangle((0, 0, w * scale - 1, h * scale - 1), radius=radius * scale, fill=255)
    return big.resize(size, Image.LANCZOS)


# ---------------------------------------------------------------- 1) wordmark
# The website logo is already the right letters on #0B0B0B: crop it to the
# letters plus an even margin, and round the plate's corners.
lg = Image.open(src_logo).convert('RGB')
px = lg.load()
W, H = lg.size
xs, ys = [], []
for y in range(H):
    for x in range(W):
        p = px[x, y]
        if max(abs(p[i] - BG[i]) for i in range(3)) > 40:
            xs.append(x)
            ys.append(y)
x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
pad_y = int(round((y1 - y0) * 0.30))
pad_x = int(round((y1 - y0) * 0.42))
box = (max(0, x0 - pad_x), max(0, y0 - pad_y), min(W, x1 + 1 + pad_x), min(H, y1 + 1 + pad_y))
plate = Image.new('RGB', (box[2] - box[0], box[3] - box[1]), BG)
plate.paste(lg.crop(box), (0, 0))
wordmark = plate.convert('RGBA')
wordmark.putalpha(rounded_mask(wordmark.size, int(wordmark.size[1] * 0.18)))
wordmark.save(os.path.join(out, 'logo.png'))


# -------------------------------------------------------------------- 2) icon
def ink_layer(text_runs, font_px):
    """One line of coloured runs, cropped to its ink. Returns an RGBA image."""
    font = ImageFont.truetype(FONT, font_px)
    width = sum(int(font.getlength(t)) for t, _ in text_runs) + font_px
    layer = Image.new('RGBA', (width, font_px * 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    x = font_px // 2
    for t, colour in text_runs:
        d.text((x, font_px // 2), t, font=font, fill=colour + (255,))
        x += font.getlength(t)
    return layer.crop(layer.getbbox())


def make_icon(size):
    """The icon drawn for one pixel size (from an 8x canvas, then reduced)."""
    k = 8 if size <= 64 else 4
    S = size * k
    icon = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    radius = int(S * 0.20) if size > 24 else int(S * 0.16)
    plate = Image.new('RGBA', (S, S), BG + (255,))
    icon.paste(plate, (0, 0), rounded_mask((S, S), radius, 1))

    # Small sizes get bigger letters (less margin): they must still read at 16 px.
    inner = 0.80 if size <= 24 else 0.70
    gap_ratio = 0.10 if size <= 24 else 0.08
    font_px = S
    top = ink_layer([('D', YELLOW), ('&', GREEN)], font_px)
    bottom = ink_layer([('Co', WHITE)], font_px)
    # One scale for both lines, so the letters are the same height.
    block_w = max(top.width, bottom.width)
    block_h = top.height + bottom.height + gap_ratio * font_px
    scale = min(inner * S / block_w, inner * S / block_h)
    top = top.resize((max(1, int(top.width * scale)), max(1, int(top.height * scale))), Image.LANCZOS)
    bottom = bottom.resize((max(1, int(bottom.width * scale)), max(1, int(bottom.height * scale))), Image.LANCZOS)
    gap = int(gap_ratio * font_px * scale)
    total_h = top.height + gap + bottom.height
    y = (S - total_h) // 2
    icon.alpha_composite(top, ((S - top.width) // 2, y))
    icon.alpha_composite(bottom, ((S - bottom.width) // 2, y + top.height + gap))
    return icon.resize((size, size), Image.LANCZOS)


big = make_icon(1024)
big.save(os.path.join(out, 'icon-1024.png'))
sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [make_icon(s) for s in sizes]
frames[-1].save(os.path.join(root, 'app.ico'), format='ICO', sizes=[(s, s) for s in sizes],
                append_images=frames[:-1])

# ---------------------------------------------------------- 3) installer BMPs
# Small picture: top right of the wizard's white header band (55x58 at 100 %,
# drawn at 250 % for high-DPI screens).
small = Image.new('RGB', (138, 140), (255, 255, 255))
ic = make_icon(124)
small.paste(ic, ((138 - 124) // 2, (140 - 124) // 2), ic)
small.save(os.path.join(out, 'installer-small.bmp'))

# Side picture: the welcome / finish pages (164x314 at 250 %).
side = Image.new('RGB', (410, 797), BG)
ic = make_icon(220)
side.paste(ic, ((410 - 220) // 2, 170), ic)
wm_w = 350
wm = wordmark.resize((wm_w, int(round(wm_w * wordmark.size[1] / wordmark.size[0]))), Image.LANCZOS)
side.paste(wm, ((410 - wm_w) // 2, 430), wm)
d = ImageDraw.Draw(side)
f = ImageFont.truetype('C:/Windows/Fonts/segoeuib.ttf', 30)
t = 'Attendance  ·  Sales'
w = d.textlength(t, font=f)
d.text(((410 - w) / 2, 430 + wm.size[1] + 24), t, font=f, fill=(200, 200, 200))
side.save(os.path.join(out, 'installer-side.bmp'))
print('ok: logo', wordmark.size, 'icon sizes', sizes)
