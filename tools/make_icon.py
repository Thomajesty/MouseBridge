"""由参考图（**黑色圆角方块 + 青→蓝渐变无限符号**）生成 Windows 图标。

严格复刻参考图 1000403342.png / _icon_src.jpg，实测数据（源图 1920×1920）：
    磁贴外框  x[100,1818] y[102,1816]  → 占画布 0.896，中心 (959,959)
    磁贴轮廓  拟合超椭圆 |u|^n+|v|^n=1 → **n = 6.0**（rms 0.045）
    磁贴底色  近黑 #02040E ~ #040613（不是纯黑，带一点冷调）
    符号外框  x[310,1612] y[634,1270]  → 宽/磁贴 = 0.757、高/磁贴 = 0.371
    符号位置  左偏移 0.122、上偏移 0.310（垂直居中略偏上）
    符号配色  左端青 #22EBF3 → 右端蓝 #0D3BEA（交叉处呈 #3AF4F9 高光）

为什么是「黑色磁贴」而不是纯透明符号：
    ∞ 本身是 2:1 的扁形，只把符号抠成透明当图标时它只占画布约 46% 高、
    墨迹覆盖率约 30%（正常应用图标 66%），固定到任务栏后视觉上比旁边的
    Edge/Chrome 小一半 —— 这正是用户反馈的"图标显示过小"。填满画布的磁贴
    能把方形轮廓撑满，符号仍保留参考图里的原始比例与配色。

各尺寸一律从 256 母版整体重采样：
    逐尺寸单独加粗/二值化会把 ∞ 中间两个孔直接糊死（16px 变一根横条），
    这一点已实测踩过，所以只保留"按原尺寸再裁一次超椭圆"让边缘利落。

用法：
    python tools/make_icon.py <源图> [输出目录] [--preview]
"""
import io
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFilter

# Windows 图标会用到的尺寸。20/24/40 是任务栏与"中等图标"档，别省。
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

# ---- 参考图实测参数（不要凭感觉改，改了就不是"那张图"了）----
TILE_RATIO = 0.896        # 磁贴边长 / 画布边长
TILE_N = 6.0              # 磁贴轮廓超椭圆指数（拟合值，rms 0.045）
TILE_DARK = (3, 5, 17)    # 磁贴底色，近黑带冷调
GLYPH_W_RATIO = 0.757     # 符号宽 / 磁贴边长
GLYPH_DX = 0.122          # 符号左边缘相对磁贴左边缘
GLYPH_DY = 0.310          # 符号上边缘相对磁贴上边缘
GRAD_L = (34, 235, 243)   # 符号左端：青 #22EBF3
GRAD_R = (13, 59, 234)    # 符号右端：蓝 #0D3BEA
CROSS_HL = (58, 244, 249)  # 交叉处高光（参考图中心实测 #3AF4F9）
GLYPH_MARGIN = 0.06       # 蒙版向外扩的比例，补偿重采样时的边缘收缩

MASTER = 256


def lum(p) -> float:
    return 0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]


def extract_glyph_mask(im: Image.Image):
    """把亮色符号从深色方块里抠出来，返回 (单通道蒙版, 原始彩色符号)。

    先按暗色轮廓定位方块范围，只在方块内部做亮度抠图 —— 源图方块外还有一圈
    浅色纸底（灰度约 250），亮度很高，不先排除会被当成符号。
    """
    im = im.convert("RGB")
    w, h = im.size
    px = im.load()

    step = 4
    xs_dark, ys_dark = [], []
    for y in range(0, h, step):
        for x in range(0, w, step):
            if lum(px[x, y]) < 110:
                xs_dark.append(x)
                ys_dark.append(y)
    if not xs_dark:
        raise SystemExit("没找到深色方块，请确认源图")
    bx0, bx1 = min(xs_dark), max(xs_dark)
    by0, by1 = min(ys_dark), max(ys_dark)

    mask = Image.new("L", (w, h), 0)
    color = Image.new("RGB", (w, h), (0, 0, 0))
    mp = mask.load()
    cp = color.load()

    def alpha_of(l: float) -> int:
        # 磁贴底 lum≈5、符号本体 lum≈120~200，阈值取 36~80
        if l <= 36:
            return 0
        if l >= 80:
            return 255
        return int((l - 36) / (80 - 36) * 255)

    for y in range(max(0, by0 + 2), min(h, by1 - 1)):
        for x in range(max(0, bx0 + 2), min(w, bx1 - 1)):
            p = px[x, y]
            # 中性灰白（三通道接近）一律当背景：方块圆角外的浅色纸底就是这种
            if max(p) - min(p) < 30:
                continue
            mp[x, y] = alpha_of(lum(p))
            cp[x, y] = p

    mask = mask.filter(ImageFilter.SMOOTH_MORE)

    bbox = mask.point(lambda a: 255 if a > 16 else 0).getbbox()
    if not bbox:
        raise SystemExit("没找到亮色符号，请确认源图")
    # 往外扩一点，重采样时符号边缘才不会显得比母版瘦
    gx0, gy0, gx1, gy1 = bbox
    mw, mh = gx1 - gx0, gy1 - gy0
    padx, pady = int(mw * GLYPH_MARGIN), int(mh * GLYPH_MARGIN)
    box = (max(0, gx0 - padx), max(0, gy0 - pady),
           min(w, gx1 + padx), min(h, gy1 + pady))
    print(f"深色方块 x[{bx0},{bx1}] y[{by0},{by1}]；符号外框 {bbox} → 扩边 {box}")
    return mask.crop(box), color.crop(box)


def squircle_mask(side: int, n: float = TILE_N) -> Image.Image:
    """超椭圆 |x|^n+|y|^n=1 的实心遮罩。

    ImageDraw.rounded_rectangle 画不出参考图那个"方中带圆"的形（n=6 比圆角
    矩形饱满得多，四角更方、边更直），所以按超方程逐像素生成；先在 4 倍
    分辨率画好再 LANCZOS 缩回，边缘才有抗锯齿。
    """
    ss = side * 4
    m = Image.new("L", (ss, ss), 0)
    px = m.load()
    c = (ss - 1) / 2.0
    inv = 1.0 / c
    for y in range(ss):
        v = abs((y - c) * inv)
        vv = v ** n
        for x in range(ss):
            u = abs((x - c) * inv)
            if (u ** n + vv) <= 1.0:
                px[x, y] = 255
    return m.resize((side, side), Image.LANCZOS)


def glyph_gradient(gw: int, gh: int) -> Image.Image:
    """符号的青→蓝渐变。

    横向为主、纵向带一点变化（参考图左上偏青、右下偏蓝），另外在中间交叉区
    叠一层高光，还原原图那种"两条笔画交叠处发亮"的观感。
    """
    n = 128
    g = Image.new("RGB", (n, n))
    px = g.load()
    for y in range(n):
        for x in range(n):
            u = x / (n - 1.0)
            v = y / (n - 1.0)
            t = min(1.0, max(0.0, 0.82 * u + 0.18 * v))
            r = GRAD_L[0] + (GRAD_R[0] - GRAD_L[0]) * t
            gg = GRAD_L[1] + (GRAD_R[1] - GRAD_L[1]) * t
            b = GRAD_L[2] + (GRAD_R[2] - GRAD_L[2]) * t
            # 交叉高光：以符号中心为中心的椭圆衰减
            d = ((u - 0.5) / 0.16) ** 2 + ((v - 0.5) / 0.30) ** 2
            k = max(0.0, 1.0 - d) * 0.55
            if k > 0:
                r += (CROSS_HL[0] - r) * k
                gg += (CROSS_HL[1] - gg) * k
                b += (CROSS_HL[2] - b) * k
            px[x, y] = (int(r), int(gg), int(b))
    return g.resize((gw, gh), Image.LANCZOS)


def build_master(mask_hi: Image.Image) -> Image.Image:
    """按参考图比例生成 256 母版：黑色超椭圆磁贴 + 青蓝渐变 ∞。"""
    side = MASTER
    tile = int(round(side * TILE_RATIO))
    pad = (side - tile) // 2

    out = Image.new("RGBA", (side, side), (0, 0, 0, 0))

    # 磁贴底：近黑 + 极淡的对角高光，避免死黑一块（参考图左上略亮）
    plate = Image.new("RGBA", (tile, tile), TILE_DARK + (255,))
    n = tile * 2
    hl = Image.new("L", (n, n), 0)
    hp = hl.load()
    for y in range(0, n, 2):
        for x in range(0, n, 2):
            d = ((x / n) ** 2 + (y / n) ** 2) ** 0.5 / 1.15
            val = max(0.0, 1.0 - d) ** 2
            v = int(26 * val)
            for dy in range(2):
                for dx in range(2):
                    if x + dx < n and y + dy < n:
                        hp[x + dx, y + dy] = v
    hl = hl.resize((tile, tile), Image.LANCZOS)
    light = Image.new("RGBA", (tile, tile), (120, 170, 255, 0))
    light.putalpha(hl)
    plate.alpha_composite(light)
    plate.putalpha(squircle_mask(tile))

    # 符号：位置与尺寸都按参考图实测比例
    gw = max(6, int(round(tile * GLYPH_W_RATIO)))
    gh = max(4, int(round(gw * mask_hi.size[1] / mask_hi.size[0])))
    m = mask_hi.resize((gw, gh), Image.LANCZOS)
    gx = pad + int(round(tile * GLYPH_DX))
    gy = pad + int(round(tile * GLYPH_DY))

    glyph = Image.new("RGBA", (gw, gh), (0, 0, 0, 0))
    glyph.paste(glyph_gradient(gw, gh), (0, 0), m)

    out.alpha_composite(plate, (pad, pad))
    out.alpha_composite(glyph, (gx, gy))
    return out


# ---------------------------------------------------------------- ICO 写出
def _bmp_frame(im: Image.Image) -> bytes:
    """32 位 BMP(DIB) 帧：ICON 里的 DIB 高度要写成 2 倍（XOR 图 + AND 掩码）。"""
    w, h = im.size
    src = im.convert("RGBA").tobytes()
    rows = []
    for y in range(h - 1, -1, -1):                      # DIB 是自下而上
        row = bytearray()
        base = y * w * 4
        for x in range(w):
            i = base + x * 4
            row += bytes((src[i + 2], src[i + 1], src[i], src[i + 3]))   # BGRA
        rows.append(bytes(row))
    xor = b"".join(rows)
    stride = ((w + 31) // 32) * 4                       # AND 掩码行按 4 字节对齐
    and_mask = b"\x00" * (stride * h)                   # 全 0：透明度交给 alpha 通道
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0,
                         len(xor) + len(and_mask), 0, 0, 0, 0)
    return header + xor + and_mask


def _png_frame(im: Image.Image) -> bytes:
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(frames, path: str) -> None:
    """手写 ICO。

    PIL 的 ICO 保存会自己按 sizes 缩放基准图，无法保证把**我们逐尺寸精修过的帧**
    原样写进去；布局取最保守的一种：≤64 用 BMP(DIB)、≥96 用 PNG（Vista 起支持，
    Win7 没问题）。
    """
    entries, blobs = [], []
    offset = 6 + 16 * len(frames)
    for im in sorted(frames, key=lambda f: f.size[0]):
        s = im.size[0]
        data = _png_frame(im) if s >= 96 else _bmp_frame(im)
        entries.append(struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s,
                                   0, 0, 1, 32, len(data), offset))
        blobs.append(data)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(frames)))
        for e in entries:
            f.write(e)
        for b in blobs:
            f.write(b)


def preview(ico_path: str, out_png: str) -> None:
    """各尺寸横向拼一张对比条：小尺寸糊不糊、有没有被裁，一眼可见。"""
    src = Image.open(ico_path)
    sizes = sorted(src.ico.sizes())
    pad, gap = 14, 16
    tall = max(h for _, h in sizes)
    width = pad * 2 + sum(w for w, _ in sizes) + gap * (len(sizes) - 1)
    strip = Image.new("RGB", (width, tall + pad * 2 + 18), (243, 243, 243))
    d = ImageDraw.Draw(strip)
    x = pad
    for (w, h) in sizes:
        frame = src.ico.getimage((w, h)).convert("RGBA")
        strip.paste(frame, (x, pad + (tall - h)), frame)
        d.text((x, tall + pad + 3), str(w), fill=(70, 70, 70))
        x += w + gap
    strip.save(out_png)
    print("->", out_png, strip.size)


def build(src: str, outdir: str) -> Image.Image:
    mask_hi, _ = extract_glyph_mask(Image.open(src))

    os.makedirs(outdir, exist_ok=True)
    master = build_master(mask_hi)
    frames = []
    for s in SIZES:
        im = master.resize((s, s), Image.LANCZOS)
        # 重采样会把超椭圆边缘磨虚，按原尺寸再裁一次，轮廓依旧利落
        tile = max(2, int(round(s * TILE_RATIO)))
        pad = (s - tile) // 2
        crop = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        m = squircle_mask(tile)
        crop.paste(im.crop((pad, pad, pad + tile, pad + tile)), (pad, pad), m)
        frames.append(crop)

    ico_path = os.path.join(outdir, "app.ico")
    write_ico(frames, ico_path)
    print("->", ico_path, os.path.getsize(ico_path), "bytes", f"({len(frames)} 帧)")

    png_path = os.path.join(outdir, "app_256.png")
    master.save(png_path, format="PNG")
    print("->", png_path, os.path.getsize(png_path), "bytes")

    # 覆盖率自检：与"正常应用图标 ~66%"对齐，低于 50% 说明任务栏里会显小
    for s in (16, 24, 32, 48):
        f = frames[[x.size[0] for x in frames].index(s)]
        a = f.getchannel("A")
        cover = sum(1 for v in a.getdata() if v > 16) / float(s * s)
        print(f"   {s}px 墨迹覆盖率 {cover:.0%}")
    return master


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(2)
    src = sys.argv[1]
    rest = [a for a in sys.argv[2:] if not a.startswith("--")]
    out = rest[0] if rest else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "MouseBridge", "Assets")
    build(src, out)
    if "--preview" in sys.argv:
        preview(os.path.join(out, "app.ico"),
                os.path.join(os.path.dirname(os.path.abspath(__file__)), "_icon_preview.png"))
