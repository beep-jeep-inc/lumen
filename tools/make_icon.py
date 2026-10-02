"""Иконка Lumen (pult.ico): скруглённый квадрат с чернильно-синим градиентом (как тема Theme/Ink.xaml),
лёгкое фиолетовое свечение и золотой знак «свет» — четырёхлучевая звезда с короткими диагональными лучами.
Для малых размеров (16-24 px) рисуется упрощённая версия: одна крупная звезда без тонких деталей.
Запуск: python tools\\make_icon.py <путь.ico>   (рядом кладётся <имя>_256.png; с ключом --previews <папка> — PNG всех размеров)"""
import math
import sys
from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 1024
BG_TOP, BG_BOT = (10, 14, 31), (18, 23, 58)          # #0A0E1F -> #12173A
VIOLET = (108, 92, 231)                              # #6C5CE7
GOLD, GOLD_LIGHT = (227, 184, 92), (255, 243, 205)   # #E3B85C -> светлее к центру


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_mask(radius):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle((28, 28, S - 28, S - 28), radius=radius, fill=255)
    return m


def background(glow):
    grad = Image.new("RGB", (S, S))
    px = grad.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(BG_TOP, BG_BOT, (x * 0.35 + y * 0.65) / (S - 1))
    if glow:
        # фиолетовое свечение чуть выше центра
        halo = Image.radial_gradient("L").resize((S, S), Image.BICUBIC)       # 0 в центре, 255 по краям
        halo = halo.point(lambda v: max(0, int((255 - v) * 0.55)) if v < 255 else 0)
        halo = halo.filter(ImageFilter.GaussianBlur(40))
        violet = Image.new("RGB", (S, S), VIOLET)
        grad = Image.composite(violet, grad, halo)
    return grad


def star_points(cx, cy, radius, power, rot=0.0, n=360):
    """Вогнутая четырёхлучевая звезда: x = R*cos^p, y = R*sin^p (p=3 — астроида, чем больше, тем тоньше лучи)."""
    pts = []
    for i in range(n):
        t = 2 * math.pi * i / n
        c, s = math.cos(t), math.sin(t)
        x = radius * math.copysign(abs(c) ** power, c)
        y = radius * math.copysign(abs(s) ** power, s)
        pts.append((cx + x * math.cos(rot) - y * math.sin(rot), cy + x * math.sin(rot) + y * math.cos(rot)))
    return pts


def shape_mask(shapes):
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    for pts in shapes:
        d.polygon(pts, fill=255)
    return m


def gold_fill(mask, cx, cy, reach):
    """Золото: светлее к центру, #E3B85C к концам лучей."""
    radial = Image.radial_gradient("L").resize((int(reach * 2), int(reach * 2)), Image.BICUBIC)  # 0 центр -> 255 край
    layer = Image.new("L", (S, S), 255)
    layer.paste(radial, (int(cx - reach), int(cy - reach)))
    layer = layer.point(lambda v: min(255, int(v * 1.0)))
    gold = Image.new("RGB", (S, S), GOLD)
    light = Image.new("RGB", (S, S), GOLD_LIGHT)
    colored = Image.composite(gold, light, layer)       # где layer велик — золото, у центра — светлый
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(colored, (0, 0), mask)
    return out


def render(small: bool) -> Image.Image:
    cx = cy = S // 2
    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = rounded_mask(210 if not small else 230)
    icon.paste(background(glow=not small), (0, 0), mask)

    if small:
        main = shape_mask([star_points(cx, cy, 430, 3.0)])
        glow_src = main
        layers = [gold_fill(main, cx, cy, 440)]
    else:
        main = shape_mask([star_points(cx, cy, 450, 4.4)])
        diag = shape_mask([star_points(cx, cy, 300, 4.0, rot=math.pi / 4)])
        both = ImageChops.lighter(main, diag)
        glow_src = both
        layers = [gold_fill(both, cx, cy, 450)]

    # золотое свечение вокруг знака
    halo = glow_src.filter(ImageFilter.GaussianBlur(26 if not small else 18)).point(lambda v: int(v * 0.55))
    glow = Image.new("RGBA", (S, S), GOLD + (0,))
    glow.putalpha(halo)
    icon.alpha_composite(glow)
    for layer in layers:
        icon.alpha_composite(layer)

    # яркое ядро в центре
    core_r = 70 if not small else 90
    core = Image.new("L", (S, S), 0)
    ImageDraw.Draw(core).ellipse((cx - core_r, cy - core_r, cx + core_r, cy + core_r), fill=255)
    core = core.filter(ImageFilter.GaussianBlur(core_r * 0.55))
    white = Image.new("RGBA", (S, S), (255, 250, 232, 0))
    white.putalpha(core.point(lambda v: int(v * 0.95)))
    icon.alpha_composite(white)

    # всё, что вышло за скруглённый квадрат, обрезаем
    a = ImageChops.multiply(icon.getchannel("A"), mask)
    icon.putalpha(a)
    return icon


def main(out: str, previews: str | None) -> None:
    big, small = render(False), render(True)
    sizes = [256, 128, 64, 48, 32, 24, 16]
    frames = {}
    for n in sizes:
        src = small if n <= 32 else big
        frames[n] = src.resize((n, n), Image.LANCZOS)
    ordered = [frames[n] for n in sizes]
    ordered[0].save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=ordered[1:])
    frames[256].save(out.rsplit(".", 1)[0] + "_256.png")
    if previews:
        for n in sizes:
            frames[n].save(f"{previews}/icon-{n}.png")
            if n < 64:   # маленькие — ещё и увеличенные в 8 раз без сглаживания, чтобы рассмотреть
                frames[n].resize((n * 8, n * 8), Image.NEAREST).save(f"{previews}/icon-{n}-x8.png")
    print("ok", out)


if __name__ == "__main__":
    args = sys.argv[1:]
    prev = None
    if "--previews" in args:
        i = args.index("--previews")
        prev = args[i + 1]
        del args[i:i + 2]
    main(args[0] if args else "pult.ico", prev)
