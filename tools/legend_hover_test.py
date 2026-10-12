"""
指示器页"颜色图例悬停联动"的端到端验证。

要验证的行为（用户需求）：
    鼠标指到右侧某个颜色项上 → 左侧预览框里立刻演示那个状态（光环换色）；
    移开 → 恢复成实时状态。

做法：
    1. 打开设置页的「指示器」标签；
    2. 在截图里定位深色预览框（#22272E）和紫色图例点（#A855F7，全图唯一）；
    3. 按预览框几何算出光环圆心，采样圆环顶部像素作为"当前环色"；
    4. 把光标移到紫色图例项上 → 环应当变紫；移开 → 环应当回到实时色（绿）。

用法：python tools/legend_hover_test.py
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shots  # noqa: E402
from PIL import Image, ImageGrab  # noqa: E402

PANEL = (0x22, 0x27, 0x2E)      # 预览框底色
PURPLE = (0xA8, 0x55, 0xF7)     # 紫：拖拽中
GREEN = (0x2E, 0xCC, 0x71)      # 绿：控制本机（实时色）
CURSOR = (1300, 400)            # 停泊点：必须在设置窗口之外


def kill_old():
    """结束所有遗留的 MouseBridge 进程（老打包版进程名不同，按名字各杀一遍）。"""
    for name in ("MouseBridge.exe", "MouseBridge-1.4.0-win-x64.exe"):
        subprocess.run(["taskkill", "/IM", name, "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    time.sleep(1)


def near(a, b, tol=26):
    return all(abs(x - y) <= tol for x, y in zip(a[:3], b[:3]))


def find_color(im, rgb, tol=18):
    px = im.load()
    w, h = im.size
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            if near(px[x, y], rgb, tol):
                return x, y
    return None


def panel_box(im):
    px = im.load()
    w, h = im.size
    xs, ys = [], []
    for y in range(0, h, 3):
        for x in range(0, w, 3):
            if near(px[x, y], PANEL, 6):
                xs.append(x)
                ys.append(y)
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def ring_color(im, bbox):
    """在光环外接框（外扩几像素）里挑"最饱和"的那个像素作为环色。
    直接取固定坐标会被描边抗锯齿干扰（边缘像素是半透明的混色），
    找最饱和像素则稳定命中描边本体。"""
    x0, y0, x1, y1 = bbox
    px = im.load()
    w, h = im.size
    best, bestv = (0, 0, 0), -1
    for y in range(max(0, y0 - 5), min(h, y1 + 6)):
        for x in range(max(0, x0 - 5), min(w, x1 + 6)):
            c = px[x, y]
            v = max(c) - min(c)
            if v > bestv:
                bestv, best = v, c
    return best


def count_color(im, bbox, rgb, tol=44):
    """统计外接框里命中某颜色的像素数。
    预览里同时有"环本体"和一条旋转的青色进度弧，两者都在同一圈上；
    所以不看"最饱和像素"，而是数各颜色的像素个数——环换成紫色时紫色像素必然从 0 变正。"""
    x0, y0, x1, y1 = bbox
    px = im.load()
    w, h = im.size
    n = 0
    for y in range(max(0, y0 - 5), min(h, y1 + 6)):
        for x in range(max(0, x0 - 5), min(w, x1 + 6)):
            if near(px[x, y], rgb, tol):
                n += 1
    return n


def main():
    kill_old()
    shots.wake_display()
    exe = shots.NEW_EXE
    p = subprocess.Popen([exe, "--settings", "--tab", "3"], cwd=os.path.dirname(exe))
    try:
        hwnd = shots.wait_window("鼠下快传 MouseBridge", 20)
        if not hwnd:
            print("[FAIL] 没等到设置窗口")
            return 1
        shots.focus(hwnd)
        shots.park_cursor()
        time.sleep(1.0)

        r = wt.RECT()
        shots.u32.GetWindowRect(hwnd, ctypes.byref(r))

        def shot(tag):
            im = ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom), all_screens=True).convert("RGB")
            im.save(os.path.join(shots.SHOTS, f"_legend_{tag}.png"))
            return im

        im0 = shot("idle")
        box = panel_box(im0)
        dot = find_color(im0, PURPLE)
        if box is None or dot is None:
            print(f"[FAIL] 定位失败：预览框={box} 紫色图例点={dot}")
            return 1

        # 光环位置直接从图里量：预览框左半区里唯一的绿色圆环就是它（不靠公式推算，
        # 免得受 DPI 缩放/内边距影响）。取它的外接框中心与半径。
        px0, py0, _, py1 = box
        gx, gy = [], []
        px = im0.load()
        for y in range(py0, py1):
            for x in range(px0, px0 + 330):
                if near(px[x, y], GREEN, 40):
                    gx.append(x)
                    gy.append(y)
        if not gx:
            print("[FAIL] 预览框里没找到绿色光环")
            return 1
        ccx = (min(gx) + max(gx)) / 2.0
        rad = (max(gx) - min(gx)) / 2.0
        ring_box = (min(gx), min(gy), max(gx), max(gy))
        print(f"  预览框={box} 光环外接框={ring_box}（光环半径≈{rad:.0f}px，中心 x≈{ccx:.0f}）")

        def sample_series(tag, frames=4, gap=0.3):
            """连续采样几帧，返回 (绿像素数, 紫像素数) 的平均值。
            进度弧是青色且绕着圈转，单帧采样容易只取到弧；多帧+计数更稳。"""
            out = []
            for i in range(frames):
                im = shot(f"{tag}{i}")
                out.append((count_color(im, ring_box, GREEN), count_color(im, ring_box, PURPLE)))
                time.sleep(gap)
            g = sum(a for a, _ in out) / len(out)
            p = sum(b for _, b in out) / len(out)
            return g, p

        g0, p0 = sample_series("idle")
        ok_idle = g0 > 8 and p0 == 0
        print(f"  未悬停：绿像素≈{g0:.0f} 紫像素≈{p0:.0f} → {'绿（实时）且无紫' if ok_idle else '非预期'}")

        # 悬停到紫色图例项（点右侧的标签文字上）
        shots.u32.SetCursorPos(r.left + dot[0] + 26, r.top + dot[1])
        time.sleep(0.9)
        g1, p1 = sample_series("hover")
        ok_hover = p1 > 8
        print(f"  悬停紫项：绿像素≈{g1:.0f} 紫像素≈{p1:.0f} → {'紫（已联动）' if ok_hover else '未联动'}")

        # 移开 → 应当恢复
        shots.park_cursor()
        time.sleep(1.0)
        g2, p2 = sample_series("leave")
        ok_back = g2 > 8 and p2 == 0
        print(f"  移开后：绿像素≈{g2:.0f} 紫像素≈{p2:.0f} → {'已恢复（绿、无紫）' if ok_back else '未恢复'}")

        ok = ok_idle and ok_hover and ok_back
        print("[PASS] 图例悬停联动" if ok else "[FAIL] 图例悬停联动")
        return 0 if ok else 1
    finally:
        try:
            p.terminate()
        except Exception:
            pass
        kill_old()


if __name__ == "__main__":
    sys.exit(main())
