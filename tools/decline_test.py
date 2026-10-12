"""
连接邀请治理测试：验证两件被用户直接点名的烦人事已经修掉。

背景（2026-10-12 真实故障）
────────────────────────────────────────────────────────────────
用户环境：本机 192.168.1.4/24，对端 DESKTOP-45A675U 同时有 192.168.1.2
（与本机同网段）和 192.168.31.228（另一个路由器网段）两个地址，发现列表里两条都在。

日志里能看到这样的序列：
    23:18:09  连接对端 192.168.31.228:39501 …
    23:18:13  连接对端 192.168.1.2:39501 …        ← 3.5 秒后换了地址
    23:18:15  连接对端 192.168.31.228:39501 …     ← 又换回来
    23:18:38  未接受来自 192.168.1.2 的连接：等待对方确认超时

两个症状其实是同一个根因：
    ClientLoop 给**整个握手**只留了 3.5 秒，而握手中间夹着一步"对端弹窗问它
    本机用户要不要接受"（最长 70 秒）。于是对端还在等它用户点确认，我们这边
    3.5 秒就判定失败 → 换下一个候选地址 → 对端又弹一个确认框 → 循环。

修了三处：
    ① 超时拆两段：常规阶段 3.5s，"对方在向它用户征求确认"阶段延长到 80s
       （必须 > 对方协议层 75s > 对方给用户的时间 70s，三方严格递增）
    ② 手填了地址就只用手填的，不再掺入自动发现的结果（原来会被轮换走）
    ③ 对方**明确拒绝**时就停下自动重连，不再一遍遍去敲门（尊重对方的选择）

用例
────────────────────────────────────────────────────────────────
    1. 被控端拒绝后，主控端停止自动重连      → 只拨号 1 次，对端只被问 1 次
    2. 手填地址独占，不被发现结果轮换顶掉    → 所有拨号都打在手填的那个地址上
    3. 对方慢吞吞才点「允许」也能连上        → 主控端耐心等，不中途甩手换地址

用法
────────────────────────────────────────────────────────────────
    python tools/decline_test.py                # 测 net8 构建
    python tools/decline_test.py --tf net48     # 测 Win7/32 位兼容构建
"""
import argparse
import base64
import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import pair_test as pt  # 复用它的 exe_for / BASE_CFG / new_log / read_text / launch

# ─────────────────────────────────────────────────────────────
# DPAPI：config.json 里的 PasswordProt 是 DPAPI(CurrentUser) 加密的，
# 测试要造"两端密码不一致"的场景，就得同样能加密出一个合法值。
# ─────────────────────────────────────────────────────────────


class _Blob(ctypes.Structure):
    _fields_ = [("cbData", wt.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def dpapi_protect(data: bytes) -> str:
    """等价于 C# 的 ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)。"""
    buf = ctypes.create_string_buffer(data, len(data))
    src = _Blob(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))
    dst = _Blob()
    ok = ctypes.windll.crypt32.CryptProtectData(
        ctypes.byref(src), None, None, None, None, 0, ctypes.byref(dst))
    if not ok:
        raise OSError("CryptProtectData failed")
    try:
        raw = ctypes.string_at(dst.pbData, dst.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(dst.pbData)
    return base64.b64encode(raw).decode("ascii")


def make_dirs(tmp, name):
    ds = []
    for tag in ("a", "b"):
        d = os.path.join(tmp, name, tag)
        os.makedirs(os.path.join(d, "logs"), exist_ok=True)
        ds.append(d)
    return ds[0], ds[1]


def write_cfg(d, cfg):
    with open(os.path.join(d, "config.json"), "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)


def wait_all(procs):
    for p in procs:
        try:
            p.wait(timeout=5)
        except Exception:
            pass


def launch_per(exe, items, smoke):
    """每个实例可以有自己的 MB_AUTO_CONFIRM（pair_test.launch 只能给同一个值）。

    items: [(datadir, confirm_value), ...]
    """
    procs = []
    for d, confirm in items:
        env = dict(os.environ, MB_DATA_DIR=d, MB_ALLOW_SELF="1",
                   MB_SMOKE_SECONDS=str(smoke), MB_AUTO_CONFIRM=confirm)
        procs.append(subprocess.Popen(
            [exe, "--smoke"], cwd=os.path.dirname(exe), env=env,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    return procs


DIAL_RE = re.compile(r"连接对端\s+([0-9.]+:\d+)")


def dial_targets(log_text):
    """日志里所有真正拨出去的目标地址（按出现顺序）。"""
    return DIAL_RE.findall(log_text)


# ─────────────────────────────────────────────────────────────
# 用例 0：静态检查三方时长的大小关系
# 运行时测这条要真等 65 秒以上，代价太高；直接读源码常量更确定。
# ─────────────────────────────────────────────────────────────
def case_timeout_constants_ordered():
    name = "0_确认等待时长顺序正确"
    print(f"\n=== 用例：{name} ===")
    root = os.path.join(os.path.dirname(HERE), "MouseBridge")

    orch = pt.read_text(os.path.join(root, "Control", "Orchestrator.cs"))
    link = pt.read_text(os.path.join(root, "Net", "SecureLink.cs"))

    # 被控端给用户考虑的时间：AskUserConfirm 里的 Task.Delay(70000)
    user_ms = None
    m = re.search(r"Task\.Delay\((\d{4,6})\)", orch)
    if m:
        user_ms = int(m.group(1))
    # 协议层等对方确认的上限
    conf_ms = None
    m = re.search(r"ConfirmWaitMs\s*=\s*(\d+)", link)
    if m:
        conf_ms = int(m.group(1))

    print(f"  被控端给用户的时间 = {user_ms} ms")
    print(f"  协议层等待上限     = {conf_ms} ms")

    if user_ms is None or conf_ms is None:
        print("  !! 没能在源码里定位到这两个常量，检查失败")
        return False

    # 必须严格大于，否则出现"用户点了允许、这边却早已判定超时"的怪事
    ok = conf_ms > user_ms
    if not ok:
        print(f"  !! 等待上限 {conf_ms}ms 不大于用户可考虑时间 {user_ms}ms："
              f"用户在这两者之间点「允许」时会被判超时")
    print("  " + ("[PASS] " if ok else "[FAIL] ") + name)
    return ok


# ─────────────────────────────────────────────────────────────
# 用例 1：拒绝后不再反复邀请
# ─────────────────────────────────────────────────────────────
def case_declined_stops_retry(exe, tmp, port_a=39701, port_b=39702):
    name = "1_拒绝后停止自动重连"
    print(f"\n=== 用例：{name} ===")
    dir_a, dir_b = make_dirs(tmp, "decline")

    write_cfg(dir_a, dict(pt.BASE_CFG, Role="server", Port=port_a,
                          PeerHost=f"127.0.0.1:{port_b}", MachineLabel="A-CONTROL"))
    write_cfg(dir_b, dict(pt.BASE_CFG, Role="client", Port=port_b,
                          MachineLabel="B-CONTROLLED"))

    # B 用 deny 模式自动拒绝（不弹窗，但仍完整走 confirm_req/confirm_result 协议）
    pt.kill_all(exe)
    procs = pt.launch(exe, [dir_a, dir_b], smoke=26, confirm="deny")
    try:
        time.sleep(21)
    finally:
        pt.kill_all(exe)
        for p in procs:
            try:
                p.wait(timeout=5)
            except Exception:
                pass

    la = pt.read_text(pt.new_log(dir_a))
    lb = pt.read_text(pt.new_log(dir_b))

    dials = dial_targets(la)
    asked = lb.count("自动拒绝本次连接")
    paused = "暂停自动重连" in la

    print(f"  A 拨号次数              = {len(dials)}  {dials}")
    print(f"  B 被征求确认次数        = {asked}")
    print(f"  A 记录到\"暂停自动重连\"  = {paused}")

    # 修复前：3.5s 超时 + 2.5s 间隔 ≈ 21 秒内会拨 3~4 次，每次都让 B 再问一次。
    # 修复后：只有 1 次拨号，然后原地挂住等用户操作。
    ok = len(dials) == 1
    if not ok:
        print(f"  !! 期望拨号 1 次，实际 {len(dials)} 次 —— 仍在自动重试")
    ok = ok and asked == 1
    if asked > 1:
        print(f"  !! B 被反复征求确认 {asked} 次 —— 确认框刷屏问题仍在")
    ok = ok and paused
    if not paused:
        print("  !! A 没有记录暂停自动重连，闸门可能没生效")

    print("  " + ("[PASS] " if ok else "[FAIL] ") + name)
    if not ok:
        print("  ---- A 日志尾部 ----")
        print("\n".join(la.splitlines()[-14:]))
        print("  ---- B 日志尾部 ----")
        print("\n".join(lb.splitlines()[-10:]))
    return ok


# ─────────────────────────────────────────────────────────────
# 用例 2：手填地址独占
# ─────────────────────────────────────────────────────────────
def case_manual_target_exclusive(exe, tmp, port_a=39711, port_b=39712):
    name = "2_手填地址不被轮换覆盖"
    print(f"\n=== 用例：{name} ===")
    dir_a, dir_b = make_dirs(tmp, "manual")
    manual = f"127.0.0.1:{port_b}"

    # 两端密码设成不一样：认证阶段立刻失败 → 连接循环会不停"换下一个候选再试"，
    # 这样地址轮换才会真的跑起来（这正是修复前暴露问题的方式）。
    # 注意：认证失败发生在确认阶段之前，PeerDeclined 不会被置位，不会触发用例 1 的闸门。
    pw_a = dpapi_protect(b"left-password")
    pw_b = dpapi_protect(b"right-password")

    write_cfg(dir_a, dict(pt.BASE_CFG, Role="server", Port=port_a, PeerHost=manual,
                          AutoDiscover=True, PasswordProt=pw_a, MachineLabel="A-CONTROL"))
    write_cfg(dir_b, dict(pt.BASE_CFG, Role="client", Port=port_b,
                          AutoDiscover=True, PasswordProt=pw_b, MachineLabel="B-CONTROLLED"))

    pt.kill_all(exe)
    procs = pt.launch(exe, [dir_a, dir_b], smoke=22, confirm="1")
    try:
        time.sleep(18)
    finally:
        pt.kill_all(exe)
        for p in procs:
            try:
                p.wait(timeout=5)
            except Exception:
                pass

    la = pt.read_text(pt.new_log(dir_a))
    dials = dial_targets(la)
    distinct = sorted(set(dials))
    discovered = la.count("发现对端")

    print(f"  A 拨号次数            = {len(dials)}")
    print(f"  拨过的不同地址        = {distinct}")
    print(f"  期间发现到的对端条目  = {discovered}")
    print(f"  手填地址              = {manual}")

    if len(dials) == 0:
        print("  !! 一次都没拨出去，用例没起到验证作用")
        print("\n".join(la.splitlines()[-14:]))
        return False

    ok = distinct == [manual]
    if not ok:
        print(f"  !! 拨号地址跑偏到了 {distinct} —— 手填地址没有独占候选列表")
    if discovered == 0:
        print("  （本轮没发现额外对端，用例为弱验证：仅确认手填地址被使用）")

    print("  " + ("[PASS] " if ok else "[FAIL] ") + name)
    if not ok:
        print("  ---- A 日志尾部 ----")
        print("\n".join(la.splitlines()[-16:]))
    return ok


# ─────────────────────────────────────────────────────────────
# 用例 3：对方好好考虑了一阵才点「允许」
# 这才是真正会翻车的路径 —— 立刻作答（allow/deny）几百毫秒就结束了，
# 碰不到"等待窗口够不够长"这件事，所以必须有这个慢用例兜住。
# ─────────────────────────────────────────────────────────────
def case_slow_confirm_still_connects(exe, tmp, port_a=39721, port_b=39722):
    name = "3_对方慢确认也能连上"
    print(f"\n=== 用例：{name} ===")
    dir_a, dir_b = make_dirs(tmp, "slow")
    manual = f"127.0.0.1:{port_b}"
    hesistate_ms = 9000          # 模拟对面这位仁兄盯着确认框看了 9 秒才点允许

    write_cfg(dir_a, dict(pt.BASE_CFG, Role="server", Port=port_a, PeerHost=manual,
                          MachineLabel="A-CONTROL"))
    write_cfg(dir_b, dict(pt.BASE_CFG, Role="client", Port=port_b,
                          MachineLabel="B-CONTROLLED"))

    pt.kill_all(exe)
    procs = launch_per(exe, [(dir_a, "1"), (dir_b, str(hesistate_ms))], smoke=30)
    try:
        time.sleep(20)
    finally:
        pt.kill_all(exe)
        wait_all(procs)

    la = pt.read_text(pt.new_log(dir_a))
    lb = pt.read_text(pt.new_log(dir_b))

    dials = dial_targets(la)
    linked = la.count("已连接对端")

    print(f"  A 拨号次数        = {len(dials)}")
    print(f"  A 成功建链次数    = {linked}")
    print(f"  预期犹豫时长      = {hesistate_ms} ms")

    # 修复前：A 等 3.5 秒就撒手 → 换地址重来 → B 又从头犹豫 9 秒 → A 又撒手，两边永远错开，
    # 结果就是"明明两边都在线、也点了允许，就是连不上"。
    # 修复后：A 把窗口拉到 80 秒，9 秒时 B 回了允许，一次就成了。
    ok = linked == 1 and len(dials) == 1
    if linked == 0:
        print("  !! 一次都没连上 —— 慢确认路径仍然会掉头参见错缝")
    elif len(dials) > 1:
        print(f"  !! 重拨了 {len(dials)} 次才连上 —— 等待窗口还是不够长")

    print("  " + ("[PASS] " if ok else "[FAIL] ") + name)
    if not ok:
        print("  ---- A 日志尾部 ----")
        print("\n".join(la.splitlines()[-14:]))
        print("  ---- B 日志尾部 ----")
        print("\n".join(lb.splitlines()[-10:]))
    return ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tf", default="net8.0-windows")
    args = ap.parse_args()
    exe = pt.exe_for(args.tf)
    print(f"目标框架：{args.tf}\n可执行文件：{exe}")

    tmp = tempfile.mkdtemp(prefix="mb-decline-")
    results = []
    try:
        results.append(case_timeout_constants_ordered())
        results.append(case_declined_stops_retry(exe, tmp))
        results.append(case_manual_target_exclusive(exe, tmp))
        results.append(case_slow_confirm_still_connects(exe, tmp))
    finally:
        pt.kill_all(exe)
        shutil.rmtree(tmp, ignore_errors=True)

    print("\n================ 汇总 ================")
    print(f"通过 {sum(results)}/{len(results)}")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
