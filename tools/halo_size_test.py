# -*- coding: utf-8 -*-
"""验证"设置页光环大小滑块 → 运行中的光环"链路是否真的生效。

链路：Slider.ValueChanged → Orchestrator.SetIndicatorLive → IndicatorWindow.Configure
      → ApplyGeometry（改 _ringSize / _box）

做法：
  1. 启动新版 exe 直达"指示器"页（带 MB_HALO_PROBE=1，程序把几何写进 %TEMP%\\mb-halo-geom.txt）；
  2. 用 UI Automation（comtypes 调 UIAutomationCore）把 SlIndicatorSize 设成 24 / 96 / 44；
  3. 读几何文件确认 ring 值跟着变；
  4. 同时扫光标所在水平线，量出光圈实际外径，双重验证。
"""
import ctypes
import os
import subprocess
import sys
import time

from PIL import Image, ImageGrab

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
EXE = os.path.join(ROOT, "MouseBridge", "bin", "Release", "net8.0-windows", "MouseBridge.exe")
GEOM = os.path.join(os.environ.get("TEMP", "."), "mb-halo-geom.txt")
RING_RGB = (154, 163, 175)          # 未连接态光圈颜色 #9AA3AF

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass
u32.SetProcessDPIAware()

CURSOR = (960, 660)                 # 停在设置窗口内的纯色区域
CROP = 320


def kill():
    subprocess.run(["taskkill", "/IM", "MouseBridge.exe", "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.0)


def wait_window(title, timeout=25):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if u32.FindWindowW(None, title):
            return True
        time.sleep(0.3)
    return False


# ---------------- UI Automation ----------------

class Uia:
    def __init__(self):
        import comtypes.client
        comtypes.client.GetModule("UIAutomationCore.dll")
        from comtypes.gen import UIAutomationClient as UIA
        self.UIA = UIA
        self.uia = comtypes.client.CreateObject(UIA.CUIAutomation, interface=UIA.IUIAutomation)

    def _children(self, el):
        a = el.FindAll(self.UIA.TreeScope_Children, self.uia.CreateTrueCondition())
        return [a.GetElement(i) for i in range(a.Length)]

    def _descendants(self, el):
        a = el.FindAll(self.UIA.TreeScope_Descendants, self.uia.CreateTrueCondition())
        return [a.GetElement(i) for i in range(a.Length)]

    def find_window(self, keyword):
        for e in self._children(self.uia.GetRootElement()):
            if keyword in (e.CurrentName or ""):
                return e
        return None

    def find_by_id(self, root, aid):
        for e in self._descendants(root):
            try:
                if e.CurrentAutomationId == aid:
                    return e
            except Exception:
                pass
        return None

    def set_range(self, el, value):
        unk = el.GetCurrentPattern(self.UIA.UIA_RangeValuePatternId)
        rv = unk.QueryInterface(self.UIA.IUIAutomationRangeValuePattern)
        rv.SetValue(float(value))
        time.sleep(0.4)
        return rv.CurrentValue


def geom_last():
    try:
        with open(GEOM, encoding="utf-8", errors="ignore") as f:
            lines = [l for l in f.read().splitlines() if l.strip()]
        return lines[-1] if lines else ""
    except OSError:
        return ""


def measure_ring(path):
    """沿光标所在水平线量光圈外径（像素）。"""
    u32.SetCursorPos(*CURSOR)
    time.sleep(0.7)
    cx, cy = CURSOR
    im = ImageGrab.grab(bbox=(cx - CROP // 2, cy - CROP // 2, cx + CROP // 2, cy + CROP // 2),
                        all_screens=True).convert("RGB")
    im.save(path)
    px = im.load()
    w, h = im.size
    midx, midy = w // 2, h // 2
    hits = []
    for dy in (-1, 0, 1, -2, 2):
        y = midy + dy
        if not (0 <= y < h):
            continue
        for x in range(max(0, midx - 130), min(w, midx + 130)):
            r, g, b = px[x, y]
            if abs(r - RING_RGB[0]) <= 12 and abs(g - RING_RGB[1]) <= 12 and abs(b - RING_RGB[2]) <= 12:
                hits.append((x, y))
    if not hits:
        return 0, 0
    xs = [p[0] for p in hits]
    return max(xs) - min(xs) + 1, len(hits)


def main():
    kill()
    try:
        os.remove(GEOM)
    except OSError:
        pass

    env = dict(os.environ)
    env["MB_HALO_PROBE"] = "1"
    p = subprocess.Popen([EXE, "--settings", "--tab", "3"], cwd=os.path.dirname(EXE), env=env)
    if not wait_window("鼠下快传 MouseBridge"):
        print("!! 设置窗口没起来")
        p.terminate()
        return 1
    print("设置窗口已就绪（指示器页）")
    time.sleep(3.6)                    # 等状态文字淡出，只留光圈

    try:
        u = Uia()
        win = u.find_window("MouseBridge")
        print("找到窗口:", bool(win))
        slider = u.find_by_id(win, "SlIndicatorSize") if win else None
        print("找到滑块:", bool(slider))
        if not slider:
            p.terminate()
            return 1
    except Exception as ex:
        print("!! UI Automation 初始化失败:", type(ex).__name__, ex)
        p.terminate()
        return 1

    results = {}
    for v in (24, 96, 44):
        try:
            now = u.set_range(slider, v)
        except Exception as ex:
            now = f"ERR {ex}"
        time.sleep(1.0)
        g = geom_last()
        d, n = measure_ring(os.path.join(ROOT, "shots", f"_halo_size_{v}.png"))
        print(f"   滑块={v:>3}  UIA回读={now}  几何=\"{g}\"  实测外径={d}px({n} 命中)")
        results[v] = (str(now), g, d)

    p.terminate()
    kill()

    print("\n===== 判定（几何 ring 值应等于滑块值；实测外径 ≈ ring + 线宽×2）=====")
    okall = True
    for v in (24, 96, 44):
        now, g, d = results[v]
        hit = f"ring={v} " in g
        near = d > 0 and abs(d - (v + 8)) <= 14
        good = hit and near and now.strip() in (str(float(v)), str(v))
        okall &= good
        print(f"  size={v:>3}  几何命中={hit}  实测={d}px  UIA回读={now}  {'OK' if good else 'FAIL'}")
    print("结果:", "全部通过" if okall else "存在问题")
    return 0 if okall else 1


if __name__ == "__main__":
    sys.exit(main())
