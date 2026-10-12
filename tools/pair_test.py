"""
双实例互连测试：在同一台机器上起两个实例，验证"两端都能主动发起连接"这条链路。

背景（v1.4.0 的真实故障）：
    上一版只有「被控端」会拨号，「主控端」只监听不发起 —— 用户坐在主控端点「连接」
    完全没反应。修复后两个角色都监听、都能发起。

做法：
    每个实例用独立的 MB_DATA_DIR（否则配置互相覆盖），并置 MB_ALLOW_SELF=1
    绕过"不许连本机地址"的保护（同机双实例在软件看来就是本机）。

用例：
    1. 主控端 + 被控端（一主一从，最常见的正确配法）    → 两端都该连上
    2. 两端都是主控端（用户最容易填错的情况）            → 也该连上，且有一端会话角色被仲裁反转
    3. 开 TLS 的一主一从                                  → 验证 SslEx 兼容层

用法：
    python tools/pair_test.py                # 测 net8 构建
    python tools/pair_test.py --tf net48     # 测 net48 构建（Win7/32 位兼容版）
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

shots_u32 = ctypes.windll.user32

BASE_CFG = {
    "Configured": True,
    "Role": "server",
    "Port": 39501,
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
}


def exe_for(tf: str) -> str:
    if tf == "net48":
        p = os.path.join(ROOT, "MouseBridge", "bin", "Release", "net48", "MouseBridge.exe")
    else:
        p = os.path.join(ROOT, "MouseBridge", "bin", "Release", "net8.0-windows", "MouseBridge.exe")
    if not os.path.isfile(p):
        print(f"[FAIL] 找不到可执行文件：{p}（先 dotnet build -c Release -f {tf}）")
        sys.exit(2)
    return p


def new_log(datadir: str) -> str:
    logs = os.path.join(datadir, "logs")
    today = time.strftime("%Y%m%d")
    return os.path.join(logs, f"mb-{today}.log")


def key(vk, down):
    """发一个物理按键（dwExtraInfo=0，钩子会当成真实输入）。
    用 keybd_event 就够了：我们只是要给被测程序制造真实的键事件。"""
    shots_u32.keybd_event(vk, 0, 0 if down else 2, 0)
    time.sleep(0.05)


def combo(*vks):
    for v in vks:
        key(v, True)
    for v in reversed(vks):
        key(v, False)


VK_CONTROL, VK_MENU, VK_1, VK_2 = 0x11, 0x12, 0x31, 0x32


def run_case(exe, name, a_role, b_role, port_a, port_b, tls, tmp,
             a_peer="", expect_one_link=True, takeover=False, confirm="1",
             expect_connected=True):
    print(f"\n=== 用例：{name} ===")
    dir_a = os.path.join(tmp, name, "a")
    dir_b = os.path.join(tmp, name, "b")
    for d in (dir_a, dir_b):
        os.makedirs(os.path.join(d, "logs"), exist_ok=True)

    cfg_a = dict(BASE_CFG, Role=a_role, Port=port_a, TlsEnabled=tls,
                 MachineLabel="HAOSTATION", PeerHost=a_peer)
    cfg_b = dict(BASE_CFG, Role=b_role, Port=port_b, TlsEnabled=tls,
                 MachineLabel="DESKTOP-B", PeerHost="127.0.0.1:" + str(port_a))
    with open(os.path.join(dir_a, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg_a, f, ensure_ascii=False, indent=2)
    with open(os.path.join(dir_b, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg_b, f, ensure_ascii=False, indent=2)

    launch(exe, [dir_a, dir_b], smoke=24 if takeover else 12, confirm=confirm)
    time.sleep(4 if takeover else 10)

    extra_ok = True
    if takeover:
        # 验"接管状态机 + 键盘跟随"这条链：Ctrl+Alt+2 跳到对端（本机进入 Capture 模式，
        # 之后所有按键都转发给对端），Ctrl+Alt+1 再拉回本机。
        combo(VK_CONTROL, VK_MENU, VK_2)
        time.sleep(3.0)
        la1 = read_text(new_log(dir_a))
        lb1 = read_text(new_log(dir_b))
        went = ("→ 切换到对端" in la1) and ("对端开始控制本机" in lb1)
        print(f"  Ctrl+Alt+2 跳对端：本机切走={('→ 切换到对端' in la1)} 对端进入被控={('对端开始控制本机' in lb1)}")

        combo(VK_CONTROL, VK_MENU, VK_1)
        time.sleep(3.0)
        back = ("← 回到本机" in read_text(new_log(dir_a))) and ("对端已回到本机" in read_text(new_log(dir_b)))
        print(f"  Ctrl+Alt+1 拉回本机：{'是' if back else '否'}")
        extra_ok = went and back

    kill_all(exe)

    la = read_text(new_log(dir_a))
    lb = read_text(new_log(dir_b))

    na = la.count("已连接对端")
    nb = lb.count("已连接对端")
    a_swap = "已仲裁反转" in la
    b_swap = "已仲裁反转" in lb
    same_role = a_role == b_role

    print(f"  A({a_role}) 建链次数={na} 会话角色被反转={a_swap}")
    print(f"  B({b_role}) 建链次数={nb} 会话角色被反转={b_swap}")
    if "同时建起两条链路" in la or "同时建起两条链路" in lb:
        print("  （两端确实撞出了两条链路，已自动取舍）")

    ok = (na >= 1 and nb >= 1) if expect_connected else (na == 0 and nb == 0)
    if expect_one_link and expect_connected:
        # 去重生效的话，每一端最终只会保留一条链路，也就只会打一条"已连接对端"
        ok = ok and na == 1 and nb == 1
    if expect_connected and same_role and na >= 1 and nb >= 1:
        ok = ok and (a_swap != b_swap)      # 同角色时恰好一端让位
    ok = ok and extra_ok
    print("  " + ("[PASS] " if ok else "[FAIL] ") + name)
    if not ok:
        print("  ---- A 日志尾部 ----")
        print("\n".join(la.splitlines()[-16:]))
        print("  ---- B 日志尾部 ----")
        print("\n".join(lb.splitlines()[-16:]))
    return ok


def launch(exe, dirs, smoke=12, confirm="1"):
    procs = []
    for d in dirs:
        env = dict(os.environ, MB_DATA_DIR=d, MB_ALLOW_SELF="1",
                   MB_SMOKE_SECONDS=str(smoke), MB_AUTO_CONFIRM=confirm)
        procs.append(subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe), env=env,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    return procs


def kill_all(exe):
    name = os.path.basename(exe)
    subprocess.run(["taskkill", "/IM", name, "/F"], stdout=subprocess.DEVNULL,
                   stderr=subprocess.DEVNULL, check=False)
    time.sleep(1)


def read_text(path):
    try:
        with open(path, "rb") as f:
            return f.read().decode("utf-8", "replace")
    except OSError:
        return ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tf", default="net8.0-windows")
    args = ap.parse_args()
    exe = exe_for(args.tf)
    print(f"目标框架：{args.tf}\n可执行文件：{exe}")
    kill_all(exe)

    tmp = tempfile.mkdtemp(prefix="mb-pair-")
    results = []
    try:
        results.append(run_case(exe, "1_一主一从_明文", "server", "client", 39601, 39602, False, tmp))
        results.append(run_case(exe, "2_两端都主控_明文", "server", "server", 39611, 39612, False, tmp))
        results.append(run_case(exe, "3_一主一从_TLS", "server", "client", 39621, 39622, True, tmp))
        # 4. 两端互相填了对方地址 → 会同时拨号撞出两条链路，必须自动去重到同一条
        results.append(run_case(exe, "4_双向同时拨号_去重", "server", "server",
                                39631, 39632, False, tmp,
                                a_peer="127.0.0.1:39632"))
        # 5. 接管状态机 + 键盘跟随：Ctrl+Alt+2 跳对端（进入 Capture）、Ctrl+Alt+1 拉回本机
        results.append(run_case(exe, "5_跳对端_再拉回本机", "server", "client",
                                39641, 39642, False, tmp, takeover=True))
        # 6. 被控端拒绝：密码对了也要被控端点「允许」才建链；拒绝时两端都不该连上
        results.append(run_case(exe, "6_被控端拒绝则不建链", "server", "client",
                                39651, 39652, False, tmp, confirm="deny",
                                expect_connected=False))
    finally:
        kill_all(exe)
        shutil.rmtree(tmp, ignore_errors=True)

    print("\n================ 汇总 ================")
    print(f"通过 {sum(results)}/{len(results)}")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
