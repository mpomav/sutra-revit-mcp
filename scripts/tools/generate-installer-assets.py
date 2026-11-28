"""Generate Inno Setup icon (.ico) and wizard bitmaps (.bmp) with Sutra + SCIO DC branding.

Outputs under installer/assets/:
  - sutra-setup.ico    (multi-size 16..256 icon for Setup exe and Apps & Features)
  - wizard-small.bmp   (55x58 top-right header badge, Sutra icon on light surface)
  - wizard-banner.bmp  (164x314 left welcome/finish banner with Sutra icon + SCIO DC mark)
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

REPO = Path(__file__).resolve().parent.parent.parent
SRC_ICON = REPO / "docs" / "assets" / "sutra-icon.png"
OUT_DIR = REPO / "build" / "assets"

# SCIO DC & Sutra palette
OBSIDIAN_TOP = (21, 20, 24)      # #151418
OBSIDIAN_BOT = (13, 13, 16)      # #0D0D10
SCIO_ORANGE = (224, 100, 0)      # #E06400
SUTRA_GOLD = (242, 145, 50)      # #F29132
CREAM = (255, 228, 173)          # #FFE4AD
MUTED = (168, 164, 160)
WHITE = (255, 255, 255)


def load_font(size: int, bold: bool = False):
    candidates = (
        ["C:/Windows/Fonts/segoeuib.ttf", "C:/Windows/Fonts/arialbd.ttf"]
        if bold
        else ["C:/Windows/Fonts/segoeui.ttf", "C:/Windows/Fonts/arial.ttf"]
    )
    for path in candidates:
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def make_ico(icon_rgba: Image.Image) -> None:
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    icon_rgba.save(OUT_DIR / "sutra-setup.ico", format="ICO", sizes=sizes)


def make_small_bmp(icon_rgba: Image.Image) -> None:
    # Inno Setup WizardSmallImageFile multi-DPI (100%..250%) + legacy 100% wizard-small.bmp
    dpi_specs = [
        (100, 55, 58),
        (125, 69, 73),
        (150, 83, 87),
        (175, 96, 102),
        (200, 110, 116),
        (225, 124, 131),
        (250, 138, 145),
    ]
    for pct, w, h in dpi_specs:
        canvas = Image.new("RGBA", (w, h), WHITE + (255,))
        emblem_size = int(round(w * 0.84))
        pad_top = max(2, int(round(h * 0.06)))
        emblem = icon_rgba.resize((emblem_size, emblem_size), Image.Resampling.LANCZOS)
        canvas.alpha_composite(emblem, ((w - emblem_size) // 2, pad_top))
        draw = ImageDraw.Draw(canvas)
        bar_h = max(2, int(round(h * 0.045)))
        bar_pad = max(6, int(round(w * 0.16)))
        draw.rectangle([bar_pad, h - bar_h - 1, w - bar_pad, h - 1], fill=SCIO_ORANGE + (255,))
        rgb = canvas.convert("RGB")
        rgb.save(OUT_DIR / f"sutra-small-{pct}.bmp", format="BMP")
        if pct == 100:
            rgb.save(OUT_DIR / "wizard-small.bmp", format="BMP")


def make_banner_bmp(icon_rgba: Image.Image) -> None:
    # Inno Setup WizardImageFile: 164x314 (24-bit BMP)
    w, h = 164, 314
    bg = Image.new("RGBA", (w, h), OBSIDIAN_TOP + (255,))
    px = bg.load()
    for y in range(h):
        t = y / max(1, h - 1)
        r = int(OBSIDIAN_TOP[0] * (1 - t) + OBSIDIAN_BOT[0] * t)
        g = int(OBSIDIAN_TOP[1] * (1 - t) + OBSIDIAN_BOT[1] * t)
        b = int(OBSIDIAN_TOP[2] * (1 - t) + OBSIDIAN_BOT[2] * t)
        for x in range(w):
            px[x, y] = (r, g, b, 255)

    draw = ImageDraw.Draw(bg)

    # Subtle architectural grid lines in background
    for gx in range(20, w, 24):
        draw.line([(gx, 0), (gx, h)], fill=(255, 228, 173, 12), width=1)
    for gy in range(20, h, 24):
        draw.line([(0, gy), (w, gy)], fill=(255, 228, 173, 12), width=1)

    # Top SCIO orange accent bar
    draw.rectangle([0, 0, w, 4], fill=SCIO_ORANGE + (255,))

    # Sutra emblem (108x108) centered upper-middle
    emblem = icon_rgba.resize((108, 108), Image.Resampling.LANCZOS)
    bg.alpha_composite(emblem, ((w - 108) // 2, 34))

    # Wordmark: SUTRA
    f_title = load_font(22, bold=True)
    f_sub = load_font(10, bold=False)
    f_scio = load_font(13, bold=True)
    f_tag = load_font(9, bold=False)

    title = "SUTRA"
    tb = draw.textbbox((0, 0), title, font=f_title)
    draw.text(((w - (tb[2] - tb[0])) // 2, 152), title, font=f_title, fill=WHITE + (255,))

    sub = "REVIT · CAD · MCP"
    sb = draw.textbbox((0, 0), sub, font=f_sub)
    draw.text(((w - (sb[2] - sb[0])) // 2, 180), sub, font=f_sub, fill=SUTRA_GOLD + (255,))

    # Divider line
    draw.line([(32, 214), (w - 32, 214)], fill=(224, 100, 0, 140), width=1)

    # SCIO DC geometric mark + wordmark at bottom
    # Orange square badge with stylized "S" / architectural bracket
    badge_x, badge_y, badge_s = 28, 242, 26
    draw.rounded_rectangle(
        [badge_x, badge_y, badge_x + badge_s, badge_y + badge_s],
        radius=5,
        fill=SCIO_ORANGE + (255,),
    )
    f_mark = load_font(15, bold=True)
    mb = draw.textbbox((0, 0), "S", font=f_mark)
    draw.text(
        (badge_x + (badge_s - (mb[2] - mb[0])) // 2, badge_y + (badge_s - (mb[3] - mb[1])) // 2 - 2),
        "S",
        font=f_mark,
        fill=WHITE + (255,),
    )
    draw.text((badge_x + badge_s + 8, badge_y - 1), "SCIO DC", font=f_scio, fill=WHITE + (255,))
    draw.text((badge_x + badge_s + 8, badge_y + 15), "Grupo POVA", font=f_tag, fill=MUTED + (255,))

    # Bottom footer tagline
    foot = "sciodc.com"
    fb = draw.textbbox((0, 0), foot, font=f_tag)
    draw.text(((w - (fb[2] - fb[0])) // 2, h - 22), foot, font=f_tag, fill=(140, 136, 132, 255))

    bg.convert("RGB").save(OUT_DIR / "wizard-banner.bmp", format="BMP")


def main() -> None:
    if not SRC_ICON.exists():
        raise FileNotFoundError(f"Missing source icon: {SRC_ICON}")
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    icon_rgba = Image.open(SRC_ICON).convert("RGBA")
    make_ico(icon_rgba)
    make_small_bmp(icon_rgba)
    make_banner_bmp(icon_rgba)
    for name in ("sutra-setup.ico", "wizard-small.bmp", "wizard-banner.bmp"):
        p = OUT_DIR / name
        print(f"wrote {p.relative_to(REPO)} ({p.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
