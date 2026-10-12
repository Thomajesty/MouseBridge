"""
连接页视觉验收：分别截「主控端视图」「被控端视图」，以及被控端的连接确认弹窗。

为什么要单独一个脚本：
    shots.py 用的是真实 %APPDATA% 配置，只能拍到当前角色那一种界面；
    而这一版连接页会按角色原位变形，两种形态都得留档对比。
    所以这里用 MB_DATA_DIR 起临时配置，不碰用户自己的设置。

用法：python tools/shot_connect.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shots  # noqa: E402

BASE = {
    "Configured": True, "Role": "server", "Port": 39501, "TlsEnabled": False,
    "PasswordProt": "", "AutoDiscover": True, "PeerHost": "192.168.1.2:39501",
    "PeerSide": "right", "SwitchDelayMs": 0, "LockKeyEnabled": True, "HotkeysEnabled": True,
    "TakeoverEnabled": True, "MachineLabel": "", "ServiceOn": True, "ConfirmOnConnect": True,
    "SyncText": True, "SyncImage": True, "SyncFiles": True, "EagerReceive": True,
    "EagerCapMB": 256, "ClipCapMB": 2048, "DropDir": "Desktop", "OpenFolderAfterDrop": False,
    "DragDropAcrossMachines": True, "DropFallbackToDefaultDir": True, "IndicatorEnabled": True,
    "IndicatorSize": 44, "IndicatorText": True, "ShowProgressArc": True, "ShowBadge": True,
    "AutoStart": False, "AutoStartMinimized": True, "LogLevel": 1, "PinnedServerThumb": "",
}


def kill_old():
    for name in ("MouseBridge.exe", "MouseBridge-1.6.0-win-x64.exe", "MouseBridge-1.4.0-win-x64.exe"):
        subprocess.run(["taskkill", "/IM", name, "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    time.sleep(1)


def write_cfg(d, **over):
    os.makedirs(os.path.join(d, "logs"), exist_ok=True)
    cfg = dict(BASE)
    cfg.update(over)
    with open(os.path.join(d, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)


def shoot(exe, datadir, out, tab="0", env_extra=None, wait=1.2):
    env = dict(os.environ, MB_DATA_DIR=datadir)
    if env_extra:
        env.update(env_extra)
    p = subprocess.Popen([exe, "--settings", "--tab", tab], cwd=os.path.dirname(exe), env=env)
    try:
        hwnd = shots.wait_window("鼠下快传 MouseBridge", 25)
        if not hwnd:
            print(f"  [FAIL] 没等到窗口 → {out}")
            return False
        shots.focus(hwnd)
        shots.park_cursor()
        time.sleep(wait)
        shots.grab_window(hwnd, out)
        print("  ok", os.path.basename(out))
        return True
    finally:
        try:
            p.terminate()
        except Exception:
            pass


def main():
    exe = shots.NEW_EXE
    outdir = os.path.join(shots.SHOTS, "after")
    os.makedirs(outdir, exist_ok=True)
    kill_old()

    tmp = tempfile.mkdtemp(prefix="mb-shot-")
    try:
        d1 = os.path.join(tmp, "server")
        write_cfg(d1, Role="server", ServiceOn=True)
        print("主控端视图…")
        shoot(exe, d1, os.path.join(outdir, "01_连接.png"))

        d2 = os.path.join(tmp, "client")
        write_cfg(d2, Role="client", ServiceOn=True)
        print("被控端视图…")
        shoot(exe, d2, os.path.join(outdir, "01_连接_被控端.png"))

        print("被控端连接确认弹窗…")
        kill_old()
        d3 = os.path.join(tmp, "client2")     # 被控端（服务端），不自动确认，留出弹窗
        d4 = os.path.join(tmp, "server2")     # 主控端，主动连过去
        write_cfg(d3, Role="client", Port=39701, ServiceOn=True, ConfirmOnConnect=True,
                  MachineLabel="DESKTOP-B")
        write_cfg(d4, Role="server", Port=39702, PeerHost="127.0.0.1:39701",
                  MachineLabel="HAOSTATION")
        env3 = dict(os.environ, MB_DATA_DIR=d3, MB_ALLOW_SELF="1", MB_SMOKE_SECONDS="40")
        env4 = dict(os.environ, MB_DATA_DIR=d4, MB_ALLOW_SELF="1", MB_SMOKE_SECONDS="40")
        p3 = subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe), env=env3)
        time.sleep(2)
        p4 = subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe), env=env4)
        hwnd = 0
        for _ in range(40):
            hwnd = shots.find_window("连接请求 · MouseBridge")
            if hwnd:
                break
            time.sleep(0.5)
        if hwnd:
            shots.focus(hwnd)
            shots.park_cursor()
            time.sleep(0.6)
            shots.grab_window(hwnd, os.path.join(outdir, "01_连接确认弹窗.png"))
            print("  ok 01_连接确认弹窗.png")
        else:
            print("  [FAIL] 没等到确认弹窗")
        p3.terminate()
        p4.terminate()
    finally:
        kill_old()
        shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
