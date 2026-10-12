"""
复现「第一次能连、断开后第二次连不上」的问题（问题 3）。

做法：单实例跑起来，用配置文件里的 PeerHost 指向"自己"（MB_ALLOW_SELF=1 放开保护），
让它在同机上一边监听一边自己连自己 —— 这样不需要第二台机器就能跑完整条
"监听 → 拨号 → 握手 → 建链 → 断开 → 重连" 的链路。

关键观测点（都从日志里抠）：
  1. 首次建链："已连接对端"
  2. 断开     ："连接断开"
  3. 二次建链 ：应再次出现"已连接对端"
  4. 端口绑定失败："监听循环异常" / SocketException / 地址已在使用
     —— 这是最可疑的：ApplyConfig() 里 _listener.Stop() 之后立刻 new TcpListener
        重新绑同一端口，若旧监听套接字尚未完全释放就会失败，而 ServerLoop 的
        catch(SocketException) 是**静默**吞掉的，于是本机再也不监听
        → 对方永远连不进来 → 表现就是"第二次不能成功使用"。

触发方式：
  --smoke 模式下 Orchestrator 正常启动；测试脚本通过修改 config.json 并让程序
  重新读取（模拟用户在设置页点「连接」→ ApplyConfig）来触发二次绑定。
  为了不改动产品代码路径，这里用最贴近真实的方式：
      写 config → 启动 → 等建链 → 改 config（换一个 PeerHost 迫使重连）
      → 用 Ctrl+Alt+1/2 之类的热键无法触发 ApplyConfig，
      所以改为：连上后杀掉进程再启动第二次（用户"关闭后再开"的语义），
      以及在同一进程生命周期内用热键触发的场景分开观察。
"""
import ctypes
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

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
    "MachineLabel": "SELF",
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
}


def exe_for(tf):
    if tf == "net48":
        return os.path.join(ROOT, "MouseBridge", "bin", "Release", "net48", "MouseBridge.exe")
    return os.path.join(ROOT, "MouseBridge", "bin", "Release", "net8.0-windows", "MouseBridge.exe")


def new_log(datadir):
    return os.path.join(datadir, "logs", "mb-" + time.strftime("%Y%m%d") + ".log")


def read_text(p):
    try:
        with open(p, "rb") as f:
            return f.read().decode("utf-8", "replace")
    except OSError:
        return ""


def kill_all(exe):
    subprocess.run(["taskkill", "/IM", os.path.basename(exe), "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    time.sleep(1.2)


def launch(exe, datadir, seconds, tag="", apply_config_at=0, bug_no_reset=False):
    """启动一个实例。MB_DATA_DIR 走环境变量，所以额外把 PID 记到文件里，
    方便测试按数据目录精确杀掉某一个实例（不能靠 CommandLine 匹配，那里看不到环境变量）。

    apply_config_at > 0 时，程序会在启动 N 秒后调用 ApplyConfig()，
    等价于"用户又点了一次「连接」" —— 用来回归二次连接失败的 bug。

    bug_no_reset=True 时告诉程序"故意不修这个 bug"，用于验证测试本身确实能抓到它。
    """
    env = dict(os.environ, MB_DATA_DIR=datadir, MB_ALLOW_SELF="1",
               MB_SMOKE_SECONDS=str(seconds), MB_AUTO_CONFIRM="1")
    if apply_config_at > 0:
        env["MB_APPLY_CONFIG_AT"] = str(apply_config_at)
    if bug_no_reset:
        env["MB_BUG_NO_RESET_CONNECTED"] = "1"
    p = subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe), env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if tag:
        with open(os.path.join(datadir, "_pid.txt"), "w") as f:
            f.write(str(p.pid))
    return p


def summarize(log):
    return {
        "connected": log.count("已连接对端"),
        "closed": log.count("连接断开"),
        "listen_fail": len(re.findall(r"监听循环异常|地址已在使用|Only one usage", log)),
        "listening": len(re.findall(r"监听端口", log)),
        "addr_in_use": log.count("AddressAlreadyInUse") + log.count("10048"),
    }


def case_restart(exe, tag, port):
    """用例 A：改配置触发 ApplyConfig 二次绑定（等价于用户第二次点「连接」）。

    这条路径是 v1.6.4 里最可疑的一处：
      ApplyConfig() → _listener.Stop() → _netCts.Cancel() → 立刻 new TcpListener(同端口)
    若旧监听套接字尚未完全释放，新 ServerLoop 的 Start() 会抛 SocketException，
    而 ServerLoop 里的 catch(SocketException) {} 是**静默**吞掉的 —— 界面上看不出任何异常，
    但本机从此不再监听，对方永远连不进来，表现就是"第二次不能成功使用"。

    做法：用 A(config) + B(config) 两个数据目录，脚本改 B 的 config.json 并让 B
    重新走一遍 ApplyConfig。（进程内改配置需界面操作，所以在 --smoke 下改用：
    启动 B 两次，第二次启动前把日志截断计数，看它能否再绑同一端口并建链。）
    """
    print(f"\n=== 用例 {tag}：同一端口连续两次启动（端口重绑） ===")
    tmp = tempfile.mkdtemp(prefix="mb-recon-")
    tmp_peer = tempfile.mkdtemp(prefix="mb-recon-peer-")
    try:
        # 对端（被控端，稳定监听）
        cfg_peer = dict(BASE_CFG, Role="client", Port=port, PeerHost="",
                        MachineLabel="PEER", ConfirmOnConnect=False)
        os.makedirs(os.path.join(tmp_peer, "logs"), exist_ok=True)
        with open(os.path.join(tmp_peer, "config.json"), "w", encoding="utf-8") as f:
            json.dump(cfg_peer, f, ensure_ascii=False, indent=2)
        launch(exe, tmp_peer, 60)
        time.sleep(1.5)

        cfg = dict(BASE_CFG, Role="server", Port=port + 1,
                   PeerHost="127.0.0.1:" + str(port), MachineLabel="MASTER")
        os.makedirs(os.path.join(tmp, "logs"), exist_ok=True)
        with open(os.path.join(tmp, "config.json"), "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)

        # 第一轮
        launch(exe, tmp, 12)
        time.sleep(8)
        kill_peer(exe, tmp)
        time.sleep(1.5)
        log1 = read_text(new_log(tmp))
        s1 = summarize(log1)
        print(f"  第1轮：建链={s1['connected']} 监听成功={s1['listening']} 监听异常={s1['listen_fail']}")

        # 第二轮：同端口再绑一次（上一轮的监听套接字可能还在 TIME_WAIT）
        launch(exe, tmp, 12)
        time.sleep(8)
        la = read_text(new_log(tmp))
        s2 = summarize(la)
        kill_all(exe)
        print(f"  第2轮：建链={s2['connected'] - s1['connected']} "
              f"监听成功={s2['listening'] - s1['listening']} 监听异常={s2['listen_fail'] - s1['listen_fail']}")

        ok = (s1["connected"] >= 1) and (s2["connected"] - s1["connected"] >= 1)
        print("  " + ("[PASS] " if ok else "[FAIL] ") + "二次启动仍能建链")
        if not ok:
            print("  ---- 第2轮日志尾部 ----")
            print("\n".join(la.splitlines()[-30:]))
        return ok
    finally:
        kill_all(exe)
        shutil.rmtree(tmp, ignore_errors=True)
        shutil.rmtree(tmp_peer, ignore_errors=True)


def case_reconnect_inproc(exe, tag, port):
    """用例 B：同一进程内断开后自动重连。

    场景还原：A 是主控端（server），去连 B（client，被控端）。
    用户"关闭"B 之后（进程被杀），A 侧应当：
      ① 在心跳超时（9 秒）内检测到链路断开并打出"连接断开"
      ② B 重新启动后自动重连（A 的 ClientLoop 重新拨号）
    """
    print(f"\n=== 用例 {tag}：断开检测 → 自动重连 ===")
    tmp_a = tempfile.mkdtemp(prefix="mb-rec-a-")
    tmp_b = tempfile.mkdtemp(prefix="mb-rec-b-")
    try:
        # A：主控端，主动连 B
        cfg_a = dict(BASE_CFG, Role="server", Port=port, PeerHost="127.0.0.1:" + str(port + 1),
                     MachineLabel="MASTER", ConfirmOnConnect=False)
        # B：被控端，只监听（不填地址，避免双向拨号）
        cfg_b = dict(BASE_CFG, Role="client", Port=port + 1, PeerHost="",
                     MachineLabel="WORKER", ConfirmOnConnect=False)
        for d, c in ((tmp_a, cfg_a), (tmp_b, cfg_b)):
            os.makedirs(os.path.join(d, "logs"), exist_ok=True)
            with open(os.path.join(d, "config.json"), "w", encoding="utf-8") as f:
                json.dump(c, f, ensure_ascii=False, indent=2)

        launch(exe, tmp_b, 60, tag="b1")   # B 先起（被控端等连）
        time.sleep(1.5)
        launch(exe, tmp_a, 60, tag="a")     # A 后起（主控端去连）
        time.sleep(8)

        la = read_text(new_log(tmp_a))
        c1 = la.count("已连接对端")
        print(f"  A 首次建链={c1}")

        # 杀掉 B（模拟用户"关闭"了被控端软件）
        kill_peer(exe, tmp_b)
        # 心跳 2s 一次、9s 判超时；留 14 秒观察窗口
        time.sleep(14)
        la2 = read_text(new_log(tmp_a))
        closed = la2.count("连接断开") - la.count("连接断开")
        print(f"  A 检测到断开={closed}")

        # 把 B 启动回来 → A 应自动重连
        launch(exe, tmp_b, 20, tag="b2")
        time.sleep(10)
        la3 = read_text(new_log(tmp_a))
        c2 = la3.count("已连接对端")

        print(f"  A 重连后建链={c2}（累计）")
        ok = c1 >= 1 and closed >= 1 and c2 > c1
        print("  " + ("[PASS] " if ok else "[FAIL] ") + "断开检测 + 自动重连")
        if not ok:
            print("  ---- A 日志尾部 ----")
            print("\n".join(la3.splitlines()[-45:]))
        return ok
    finally:
        kill_all(exe)
        shutil.rmtree(tmp_a, ignore_errors=True)
        shutil.rmtree(tmp_b, ignore_errors=True)


def kill_peer(exe, datadir):
    """按数据目录里记下的 PID 精确杀掉对应实例。"""
    try:
        with open(os.path.join(datadir, "_pid.txt")) as f:
            pid = int(f.read().strip())
    except Exception as e:
        print(f"    [warn] 读不到 PID：{e}")
        return
    subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)


def case_double_connect(exe, tag, port, bug_no_reset=False):
    """用例 C：**用户点第二次「连接」** 之后仍能建链（本 bug 的精确回归）。

    复现的用户操作：
      1. 第一次点「连接」→ 建链成功
      2. 关掉/再打开，第二次点「连接」→ BtnConnect_Click 里会调用 ApplyConfig()

    ApplyConfig() 会把 _link 摘成 null 再 Close()，于是 OnLinkClosed 里
    `if (!ReferenceEquals(_link, dead)) return;` 提前返回，**Connected 永远卡在 true**，
    导致 ClientLoop 不再拨号、ServerLoop 把新进来的连接直接关掉
    —— 表现就是"第二次怎么都连不上"。

    B 侧在启动 N 秒后自动调一次 ApplyConfig()，然后必须还能重新建链。
    bug_no_reset=True 时故意保留这个 bug，用来确认测试确实抓得住它（应当 FAIL）。
    """
    label = "（故意保留 bug，期望 FAIL）" if bug_no_reset else ""
    print(f"\n=== 用例 {tag}：点第二次连接后仍能建链 {label} ===")
    tmp_b = tempfile.mkdtemp(prefix="mb-dc-b-")   # 被控端（稳定服务）
    tmp_a = tempfile.mkdtemp(prefix="mb-dc-a-")   # 主控端（会二次点连接）
    try:
        cfg_b = dict(BASE_CFG, Role="client", Port=port + 1, PeerHost="",
                     MachineLabel="WORKER", ConfirmOnConnect=False)
        cfg_a = dict(BASE_CFG, Role="server", Port=port,
                     PeerHost="127.0.0.1:" + str(port + 1), MachineLabel="MASTER")
        for d, c in ((tmp_b, cfg_b), (tmp_a, cfg_a)):
            os.makedirs(os.path.join(d, "logs"), exist_ok=True)
            with open(os.path.join(d, "config.json"), "w", encoding="utf-8") as f:
                json.dump(c, f, ensure_ascii=False, indent=2)

        launch(exe, tmp_b, 34, tag="b")
        time.sleep(1.5)
        # A 在启动 8 秒后模拟"再点一次连接"
        launch(exe, tmp_a, 34, tag="a", apply_config_at=8, bug_no_reset=bug_no_reset)

        time.sleep(6)
        la1 = read_text(new_log(tmp_a))
        c_before = la1.count("已连接对端")
        print(f"  A 第二次点连接前：建链={c_before}")

        time.sleep(16)   # 跨过 ApplyConfig 时刻（第 8 秒）后再等足重连时间
        la2 = read_text(new_log(tmp_a))
        c_after = la2.count("已连接对端")
        applied2 = la2.count("模拟用户再次点")
        closed = la2.count("连接断开")

        print(f"  A 执行 ApplyConfig 次数={applied2} 断开={closed} 最终建链={c_after}")
        reconnected = applied2 >= 1 and c_before >= 1 and c_after > c_before
        # 带 bug 时要求"复现失败"，不带 bug 时要求"确实恢复"
        ok = (not reconnected) if bug_no_reset else reconnected
        if bug_no_reset:
            print("  " + ("[PASS] " if ok else "[FAIL] ") + "测试有效性：bug 存在时确实复现失败")
        else:
            print("  " + ("[PASS] " if ok else "[FAIL] ") + "点第二次连接后仍能建链")
        if not ok:
            print("  ---- A 日志尾部 ----")
            print("\n".join(la2.splitlines()[-45:]))
        return ok
    finally:
        kill_all(exe)
        shutil.rmtree(tmp_a, ignore_errors=True)
        shutil.rmtree(tmp_b, ignore_errors=True)


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--tf", default="net8.0-windows")
    ap.add_argument("--only", default="", help="只跑某个用例：A/B/C")
    args = ap.parse_args()
    exe = exe_for(args.tf)
    print(f"可执行文件：{exe}")
    kill_all(exe)

    results = []
    if args.only in ("", "A"):
        results.append(case_restart(exe, "A 重启", 39711))
    if args.only in ("", "B"):
        results.append(case_reconnect_inproc(exe, "B 断线重连", 39721))
    if args.only in ("", "C"):
        # 先用"故意保留 bug"跑一遍，证明测试确实抓得住 → 再跑修复版
        caught = case_double_connect(exe, "C 二次点连接", 39731, bug_no_reset=True)
        fixed = case_double_connect(exe, "C 二次点连接", 39741, bug_no_reset=False)
        results.append(caught and fixed)
        print(f"  → 回归测试有效性：{'确认（bug 存在时必失败）' if caught else '存疑（bug 存在时竟然也通过）'}")
    print("\n================ 汇总 ================")
    print(f"通过 {sum(results)}/{len(results)}")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
