"""
设置界面"挡住跳屏"回归测试（v1.6.9）。

用户反馈原文：
    「在打开软件的设置界面时就不能操作跳动到对面屏幕了 这是刻意保留的功能还是bug
      这样体验有问题 感觉像是卡住了 需要优化 应该要放行的」

根因（已在 v1.6.9 修复）：
    旧逻辑在远控（Capture）期间留了一条口子 —— "前台是本程序自己的窗口时，
    不吞鼠标、并把控制权弹回本机"。而设置窗口只要开着就**永远是前台**，于是：
    推到边缘 → 切到对端 → 下一帧（实测 8ms）判"前台是自家界面" → 弹回本机，
    来回抖动，看起来像卡死。见 logs 里"→ 切换到对端 / ← 回到本机"相隔 8ms 的记录。

用例：
    0. 静态断言：Orchestrator.cs 里不再有"前台自家窗口就 SwitchToLocal"的旧模式，
       HookEngine 的鼠标 Capture 分支必须有 BugOwnWindow/MouseLetThrough 守卫。
    1. 正向：A 开着设置窗口（MB_SMOKE_SETTINGS=1），Ctrl+Alt+2 跳对端后制造真实鼠标
       位移（SendInput，dwExtraInfo=0，钩子只认物理输入）→ 断言"切换到对端"之后
       **不再出现**"回到本机"。
    2. 反向验证：同样的场景但置 MB_BUG_OWN_WINDOW=1（恢复旧行为）→ 用例 1 的判定
       必须变成 FAIL。否则说明用例 1 根本没抓到那个 bug，修不修都"绿"。

用法：
    python tools/own_window_test.py             # 测 net8 构建
    python tools/own_window_test.py --tf net48  # 测 net48 构建（Win7/32 位兼容版）

注意：本测试会动真实鼠标（SendInput 制造位移），请与其他联机测试串行执行。
"""
import argparse
import ctypes
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

u32 = ctypes.windll.user32

BASE_CFG = {
    "Configured": True,
    "Role": "server",
    "Port": 39701,
    "TlsEnabled": False,
    "PasswordProt": "",
    "AutoDiscover": False,
    "PeerHost": "",
    "PeerSide": "right",
    "SwitchDelayMs": 0,
    "LockKeyEnabled": True,
    "HotkeysEnabled": True,
    "TakeoverEnabled": True,
    "MachineLabel": "",
    "ServiceOn": True,
    "ConfirmOnConnect": False,
    "SyncText": True,
    "SyncImage": True,
    "SyncFiles": True,
    "EagerReceive": True,
    "EagerCapMB": 256,
    "ClipCapMB": 2048,
    "DropDir": "Desktop",
    "OpenFolderAfterDrop": False,
    "DragDropAcrossMachines": True,
    "DropFallbackToDefaultDir": True,
    "IndicatorEnabled": False,
    "IndicatorSize": 44,
    "IndicatorText": True,
    "ShowProgressArc": True,
    "ShowBadge": True,
    "AutoStart": False,
    "AutoStartMinimized": True,
    "LogLevel": 1,
    "PinnedServerThumb": "",
    "AllowReverseControl": False,
    "ConfirmReverseControl": False,
}

VK_CONTROL, VK_MENU, VK_1, VK_2 = 0x11, 0x12, 0x31, 0x32


def exe_for(tf):
    if tf == "net48":
        p = os.path.join(ROOT, "MouseBridge", "bin", "Release", "net48", "MouseBridge.exe")
    else:
        p = os.path.join(ROOT, "MouseBridge", "bin", "Release", "net8.0-windows", "MouseBridge.exe")
    if not os.path.isfile(p):
        print(f"[FAIL] 找不到可执行文件：{p}（先 dotnet build -c Release）")
        sys.exit(2)
    return p


def new_log(datadir):
    return os.path.join(datadir, "logs", f"mb-{time.strftime('%Y%m%d')}.log")


def read_text(path):
    try:
        with open(path, "rb") as f:
            return f.read().decode("utf-8", "replace")
    except OSError:
        return ""


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long),
                ("mouseData", ctypes.c_ulong), ("dwFlags", ctypes.c_ulong),
                ("time", ctypes.c_ulong), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT)]
    _anonymous_ = ("u",)
    _fields_ = [("type", ctypes.c_ulong), ("u", _U)]


MOUSEEVENTF_MOVE = 0x0001

SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN = 76, 77
SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 78, 79


def push_to_right_edge():
    """把物理鼠标真实地推到虚拟桌面最右边缘 —— 用户跳屏的实际操作。

    不用 Ctrl+Alt+2 热键：热键路径要经 UiUtil.Run 调度到 UI 线程执行，
    启动初期 UI 线程忙时延迟不可控；而"推到边缘"在钩子线程里直接调
    SwitchToRemote，立即生效，且正是要测的那条用户路径。
    """
    x0 = u32.GetSystemMetrics(SM_XVIRTUALSCREEN)
    y0 = u32.GetSystemMetrics(SM_YVIRTUALSCREEN)
    w = u32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
    h = u32.GetSystemMetrics(SM_CYVIRTUALSCREEN)
    inp = INPUT()
    inp.type = 0
    inp.mi.mouseData = 0
    inp.mi.dwFlags = MOUSEEVENTF_MOVE
    inp.mi.time = 0
    inp.mi.dwExtraInfo = None
    # 先跳到屏幕中央（产生"远离边缘"事件 → ArmOutIfAway 武装 _armedOut）
    u32.SetCursorPos(x0 + w // 2, y0 + h // 2)
    time.sleep(0.3)
    # 分 30 小步推到最右边缘（步数多了，切换后残留的推入事件会持续几百毫秒，
    # 与产品的"进场 300ms 宽限 + 4 帧去抖"打架，误报弹回 —— 真实用户一次推入
    # 也就几百毫秒，事件余量远小于这个量级）
    for _ in range(30):
        inp.mi.dx = max(4, w // 30)
        inp.mi.dy = 0
        u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
        time.sleep(0.01)
    time.sleep(0.2)


def wiggle(n=24, step=8, pause=0.015):
    """制造 n 次真实物理鼠标位移（dwExtraInfo=0，钩子当成真实输入收下）。

    只向"远端深处"方向动（右下）：对端坐标系里 _cx 从返回侧边缘起步，
    而回程判定是"进场即武装、碰到返回侧边缘就弹回"—— 这是正常设计。
    若在这里做双向抖动，向返回侧的位移会立刻触发回程，测试就没法区分
    "自家窗口弹回"和"正常回程"了。
    """
    inp = INPUT()
    inp.type = 0
    for i in range(n):
        inp.mi.dx = step
        inp.mi.dy = step // 2
        inp.mi.mouseData = 0
        inp.mi.dwFlags = MOUSEEVENTF_MOVE
        inp.mi.time = 0
        inp.mi.dwExtraInfo = None
        u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
        time.sleep(pause)


def key(vk, down):
    u32.keybd_event(vk, 0, 0 if down else 2, 0)
    time.sleep(0.05)


def combo(*vks):
    for v in vks:
        key(v, True)
    for v in reversed(vks):
        key(v, False)


def kill_all(exe):
    subprocess.run(["taskkill", "/IM", os.path.basename(exe), "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    time.sleep(1.5)


def case_static():
    """用例 0：静态断言旧行为已被移除、反向验证钩子仍在。"""
    orch = read_text(os.path.join(ROOT, "MouseBridge", "Control", "Orchestrator.cs"))
    hook = read_text(os.path.join(ROOT, "MouseBridge", "Input", "HookEngine.cs"))
    ok = True
    old = "if (Native.IsOwnProcessForeground()) { SwitchToLocal(); return; }"
    if old in orch:
        print("  [FAIL] Orchestrator.cs 仍保留旧模式：前台自家窗口就 SwitchToLocal")
        ok = False
    if "BugOwnWindow" not in orch or "MB_BUG_OWN_WINDOW" not in orch:
        print("  [FAIL] Orchestrator.cs 缺少 MB_BUG_OWN_WINDOW 反向验证开关")
        ok = False
    if "MouseLetThrough" not in hook:
        print("  [FAIL] HookEngine.cs 缺少 MouseLetThrough（确认弹窗放行闸门）")
        ok = False
    print(f"  {'[PASS]' if ok else '[FAIL]'} 0_静态断言_旧行为已移除")
    return ok


def run_live(exe, tmp, name, bug=False):
    """用例 1/2 共用的活体场景。

    返回 (判定是否通过, 详情)。用例 2（bug=True）期望这个判定变成 False。
    """
    dir_a = os.path.join(tmp, name, "a")
    dir_b = os.path.join(tmp, name, "b")
    for d in (dir_a, dir_b):
        os.makedirs(os.path.join(d, "logs"), exist_ok=True)

    cfg_a = dict(BASE_CFG, Role="server", Port=39701, MachineLabel="HAOSTATION",
                 PeerHost="127.0.0.1:39702")
    cfg_b = dict(BASE_CFG, Role="client", Port=39702, MachineLabel="DESKTOP-B",
                 PeerHost="127.0.0.1:39701")
    with open(os.path.join(dir_a, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg_a, f, ensure_ascii=False, indent=2)
    with open(os.path.join(dir_b, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg_b, f, ensure_ascii=False, indent=2)

    env_common = dict(os.environ, MB_DATA_DIR="", MB_ALLOW_SELF="1",
                      MB_SMOKE_SECONDS="45", MB_AUTO_CONFIRM="1")
    env_common.pop("MB_DATA_DIR")
    if bug:
        env_common["MB_BUG_OWN_WINDOW"] = "1"

    p_a = subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe),
                           env=dict(env_common, MB_DATA_DIR=dir_a, MB_SMOKE_SETTINGS="1"),
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    p_b = subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe),
                           env=dict(env_common, MB_DATA_DIR=dir_b),
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print(f"    · 启动 A(主控+设置窗口) pid={p_a.pid}  B(被控) pid={p_b.pid}")

    time.sleep(5)   # 先给进程起来的时间

    # 等连接真正建立（最多再等 20s）—— 8 秒裸等在冷启动时会不够，
    # 热键分支要求 Connected 才生效，抢跑就是"静默不切"。
    connected = False
    for _ in range(20):
        if "已连接对端" in read_text(new_log(dir_a)) and "已连接对端" in read_text(new_log(dir_b)):
            connected = True
            break
        time.sleep(1.0)
    print(f"    · 连接建立：{connected}")
    time.sleep(2)   # 再稳一下，让设置窗口的显示状态安定

    push_to_right_edge()   # 用户实际操作：把鼠标推到屏幕右边缘

    # 切换在钩子线程直接执行，一般 1 秒内就有日志；轮询最多 12 秒兜底。
    went = False
    for _ in range(12):
        la = read_text(new_log(dir_a))
        lb = read_text(new_log(dir_b))
        # bug 模式下可能切过去一帧就被弹回，B 端未必来得及打"对端开始控制本机"，
        # 所以只要求本机日志出现"切换到对端"即可。
        if "→ 切换到对端" in la:
            went = True
            break
        time.sleep(1.0)
    print(f"    · Ctrl+Alt+2 跳对端：本机切走={'→ 切换到对端' in la} 对端进入被控={'对端开始控制本机' in lb}")

    wiggle()          # 真实鼠标位移 —— 旧行为在这里被"前台是设置窗口"弹回
    time.sleep(2.5)

    la = read_text(new_log(dir_a))   # 先读日志再拉回，避免清理动作污染判定
    idx = la.rfind("→ 切换到对端")
    stayed, detail = False, "未切换到对端"
    if idx >= 0:
        after = la[idx:]
        if "← 回到本机" in after:
            n = after.count("← 回到本机")
            detail = f"切换后被弹回本机 ×{n}（抖动复现）"
        else:
            stayed = True
            detail = "切换到对端并保持（设置窗口开着也没弹回）"
    print(f"    · 判定：{detail}")

    combo(VK_CONTROL, VK_MENU, VK_1)   # 清理：拉回本机（若还在对端）
    time.sleep(1.0)
    kill_all(exe)
    shutil.rmtree(os.path.join(tmp, name), ignore_errors=True)
    return went and stayed, detail


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tf", default="net8.0-windows")
    args = ap.parse_args()
    exe = exe_for(args.tf)
    print(f"目标框架：{args.tf}\n可执行文件：{exe}")
    kill_all(exe)

    tmp = tempfile.mkdtemp(prefix="mb-ownwin-")
    results = []
    try:
        results.append(case_static())

        print("\n=== 用例 1：设置窗口开着，跳到对端后应保持（正向） ===")
        ok1, d1 = run_live(exe, tmp, "1_设置窗开着_跳对端保持", bug=False)
        print(f"  {'[PASS]' if ok1 else '[FAIL]'} 1_设置窗开着_跳对端保持　{d1}")
        results.append(ok1)

        print("\n=== 用例 2：MB_BUG_OWN_WINDOW=1 复现旧行为（反向验证，期望 FAIL） ===")
        ok2, d2 = run_live(exe, tmp, "2_反向验证_旧行为必须复现", bug=True)
        reversed_ok = not ok2
        print(f"  {'[PASS]' if reversed_ok else '[FAIL]'} 2_反向验证_旧行为必须复现　旧行为复现={not ok2}（{d2}）")
        results.append(reversed_ok)
    finally:
        kill_all(exe)
        shutil.rmtree(tmp, ignore_errors=True)

    print("\n================ 汇总 ================")
    print(f"通过 {sum(results)}/{len(results)}")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
