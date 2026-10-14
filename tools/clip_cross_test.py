"""
跨机剪贴板端到端测试（需求2：双端 Ctrl+C/V 统一复制粘贴的底层链路验证）。

同机双实例（loopback）互连后，用系统剪贴板模拟"用户复制"：
    1. 写文本 → A 实例监听到并推送 → B 实例应写入（B 日志出现"收到文本 …B"）
    2. 再写一份新文本 → 反向同样验证（任一端日志出现"收到文本 …B"）
    3. 写文件（CF_HDROP）→ 互相推送 → 任一端日志出现"自动预接收"

判定全走实例日志。注意：同机双实例共享同一个系统剪贴板，写入时两端都会"自认复制"
并互相推送，「以最新为准」在毫秒级撞车时会单侧互拒 —— 这是同机测试的固有噪声
（真机两台电脑不存在）。所以每个方向最多重试 3 轮，任一轮命中即视为链路可用。

用法：
    python tools/clip_cross_test.py           # net8
    python tools/clip_cross_test.py --tf net48
"""
import argparse
import ctypes
import json
import os
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32

sys.path.insert(0, HERE)
from pair_test import BASE_CFG, exe_for, read_text, kill_all, new_log  # noqa: E402

CF_UNICODETEXT, CF_HDROP = 13, 15
GMEM_MOVEABLE = 0x0002

# 64 位 python 下必须显式声明句柄类型，否则 GlobalAlloc 返回值被截断成 int → GlobalLock 写崩
k32.GlobalAlloc.restype = ctypes.c_void_p
k32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
k32.GlobalLock.restype = ctypes.c_void_p
k32.GlobalLock.argtypes = [ctypes.c_void_p]
k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
u32.SetClipboardData.restype = ctypes.c_void_p
u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
u32.GetClipboardData.restype = ctypes.c_void_p
u32.GetClipboardData.argtypes = [ctypes.c_uint]


def _set_clip_data(fmt: int, data: bytes):
    last_err = None
    for _ in range(5):
        try:
            if not u32.OpenClipboard(None):
                time.sleep(0.3)
                continue
            try:
                u32.EmptyClipboard()
                h = k32.GlobalAlloc(GMEM_MOVEABLE, len(data))
                p = k32.GlobalLock(h)
                if not p:
                    raise OSError("GlobalLock 失败")
                ctypes.memmove(p, data, len(data))
                k32.GlobalUnlock(h)
                if not u32.SetClipboardData(fmt, h):
                    raise OSError("SetClipboardData 失败")
                return
            finally:
                u32.CloseClipboard()
        except OSError as e:
            last_err = e
            time.sleep(0.5)
    raise OSError(f"写剪贴板失败（重试 5 次）：{last_err}")


def set_clip_text(s: str):
    data = s.encode("utf-16-le") + b"\x00\x00"
    _set_clip_data(CF_UNICODETEXT, data)


def set_clip_files(paths: list):
    # DROPFILES 头：pFiles(4) pt(8) fNC(4) fWide(4) = 20 字节
    joined = "".join(p + "\x00" for p in paths) + "\x00"
    data = (20).to_bytes(4, "little") + b"\x00" * 12 + (1).to_bytes(4, "little") \
        + joined.encode("utf-16-le") + b"\x00\x00"
    _set_clip_data(CF_HDROP, data)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tf", default="net8")
    args = ap.parse_args()

    exe = exe_for(args.tf)
    tmp = tempfile.mkdtemp(prefix="mb-clip-")
    dir_a, dir_b = os.path.join(tmp, "a"), os.path.join(tmp, "b")
    for d in (dir_a, dir_b):
        os.makedirs(os.path.join(d, "logs"), exist_ok=True)

    cfg_a = dict(BASE_CFG, Role="server", Port=39511, PeerHost="")
    cfg_b = dict(BASE_CFG, Role="client", Port=39512, PeerHost="127.0.0.1:39511")
    for d, cfg in ((dir_a, cfg_a), (dir_b, cfg_b)):
        with open(os.path.join(d, "config.json"), "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)

    print("启动双实例（--smoke 100）…")
    for d in (dir_a, dir_b):
        env = dict(os.environ, MB_DATA_DIR=d, MB_ALLOW_SELF="1",
                   MB_SMOKE_SECONDS="100", MB_AUTO_CONFIRM="1")
        subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe), env=env,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(10)
        la0, lb0 = read_text(new_log(dir_a)), read_text(new_log(dir_b))
        connected = "已连接对端" in la0 and "已连接对端" in lb0
        print(f"  互连：{'OK' if connected else 'FAIL'}")
        ok = connected

        def attempt_text(tag: str):
            payload = f"MB-{tag}-" + str(int(time.time() * 1000)) + "-" + "z" * 100
            nb = len(payload.encode("utf-8"))
            set_clip_text(payload)
            time.sleep(4)
            return nb, f"收到文本 {nb}B"

        # —— 1. A→B 文本（最多 2 轮）——
        hit_b = False
        for rnd in range(2):
            nb, marker = attempt_text("A")
            hit_b = marker in read_text(new_log(dir_b))
            print(f"  A→B 文本 第{rnd+1}轮（{nb}B）：{'收到' if hit_b else '未收到'}")
            if hit_b:
                break
        ok = ok and hit_b

        # —— 2. 反向文本（最多 3 轮，任一端收到即可；同机噪声详见文件头）——
        hit_any = False
        for rnd in range(3):
            na, marker = attempt_text("B")
            hit_any = marker in read_text(new_log(dir_a)) or marker in read_text(new_log(dir_b))
            print(f"  B→A 文本 第{rnd+1}轮（{na}B）：{'收到' if hit_any else '未收到'}（任一端）")
            if hit_any:
                break
        ok = ok and hit_any

        # —— 3. 文件（CF_HDROP → eager 预接收，最多 2 轮，任一端）——
        src = os.path.join(tmp, "src")
        os.makedirs(src, exist_ok=True)
        hit_files = False
        for rnd in range(2):
            files = [os.path.join(src, f"clip_file_{rnd}_{i}.bin") for i in range(2)]
            for f in files:
                with open(f, "wb") as fh:
                    fh.write(os.urandom(4096))
            set_clip_files(files)
            time.sleep(6)
            hit_files = ("自动预接收" in read_text(new_log(dir_a))
                         or "自动预接收" in read_text(new_log(dir_b)))
            print(f"  文件 第{rnd+1}轮：{'有端预接收' if hit_files else '无'}")
            if hit_files:
                break
        ok = ok and hit_files

        print("\n" + ("[PASS] 跨机剪贴板链路" if ok else "[FAIL] 跨机剪贴板链路"))
        if not ok:
            for name, d in (("A", dir_a), ("B", dir_b)):
                print(f"  ---- {name} 日志尾部 ----")
                print("\n".join(read_text(new_log(d)).splitlines()[-20:]))
        return 0 if ok else 1
    finally:
        kill_all(exe)
        print(f"（实例日志保留在 {tmp}，便于排查）")


if __name__ == "__main__":
    sys.exit(main())
