# -*- coding: utf-8 -*-
"""MouseBridge 验收截图：设置窗口各页 + 光标光环各状态 + 传输记录/日志窗口。

用法（在项目根目录）：
    python tools/shots.py after      # 新版 build 五个设置页
    python tools/shots.py history    # 传输记录与运行日志窗口
    python tools/shots.py halo       # 光环状态轮播逐帧截图
    python tools/shots.py compose    # 把所有图拼成总览
    python tools/shots.py all

注意：截图前会先「SetWindowPos(HWND_TOP) + BringWindowToTop」把窗口提到最前，
否则抢不过 IDE 前台窗口，只能截到被挡住的一角。
"""
import ctypes
import ctypes.wintypes as wt
import hashlib
import os
import subprocess
import sys
import time

from PIL import Image, ImageDraw, ImageFont, ImageGrab, ImageStat

def _read_ver():
    """从 AppInfo.cs 读版本号，保证总览图标题和实际构建一致。"""
    import re
    try:
        with open(os.path.join(ROOT, "MouseBridge", "Core", "AppInfo.cs"),
                  encoding="utf-8") as fh:
            t = fh.read()
        return re.search(r'Version = "(.*?)"', t).group(1)
    except Exception:
        return "?"

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
APP_VER = _read_ver()
SHOTS = os.path.join(ROOT, "shots")
NEW_EXE = os.path.join(ROOT, "MouseBridge", "bin", "Release", "net8.0-windows", "MouseBridge.exe")
OLD_EXE = os.path.join(ROOT, "dist", "MouseBridge.exe")
CFG = os.path.join(os.environ["APPDATA"], "MouseBridge", "config.json")

u32 = ctypes.windll.user32
# 必须声明 argtypes：不声明时 ctypes 按 32 位 int 传参，HWND_TOPMOST(-1) 会被零扩展成
# 0x00000000FFFFFFFF（不是合法 HWND），SetWindowPos 直接返回 0、窗口纹丝不动，
# 截出来的图就全是被 IDE 盖住的画面。
u32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, ctypes.c_uint]
u32.SetWindowPos.restype = wt.BOOL
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass
u32.SetProcessDPIAware()

SWP_NOSIZE, SWP_NOMOVE, SWP_NOZORDER = 0x0001, 0x0002, 0x0004
HWND_TOPMOST, HWND_NOTOPMOST = -1, -2
VK_CONTROL, VK_TAB, VK_LWIN, VK_D, KEYEVENTF_KEYUP = 0x11, 0x09, 0x5B, 0x44, 0x0002


def find_window(title):
    return u32.FindWindowW(None, title)


def wait_window(title, timeout=25):
    t0 = time.time()
    while time.time() - t0 < timeout:
        h = find_window(title)
        if h:
            return h
        time.sleep(0.3)
    return 0


def rect_of(hwnd):
    r = wt.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def focus(hwnd, x=40, y=30):
    """把窗口挪到左上角并**真置顶**，截完用 unfocus 恢复。

    SetForegroundWindow 抢不过已经激活的 IDE；真正管用的是 HWND_TOPMOST
    （前提是 SetWindowPos 已声明 argtypes，见文件开头）。
    """
    u32.SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE)
    u32.SetForegroundWindow(hwnd)
    time.sleep(0.8)


def unfocus(hwnd):
    u32.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE)


def show_desktop():
    """Win+D：把光环截图背景切成干净的桌面（截完再按一次还原）。"""
    u32.keybd_event(VK_LWIN, 0, 0, 0)
    u32.keybd_event(VK_D, 0, 0, 0)
    time.sleep(0.05)
    u32.keybd_event(VK_D, 0, KEYEVENTF_KEYUP, 0)
    u32.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(1.0)


def send_ctrl_tab():
    u32.keybd_event(VK_CONTROL, 0, 0, 0)
    u32.keybd_event(VK_TAB, 0, 0, 0)
    time.sleep(0.05)
    u32.keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, 0)
    u32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)


MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0002, 0x0004


def click(x, y):
    u32.SetCursorPos(int(x), int(y))
    time.sleep(0.15)
    u32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.05)
    u32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.35)


def grab_rect(bbox, path):
    im = ImageGrab.grab(bbox=bbox, all_screens=True)
    im.save(path)
    return im


def wake_display():
    """显示器 DPMS 休眠时 PrintWindow 全黑（DWM 停合成），而 ImageGrab 会拿到
    休眠前的陈旧帧——看似正常实为废图。先发鼠标移动事件把屏幕唤醒再抓。"""
    u32.mouse_event(0x0001, 0, 1, 0, 0)   # MOUSEEVENTF_MOVE
    time.sleep(0.2)
    u32.mouse_event(0x0001, 1, 0, 0, 0)
    time.sleep(2.5)


def grab_window(hwnd, path):
    """PrintWindow 直接抓窗口自身渲染，**不被任何遮挡窗口影响**。

    桌面上总有别的程序（IDE、资源管理器）在我们 raise 之后又抢回前景，
    屏幕抓图靠竞速不稳；PW_RENDERFULLCONTENT 能抓 WPF(DirectComposition) 的内容。
    唯一失效场景是显示器休眠（DWM 停止合成，抓出来全黑）——此时唤醒显示器重试，
    仍黑才回退屏幕抓取。
    """
    gdi = ctypes.windll.gdi32
    r = wt.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    w, ht = r.right - r.left - 2, r.bottom - r.top - 2

    class BIH(ctypes.Structure):
        _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32),
                    ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                    ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32),
                    ("biSizeImage", ctypes.c_uint32), ("biXPelsPerMeter", ctypes.c_int32),
                    ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32),
                    ("biClrImportant", ctypes.c_uint32)]

    def _printwindow():
        hdc = u32.GetWindowDC(hwnd)
        mem = gdi.CreateCompatibleDC(hdc)
        bmp = gdi.CreateCompatibleBitmap(hdc, w, ht)
        gdi.SelectObject(mem, bmp)
        ok = u32.PrintWindow(hwnd, mem, 2)      # 2 = PW_RENDERFULLCONTENT
        bi = BIH(ctypes.sizeof(BIH), w, -ht, 1, 32, 0, 0, 0, 0, 0, 0)
        buf = ctypes.create_string_buffer(w * ht * 4)
        gdi.GetDIBits(mem, bmp, 0, ht, buf, ctypes.byref(bi), 0)
        im = Image.frombuffer("RGBA", (w, ht), buf.raw, "raw", "BGRA", 0, 1).convert("RGB")
        gdi.DeleteObject(bmp)
        gdi.DeleteDC(mem)
        u32.ReleaseDC(hwnd, hdc)
        return ok, im

    # 真黑屏（显示器休眠 / DWM 未合成）判定要用**均值**：正文里的黑色文字
    # 会让最小值必然为 0，按最小值判断会把正常截图误判成黑屏而回退屏幕抓取。
    for attempt in range(3):
        ok, im = _printwindow()
        mean = sum(ImageStat.Stat(im).mean) / 3
        if ok and mean > 12:
            im.save(path)
            return im
        wake_display()          # 八成是显示器睡着了：唤醒后再来一次
    ImageGrab.grab(bbox=(r.left + 1, r.top + 1, r.right - 1, r.bottom - 1),
                   all_screens=True).save(path)
    return None


def kill_all():
    subprocess.run(["taskkill", "/IM", "MouseBridge.exe", "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.2)


def park_cursor():
    """把光标挪到设置窗口之外，免得光环出现在截图里。"""
    w = u32.GetSystemMetrics(0)
    h = u32.GetSystemMetrics(1)
    u32.SetCursorPos(w - 24, h // 2)
    time.sleep(0.45)


def shoot_settings(exe, outdir, title, force_unconfigured=False, tabs_arg=False):
    """抓设置窗口各页（每页独立启动一次，用 --tab N 精确定位）。"""
    os.makedirs(outdir, exist_ok=True)
    kill_all()
    backup = None
    if force_unconfigured and os.path.exists(CFG):
        backup = CFG + ".shotsbak"
        os.replace(CFG, backup)
    names = ["01_连接", "02_屏幕与切换", "03_剪贴板与文件", "04_指示器", "05_高级", "06_关于"]
    try:
        for i, n in enumerate(names):
            if tabs_arg:
                kill_all()
                argv = [exe, "--settings", "--tab", str(i)]
                p = subprocess.Popen(argv, cwd=os.path.dirname(exe))
                h = wait_window(title)
                if not h:
                    print("!! 没等到窗口:", title)
                    p.terminate()
                    return False
                focus(h)
                park_cursor()
                time.sleep(0.5)
                grab_window(h, os.path.join(outdir, n + ".png"))
                print("  ok", n)
                unfocus(h)
                p.terminate()
                time.sleep(0.8)
            else:
                # 旧版没有 --tab：点一下标签栏拿到焦点，再用 Ctrl+Tab 逐页切换
                if i == 0:
                    p = subprocess.Popen([exe], cwd=os.path.dirname(exe))
                    h = wait_window(title)
                    if not h:
                        print("!! 没等到窗口:", title)
                        p.terminate()
                        return False
                    focus(h)
                bbox = rect_of(h)
                click(bbox[0] + 45, bbox[1] + 67)   # 落在第一个标签上，顺便聚焦 TabControl
                park_cursor()
                grab_window(h, os.path.join(outdir, n + ".png"))
                print("  ok", n)
                if i < len(names) - 1:
                    u32.SetForegroundWindow(h)
                    time.sleep(0.3)
                    send_ctrl_tab()
                    time.sleep(1.0)
        if not tabs_arg:
            p.terminate()
            time.sleep(0.8)
        return True
    finally:
        kill_all()
        if backup:
            os.replace(backup, CFG)


def shoot_halo(outdir, wait=22):
    """光环轮播逐帧截图：程序把当前帧号写进 %TEMP%\\mb-halo-frame.txt，据此精确同步。"""
    os.makedirs(outdir, exist_ok=True)
    kill_all()
    stamp = os.path.join(os.environ.get("TEMP", "."), "mb-halo-frame.txt")
    try:
        os.remove(stamp)
    except OSError:
        pass
    p = subprocess.Popen([NEW_EXE, "--preview-halo"], cwd=os.path.dirname(NEW_EXE))
    cx, cy = 700, 430
    # 文字气泡在光圈右侧，所以取景框要往右留足空间
    bx0, by0, bx1, by1 = cx - 90, cy - 105, cx + 470, cy + 105
    saved, seen = 0, set()
    t0 = time.time()
    last_idx = -1
    try:
        show_desktop()                     # 背景切到干净桌面，避免 IDE 文字入镜
        while time.time() - t0 < wait and saved < 8:
            try:
                with open(stamp) as f:
                    idx = int(f.read().strip())
            except Exception:
                time.sleep(0.2)
                continue
            if idx != last_idx and idx not in seen:
                last_idx = idx
                time.sleep(0.35)           # 等这一帧画稳
                u32.SetCursorPos(cx, cy)
                time.sleep(0.15)
                im = ImageGrab.grab(bbox=(bx0, by0, bx1, by1), all_screens=True).convert("RGB")
                seen.add(idx)
                saved += 1
                im.save(os.path.join(outdir, f"halo_{idx + 1:02d}.png"))
                print(f"  ok frame {idx + 1}")
            time.sleep(0.15)
    finally:
        show_desktop()                     # 还原桌面
        p.terminate()
        kill_all()
    return saved


# ---------------- 传输记录 / 日志窗口 ----------------

HISTORY_TITLE = "鼠下快传 传输记录"
JOURNAL = os.path.join(os.environ["APPDATA"], "MouseBridge", "transfers.jsonl")


def _seed_journal():
    """写入几条示例记录，便于出图；返回还原用的备份（None 表示原本没有）。"""
    backup = None
    if os.path.exists(JOURNAL):
        backup = JOURNAL + ".shotbak"
        os.replace(JOURNAL, backup)
    os.makedirs(os.path.dirname(JOURNAL), exist_ok=True)
    import json
    from datetime import datetime, timedelta

    base = datetime.now().replace(hour=9, minute=12, second=0, microsecond=0)

    def rec(i, send, label, count, total, done, state, target, note, via, at, dur):
        st = base + timedelta(minutes=at)
        return {
            "Id": 638000000000000000 + i,
            "Start": st.isoformat(),
            "End": (st + timedelta(seconds=dur)).isoformat(),
            "Send": send, "Peer": "ACER-LAPTOP", "Label": label,
            "FileCount": count, "TotalBytes": total, "DoneBytes": done,
            "State": state, "Target": target, "Note": note, "Via": via,
        }

    rows = [
        rec(1, True, "发送 3 个文件", 3, 1289000000, 1289000000, 1, "→ ACER-LAPTOP", "", "跨机拖拽", 258, 41),
        rec(2, False, "接收 12 个文件", 12, 456000000, 456000000, 1, r"C:\Users\Thomajesty\Downloads\项目备份", "12 项已落地", "跨机拖拽", 213, 72),
        rec(3, False, "接收 1 个文件", 1, 88000000, 31200000, 2, "", "连接断开", "剪贴板", 168, 0),
        rec(4, True, "发送 2 个文件", 2, 5400000, 5400000, 1, "→ ACER-LAPTOP", "", "剪贴板", 142, 4),
        rec(5, False, "接收 5 个文件", 5, 21000000, 21000000, 1, "剪贴板暂存", "已就绪 5 项，Ctrl+V 粘贴", "自动预接收", 76, 3),
        rec(6, False, "接收 1 个文件", 1, 3100000, 0, 3, "", "发送已取消", "剪贴板", 31, 0),
        rec(7, True, "发送 48 个文件", 48, 2600000000, 2600000000, 1, "→ ACER-LAPTOP", "", "跨机拖拽", 12, 96),
    ]
    with open(JOURNAL, "w", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    return backup


def _restore_journal(backup):
    try:
        if os.path.exists(JOURNAL):
            os.remove(JOURNAL)
        if backup:
            os.replace(backup, JOURNAL)
    except OSError:
        pass


def shoot_history(outdir):
    os.makedirs(outdir, exist_ok=True)
    kill_all()
    cfg = os.path.join(os.environ["APPDATA"], "MouseBridge", "config.json")
    cfg_backup = None
    if os.path.exists(cfg):
        cfg_backup = cfg + ".shotbak"
        os.replace(cfg, cfg_backup)
    os.makedirs(os.path.dirname(cfg), exist_ok=True)
    with open(cfg, "w", encoding="utf-8") as f:
        f.write('{"Configured": true}')
    backup = _seed_journal()
    try:
        p = subprocess.Popen([NEW_EXE, "--history"], cwd=os.path.dirname(NEW_EXE))
        h = wait_window(HISTORY_TITLE)
        if not h:
            print("!! 没等到记录窗口")
            p.terminate()
            return False
        focus(h)
        time.sleep(2.2)                    # 等表格与日志填满（日志首帧要等 LogTail 读盘）
        park_cursor()                      # 光标挪出窗口，否则光环会画在表格上
        bbox = None
        for _ in range(30):                # 等窗口真正完成布局/定位
            b = rect_of(h)
            if b[2] - b[0] > 200 and b[3] - b[1] > 200:
                bbox = b
                break
            time.sleep(0.3)
        if bbox is None:
            print("!! 记录窗口尺寸异常:", rect_of(h))
            p.terminate()
            return False
        grab_window(h, os.path.join(outdir, "10_传输记录与日志.png"))
        print("  ok 传输记录与日志")
        unfocus(h)
        p.terminate()
        time.sleep(0.8)
        return True
    finally:
        kill_all()
        _restore_journal(backup)
        if cfg_backup:
            os.replace(cfg_backup, cfg)
        else:
            try:
                os.remove(cfg)
            except OSError:
                pass


# ---------------- 拼接 ----------------

def _font(size):
    for name in ("msyh.ttc", "msyhbd.ttc", "simhei.ttf"):
        p = os.path.join(r"C:\Windows\Fonts", name)
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def compose():
    """拼两张验收图：新版界面总览（5 个设置页 + 传输记录窗口）、光环状态总览。

    注：旧的「新旧对比」图已废弃。v1.0.0 的旧截图是 22:41 用当时的 dist/MouseBridge.exe
    拍的，那时窗口没有置顶，被 IDE 挡住了一半；而 dist/MouseBridge.exe 在打包时已被
    v1.2.0 覆盖，旧二进制不可恢复，没法重拍，所以不再出新旧对比。"""
    after = os.path.join(SHOTS, "after")
    f = _font(22)
    fs = _font(15)

    # ---------- 新版界面总览 ----------
    names = ["01_连接", "02_屏幕与切换", "03_剪贴板与文件", "04_指示器", "05_高级", "06_关于",
             "10_传输记录与日志"]
    pages = []
    for n in names:
        p = os.path.join(after, n + ".png")
        if os.path.exists(p):
            pages.append((n, Image.open(p).convert("RGB")))
    if pages:
        W = 560
        thumbs = [(n, im.resize((W, int(im.height * W / im.width)))) for n, im in pages]
        cols = 3
        rows = (len(thumbs) + cols - 1) // cols
        ch = max(t[1].height for t in thumbs) + 44
        canvas = Image.new("RGB", (W * cols + 12 * (cols + 1), ch * rows + 66), (245, 245, 245))
        d = ImageDraw.Draw(canvas)
        d.text((16, 18), f"鼠下快传 v{APP_VER} 界面总览：设置 6 页 + 传输记录/运行日志",
               fill=(20, 20, 20), font=f)
        for i, (n, t) in enumerate(thumbs):
            x = 12 + (i % cols) * (W + 12)
            y = 56 + (i // cols) * ch
            canvas.paste(t, (x, y))
            d.rectangle((x - 1, y - 1, x + W, y + t.height), outline=(190, 190, 190))
            d.text((x + 2, y + t.height + 4), n[3:], fill=(90, 90, 90), font=fs)
        out = os.path.join(SHOTS, "00_界面总览.png")
        canvas.save(out)
        print("->", out)

    # ---------- 光环状态总览 ----------
    halo = os.path.join(SHOTS, "halo")
    files = sorted([f for f in os.listdir(halo) if f.startswith("halo_") and f.endswith(".png")]) if os.path.isdir(halo) else []
    if files:
        ims = [Image.open(os.path.join(halo, f)).convert("RGB") for f in files]
        cw2, chh = ims[0].width, ims[0].height
        cols2 = 2 if len(ims) > 3 else len(ims)
        rows2 = (len(ims) + cols2 - 1) // cols2
        g = Image.new("RGB", (cw2 * cols2 + 20, chh * rows2 + 76), (255, 255, 255))
        dg = ImageDraw.Draw(g)
        dg.text((16, 18), f"光标光环各状态（共 {len(ims)} 帧）：颜色 + 文字双通道指示当前状态与文件去向",
                fill=(20, 20, 20), font=f)
        for i, im in enumerate(ims):
            x = 10 + (i % cols2) * cw2
            y = 60 + (i // cols2) * chh
            g.paste(im, (x, y))
            dg.rectangle((x, y, x + im.width - 1, y + im.height - 1), outline=(220, 220, 220))
        outp = os.path.join(SHOTS, "01_光环状态总览.png")
        g.save(outp)
        print("->", outp)


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "all"
    os.makedirs(SHOTS, exist_ok=True)
    if cmd in ("after", "all"):
        print("新版设置页…")
        shoot_settings(NEW_EXE, os.path.join(SHOTS, "after"), "鼠下快传 MouseBridge",
                       force_unconfigured=True, tabs_arg=True)
    if cmd in ("halo", "all"):
        print("光环状态…")
        shoot_halo(os.path.join(SHOTS, "halo"))
    if cmd in ("history", "all"):
        print("传输记录与日志窗口…")
        shoot_history(os.path.join(SHOTS, "after"))
    if cmd in ("compose", "all"):
        compose()


if __name__ == "__main__":
    main()
