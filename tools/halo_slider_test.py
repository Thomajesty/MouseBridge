# -*- coding: utf-8 -*-
"""端到端验证"设置页光环大小滑块"是否真的能改变运行中的光环。

链路：真实拖动滑块 → Slider.ValueChanged → Orchestrator.SetIndicatorLive
      → IndicatorWindow.Configure → ApplyGeometry（改 _ringSize / _box）

做法：
  1. 以 MB_HALO_PROBE=1 启动 exe 直达"指示器"页（程序把几何写进 %TEMP%\\mb-halo-geom.txt）；
  2. 截图定位滑块（用强调色的"已填充轨道"锁定滑块位置），用真实鼠标把滑块拖到左/右/中；
  3. 每次核对几何文件里的 ring 值，并把光标停到纯色区量出光圈实际外径。
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

from PIL import Image, ImageGrab

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
EXE = os.path.join(ROOT, "MouseBridge", "bin", "Release", "net8.0-windows", "MouseBridge.exe")
TITLE = "鼠下快传 MouseBridge"
GEOM = os.path.join(os.environ.get("TEMP", "."), "mb-halo-geom.txt")
RING_RGB = (154, 163, 175)          # 未连接态光圈 #9AA3AF
TRACK_RGB = (200, 200, 200)         # 滑块轨道灰 #C8C8C8（Theme.xaml TrackBrush）
ACCENT = (0, 103, 192)              # 强调色 #0067C0

u32 = ctypes.windll.user32
# 必须声明 argtypes：不声明时 ctypes 按 32 位 int 传参，HWND_TOPMOST(-1) 会被零扩展成
# 0x00000000FFFFFFFF（不是合法 HWND），SetWindowPos 返回 0、窗口不置顶，
# 截出来的窗口区域其实是被 IDE 盖住的画面，压根找不到强调色轨道。
u32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, ctypes.c_uint]
u32.SetWindowPos.restype = wt.BOOL
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass
u32.SetProcessDPIAware()

HWND_TOPMOST, HWND_NOTOPMOST = -1, -2

MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0002, 0x0004
CURSOR = (1300, 400)   # 必须在设置窗口（40,30 起 1000×875）外，否则光圈被设置窗口盖住
CROP = 320


def kill():
    subprocess.run(["taskkill", "/IM", "MouseBridge.exe", "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.0)


def wait_window(title, timeout=25):
    t0 = time.time()
    while time.time() - t0 < timeout:
        h = u32.FindWindowW(None, title)
        if h:
            return h
        time.sleep(0.3)
    return 0


def rect_of(hwnd):
    r = wt.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def drag(x0, y0, x1, y1, steps=16):
    u32.SetCursorPos(int(x0), int(y0))
    time.sleep(0.25)
    u32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.12)
    for i in range(1, steps + 1):
        u32.SetCursorPos(int(x0 + (x1 - x0) * i / steps), int(y0 + (y1 - y0) * i / steps))
        time.sleep(0.035)
    time.sleep(0.25)
    u32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.45)


def geom_lines():
    try:
        with open(GEOM, encoding="utf-8", errors="ignore") as f:
            return [l for l in f.read().splitlines() if l.strip()]
    except OSError:
        return []


def geom_last():
    ls = geom_lines()
    return ls[-1] if ls else ""


def brightness_scale(im):
    """估计"白"在这张截图里实际被渲染成多少，返回 0~1 的缩放系数。

    桌面上残留排队弹窗（安全中心 / 防火墙授权框）会把整屏压暗到 55%
    （白 255→140、轨道灰 200→110），此时按原始色值判据会一个都匹配不上。
    取窗口内亮度 90 分位当作"白"的实测值来反推缩放系数。
    """
    small = im.resize((64, 64))
    lums = sorted(0.299 * r + 0.587 * g + 0.114 * b
                  for r, g, b in small.getdata())
    white = lums[int(len(lums) * 0.9)]
    return min(1.0, max(0.35, white / 255.0))


MIN_RUN = 60               # 轨道可能被滑块拇指切成两段，所以单段门槛不能太高


def _is_track(r, g, b, s):
    """滑块轨道灰 #C8C8C8：中性灰（r≈g≈b）且贴近 200。
    容差收紧到 ±8，避免把按钮描边 #D6D6D6（差 14）混进来。"""
    t = TRACK_RGB[0] * s
    tol = 8 * s + 4
    return (abs(r - t) <= tol and abs(g - t) <= tol and abs(b - t) <= tol
            and max(r, g, b) - min(r, g, b) <= 8)


def _is_accent(r, g, b, s):
    a = ACCENT
    return (abs(r - a[0] * s) <= 55 * s + 20 and abs(g - a[1] * s) <= 45 * s + 18
            and abs(b - a[2] * s) <= 35 * s + 18 and b - r > 60 * s)


def find_slider(bbox):
    """在设置窗口截图里定位滑块轨道。

    轨道 = 一行里的「灰色空段 + 蓝色填充段」，中间被白色拇指挡住会断开，所以两段
    分开找、各自 ≥MIN_RUN 即可。只找强调色会误判——状态栏的 PbBusy 不确定进度条
    （66×4、同为强调色）会被当成滑块轨道，拖它当然不生效。

    返回 (截图, thumb_x, track_y, track_x0, track_x1, 尺寸)；thumb_x = 拇指中心。
    """
    im = ImageGrab.grab(bbox=bbox, all_screens=True).convert("RGB")
    s = brightness_scale(im)
    px = im.load()
    w, h = im.size
    # 状态栏（顶部约 100px）里的 PbBusy 不确定进度条同样是"细长蓝条"，
    # 拖它当然不生效。指示器页的滑块一定在这以下，直接从 120px 起扫。
    y_start = 120
    # 逐行收集"轨道像素"（灰或蓝）的连续段
    rows = {}
    for y in range(y_start, h):
        runs, start, kind = [], 0, None
        for x in range(w):
            p = px[x, y]
            k = "g" if _is_track(*p, s) else ("a" if _is_accent(*p, s) else None)
            if k != kind:
                if kind is not None and x - start >= MIN_RUN:
                    runs.append((start, x - start, kind))
                start, kind = x, k
        if kind is not None and w - start >= MIN_RUN:
            runs.append((start, w - start, kind))
        if runs:
            rows[y] = runs

    ys = sorted(rows)
    groups, cur = [], [ys[0]] if ys else []
    for y in ys[1:]:
        if y - cur[-1] <= 2:
            cur.append(y)
        else:
            groups.append(cur)
            cur = [y]
    if cur:
        groups.append(cur)

    for g in groups:
        if not (2 <= len(g) <= 10):
            continue
        # 合并该行组内所有段，取整体左右边界
        segs = [seg for y in g for seg in rows[y]]
        x0 = min(seg[0] for seg in segs)
        x1 = max(seg[0] + seg[1] for seg in segs)
        # 指示器页的滑块轨道实测 103~315（212px）；PbBusy 进度条只有 66px，
        # 复选框/按钮边框更短。用 150px 门槛把后者挡在外面。
        if x1 - x0 < 150:
            continue
        kinds = {seg[2] for seg in segs}
        # 轨道至少要有强调色填充段；纯灰（值为最小）时靠"灰段够长"兜底
        if "a" not in kinds and x1 - x0 < 150:
            continue
        # 拇指中心 = 填充段末端 + 半个拇指；没有填充段（值最小）时用左边界起算
        accent_max = max((seg[0] + seg[1] for seg in segs if seg[2] == "a"), default=-1)
        thumb_x = accent_max + 8 if accent_max >= 0 else x0 + 8
        # 轨道右端要用"填充段末端 + 拇指半宽 + 灰色剩余"，否则值到最大时右端会缩水，
        # 下一轮算出的目标点就偏小、拖不到位。
        gray_end = max((seg[0] + seg[1] for seg in segs if seg[2] == "g"), default=-1)
        track_right = max(x1, gray_end if gray_end > 0 else x1)
        if "g" in kinds and gray_end > accent_max:
            track_right = gray_end
        return im, thumb_x, (g[0] + g[-1]) // 2, x0, track_right, im.size
    return None


def measure_ring(path):
    """把光标停到固定点，量出光圈外径。

    松开左键后指示器可能有一瞬间不在屏上（拖拽态切换 / 状态刷新），所以这里重试几次，
    拿到第一个真实量到的外径就返回；全都没量到再把最后一帧存下来便于排查。
    """
    u32.SetCursorPos(*CURSOR)
    cx, cy = CURSOR
    last = None
    for attempt in range(6):
        time.sleep(0.8 if attempt == 0 else 0.5)
        im = ImageGrab.grab(bbox=(cx - CROP // 2, cy - CROP // 2, cx + CROP // 2, cy + CROP // 2),
                            all_screens=True).convert("RGB")
        last = im
        px = im.load()
        w, h = im.size
        sc = brightness_scale(im)
        rt, gt, bt = (c * sc for c in RING_RGB)
        tol = 6 * sc + 4
        midx, midy = w // 2, h // 2
        # 光圈挂在光标右侧、中心比光标热点低 ~8px（见 IndicatorWindow.CursorGapX/CursorDropY），
        # 所以在竖直方向扫一整段，取"宽度最大"的那一行作为外径（即过圈心的弦）
        best_w = 0
        for dy in range(-48, 49):
            y = midy + dy
            if not (0 <= y < h):
                continue
            hits = []
            for x in range(max(0, midx - 130), min(w, midx + 130)):
                r, g, b = px[x, y]
                # 光圈 #9AA3AF 是"偏蓝的灰"：既要蓝偏移、又要贴近该色，
                # 免得中性灰文字抗锯齿、图例色点的边缘混进来
                if (b - r) >= 15 * sc and abs(r - rt) <= tol and abs(g - gt) <= tol and abs(b - bt) <= tol:
                    hits.append(x)
            if hits:
                best_w = max(best_w, max(hits) - min(hits) + 1)
        if best_w:
            im.save(path)
            return best_w
    if last is not None:
        last.save(path)
    return 0


def main():
    kill()
    # 显示器 DPMS 休眠时 ImageGrab 会拿到休眠前的陈旧帧，滑块定位必失败——先唤醒
    u32.mouse_event(0x0001, 0, 1, 0, 0)
    time.sleep(0.2)
    u32.mouse_event(0x0001, 1, 0, 0, 0)
    time.sleep(2.5)
    try:
        os.remove(GEOM)
    except OSError:
        pass

    env = dict(os.environ)
    env["MB_HALO_PROBE"] = "1"
    # 独立数据目录：避免读到真实配置里保存的对端地址——那样启动即自动回连，
    # 指示器进入"连接中"动画态（光圈不是灰色 #9AA3AF），实测外径必然对不上。
    data_dir = os.path.join(os.environ.get("TEMP", "."), "mb-halo-test-data")
    os.makedirs(data_dir, exist_ok=True)
    env["MB_DATA_DIR"] = data_dir
    p = subprocess.Popen([EXE, "--settings", "--tab", "3"], cwd=os.path.dirname(EXE), env=env)
    h = wait_window(TITLE)
    if not h:
        print("!! 设置窗口没起来")
        p.terminate()
        return 1
    time.sleep(3.5)
    # 真置顶（HWND_TOPMOST 需要 argtypes 才生效，见文件开头），否则截图区域会被 IDE 盖住
    u32.SetWindowPos(h, HWND_TOPMOST, 40, 30, 0, 0, 0x0001)
    u32.SetForegroundWindow(h)
    time.sleep(1.0)

    l, t, r, b = rect_of(h)
    bbox = (l, t, r, b)
    found = find_slider(bbox)
    if not found:
        print("!! 没定位到滑块")
        shot = ImageGrab.grab(bbox=bbox, all_screens=True).convert("RGB")
        fp = os.path.join(ROOT, "shots", "_slider_fail.png")
        shot.save(fp)
        print(f"   已存失败截图 {fp}，亮度系数 {brightness_scale(shot):.2f}")
        p.terminate()
        kill()
        return 1
    shot, thumb_x, track_y, track_x0, track_x1, (sw, sh) = found
    shot.save(os.path.join(ROOT, "shots", "_slider_locate.png"))
    print(f"窗口 {sw}x{sh}，滑块 thumb 位于窗口内 x={thumb_x}, y={track_y}"
          f"（轨道 {track_x0}~{track_x1}），初始 {geom_last()}")
    u32.SetForegroundWindow(h)
    time.sleep(0.4)

    # 轨道几何全程锁定首次定位值。滑块 Width 固定，轨道位置不会变；而每轮重新
    # 检测出的"右端"会随填充段长度缩水（值到最大时灰段消失，只剩填充末端），
    # 拿它算下一轮目标点就会越拖越偏、停不到端点。
    # 起始值 44 恰好在轨道中间，灰段最长，检测最准。
    base_x0, base_x1 = track_x0, track_x1
    left_x, right_x = l + base_x0 - 90, l + base_x1 + 60
    mid_x = l + (base_x0 + base_x1) // 2
    ty = t + track_y
    cases = [("拖到最右", right_x), ("拖到最左", left_x), ("拖到中间", mid_x)]
    results = []
    for label, tx in cases:
        before = len(geom_lines())
        # 每轮只重新取拇指起点（会随拖动移动），轨道端点用锁定值
        f0 = find_slider(bbox)
        if f0:
            thumb_x = f0[1]
        drag(l + thumb_x, ty, tx, ty)
        time.sleep(0.8)
        g = geom_last()
        d = measure_ring(os.path.join(ROOT, "shots", f"_halo_slider_{label[2:]}.png"))
        ring = int(g.split("ring=")[1].split()[0]) if "ring=" in g else -1
        stroke = float(g.split("stroke=")[1].split()[0]) if "stroke=" in g else 0.0
        print(f"   {label:<8} 几何=\"{g}\"  实测外径={d}px")
        results.append((label, ring, stroke, d, len(geom_lines()) > before))

    p.terminate()
    kill()

    print("\n===== 判定（几何 ring 应随拖动变化；像素外径≈ring+2×线宽，描边整体在椭圆外侧）=====")
    rings = [x[1] for x in results]
    changed = len(set(rings)) >= 2
    print("  拖动后 ring 序列:", rings, "→", "随拖动变化" if changed else "没变化")
    okall = changed
    for label, ring, stroke, d, fired in results:
        expect = ring + stroke * 2
        near = d > 0 and abs(d - expect) <= 9
        good = fired and near
        okall &= good
        print(f"  {label:<8} ring={ring:>3} 期望≈{expect:.0f}px 实测={d}px 吻合={near}  {'OK' if good else 'FAIL'}")
    print("结果:", "全部通过" if okall else "存在问题")
    return 0 if okall else 1


if __name__ == "__main__":
    sys.exit(main())
