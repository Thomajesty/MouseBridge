"""
把 exe/ico 里的图标按各档尺寸渲染成一张对比图，用于人工核对图标质量。

为什么需要它：任务栏/资源管理器里图标到底是"太小""发虚"还是"没内嵌"，
光看代码看不出来，必须把 exe 真正内嵌的那份图标取出来看。

实现要点（踩过的坑）：
    必须用 CreateDIBSection 建 32 位 DIB 再 DrawIconEx ——
    用 CreateCompatibleBitmap 建的是没有 alpha 平面的兼容位图，
    GetDIBits 读回来的 alpha 全是 0，画出来是一张近乎空白的图
    （连 notepad.exe 都会"渲染成空"，很容易误判成"exe 没图标"）。

用法：
    python tools/exe_icon_dump.py                       # 默认看两个交付包
    python tools/exe_icon_dump.py <exe或ico> [...]      # 指定文件
"""
import ctypes
import os
import sys
from ctypes import wintypes as wt

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SHOTS = os.path.join(ROOT, "shots")

sh = ctypes.windll.shell32
u32 = ctypes.windll.user32
gdi = ctypes.windll.gdi32

sh.ExtractIconExW.argtypes = [wt.LPCWSTR, ctypes.c_int,
                              ctypes.POINTER(wt.HICON), ctypes.POINTER(wt.HICON), wt.UINT]
sh.ExtractIconExW.restype = wt.UINT
u32.DrawIconEx.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int, wt.HICON,
                           ctypes.c_int, ctypes.c_int, wt.UINT, wt.HANDLE, wt.UINT]
gdi.CreateDIBSection.argtypes = [wt.HDC, ctypes.c_void_p, wt.UINT,
                                 ctypes.POINTER(ctypes.c_void_p), wt.HANDLE, wt.UINT]
gdi.CreateDIBSection.restype = wt.HBITMAP
# 句柄类参数必须声明 argtypes：不声明时 ctypes 按 C int 传参，
# 句柄值一旦超过 2^31 就会抛 "int too long to convert"（第一次没声明时正好撞上）。
gdi.SelectObject.argtypes = [wt.HDC, wt.HGDIOBJ]
gdi.SelectObject.restype = wt.HGDIOBJ
gdi.DeleteObject.argtypes = [wt.HGDIOBJ]
gdi.DeleteDC.argtypes = [wt.HDC]
gdi.CreateCompatibleDC.argtypes = [wt.HDC]
gdi.CreateCompatibleDC.restype = wt.HDC
gdi.CreateSolidBrush.argtypes = [wt.COLORREF]
gdi.CreateSolidBrush.restype = wt.HBRUSH
u32.FillRect.argtypes = [wt.HDC, ctypes.c_void_p, wt.HBRUSH]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                ("biClrImportant", ctypes.c_uint32)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", ctypes.c_uint32 * 3)]


def render_hicon(h, size, bg=(245, 245, 245)):
    """把 HICON 按 size 渲染到带 alpha 的 DIB，返回 RGBA 图。"""
    hdc = gdi.CreateCompatibleDC(0)
    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = size
    bmi.bmiHeader.biHeight = -size          # 负高度 = 自上而下
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0         # BI_RGB
    bits = ctypes.c_void_p()
    hbm = gdi.CreateDIBSection(hdc, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    gdi.SelectObject(hdc, hbm)
    # 先用背景色铺满（DIB 初始是全 0，透明区域会显成纯黑，不好看）
    ctypes.memset(bits, 0, size * size * 4)
    brush = gdi.CreateSolidBrush(bg[0] | (bg[1] << 8) | (bg[2] << 16))
    rect = wt.RECT(0, 0, size, size)
    u32.FillRect(hdc, ctypes.byref(rect), brush)
    gdi.DeleteObject(brush)
    u32.DrawIconEx(hdc, 0, 0, h, size, size, 0, None, 3)   # 3 = DI_NORMAL
    raw = ctypes.string_at(bits, size * size * 4)
    im = Image.frombuffer("RGBA", (size, size), raw, "raw", "BGRA", 0, 1)
    gdi.DeleteObject(hbm)
    gdi.DeleteDC(hdc)
    return im.copy()


def icon_frames(path, sizes=(16, 20, 24, 32, 48, 64)):
    """取 exe 内嵌图标（大图标档）渲染成各尺寸。"""
    if path.lower().endswith(".ico"):
        src = Image.open(path)
        return {s: src.ico.getimage((s, s)).convert("RGBA") for s in sizes if (s, s) in src.ico.sizes()}
    lg = wt.HICON()
    sm = wt.HICON()
    n = sh.ExtractIconExW(os.path.abspath(path), 0, ctypes.byref(lg), ctypes.byref(sm), 1)
    if n == 0 or not lg.value:
        return {}
    out = {s: render_hicon(lg, s) for s in sizes}
    u32.DestroyIcon(lg)
    if sm.value:
        u32.DestroyIcon(sm)
    return out


def ink_ratio(im):
    a = im.getchannel("A")
    tot = im.size[0] * im.size[1]
    return sum(1 for p in a.getdata() if p > 16) / tot


def main():
    targets = sys.argv[1:]
    if not targets:
        targets = [os.path.join(ROOT, "dist", "MouseBridge-1.6.0-win-x64.exe"),
                   os.path.join(ROOT, "dist", "MouseBridge-1.6.0-net8-x64.exe"),
                   os.path.join(ROOT, "MouseBridge", "Assets", "app.ico")]
    # 对照：系统记事本，用来说明"这张对比图是可信的"
    control = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "System32", "notepad.exe")
    if os.path.isfile(control):
        targets.append(control)

    rows = []
    for t in targets:
        if not os.path.isfile(t):
            print(f"[跳过] 不存在：{t}")
            continue
        frames = icon_frames(t)
        label = os.path.basename(t)
        if not frames:
            print(f"[警告] {label} 没有内嵌图标")
        rows.append((label, frames))
        detail = "  ".join(f"{s}px:{ink_ratio(frames[s]) * 100:.0f}%" for s in sorted(frames))
        print(f"{label}: {detail}")

    if not rows:
        return 1
    sizes = [16, 20, 24, 32, 48, 64]
    rowh, pad, hdr = 72, 14, 24
    col = [pad]
    for s in sizes:
        col.append(col[-1] + s + 26)
    width = col[-1] + pad
    height = hdr + pad + len(rows) * (rowh + 8)
    strip = Image.new("RGB", (width, height), (245, 245, 245))
    d = ImageDraw.Draw(strip)
    for i, s in enumerate(sizes):
        d.text((col[i], 4), f"{s}px", fill=(70, 70, 70))
    for r, (label, frames) in enumerate(rows):
        y = hdr + pad + r * (rowh + 8)
        d.text((pad, y + rowh + 2), label, fill=(40, 40, 40))
        for i, s in enumerate(sizes):
            if s in frames:
                strip.paste(frames[s], (col[i], y + (rowh - s) // 2), frames[s])
    out = os.path.join(SHOTS, "_icon_compare.png")
    strip.save(out)
    print("->", out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
