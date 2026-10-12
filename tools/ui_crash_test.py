# -*- coding: utf-8 -*-
"""
界面崩溃回归测试：真机连线 + **打开设置页**，断言界面线程零异常。

为什么必须单独有它（真实事故）：
    v1.6.5 在设置页里写了不存在的资源键 FindResource("StrokeBrush")。
    因为 WPF 资源键是运行时按字符串查的，编译器完全看不见，所以：

        dotnet build      → 0 错误 0 警告，通过
        --selftest        → 22 项全过（自检根本不创建设置页）
        pair_test         → 6/6（用 --smoke，也不创建设置页）
        reconnect/reverse → 全过（同上）
        用户真机一连接     → 界面线程每个刷新周期抛 ResourceReferenceKeyNotFoundException，
                            全局异常处理再弹一个模态框 → 用户看到的就是"一直弹错误框"

    也就是说前面所有测试都覆盖不到"设置页刷新"这条路径。这个脚本补上它：
    用 MB_SMOKE_SETTINGS=1 让两个实例都开设置页，正常互连，然后查日志里有没有
    "界面线程未处理异常 / 后台线程未处理异常 / 任务未观察异常"。

    注意 MB_NO_UI_POPUP=1：测试里不弹模态框，否则异常弹窗会把进程卡住，
    "真炸了"和"测试超时"就分不清了。

用法：
    python tools/ui_crash_test.py                # 测 net8 构建
    python tools/ui_crash_test.py --tf net48     # 测 net48 构建
"""
import argparse
import os
import sys
import tempfile
import shutil
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pair_test as pt  # 复用配置模板、启动/清理/日志读取

ERROR_MARKERS = (
    "界面线程未处理异常",
    "后台线程未处理异常",
    "任务未观察异常",
)


def run_case(exe, tmp, name, port_a, port_b):
    print(f"\n=== 用例：{name} ===")
    dir_a = os.path.join(tmp, name, "a")
    dir_b = os.path.join(tmp, name, "b")
    for d in (dir_a, dir_b):
        os.makedirs(os.path.join(d, "logs"), exist_ok=True)

    import json
    cfg_a = dict(pt.BASE_CFG, Role="server", Port=port_a, MachineLabel="HAOSTATION")
    cfg_b = dict(pt.BASE_CFG, Role="client", Port=port_b, MachineLabel="DESKTOP-B",
                 PeerHost="127.0.0.1:" + str(port_a), ConfirmOnConnect=False, ServiceOn=True)
    for d, c in ((dir_a, cfg_a), (dir_b, cfg_b)):
        with open(os.path.join(d, "config.json"), "w", encoding="utf-8") as f:
            json.dump(c, f, ensure_ascii=False, indent=2)

    # 两端都开设置页：设置页的 DispatcherTimer 每秒刷新一次，
    # 只要刷新路径上有任何异常，几秒钟内必然出现在日志里。
    # （pair_test.launch 不带自定义环境变量，所以这里自己起进程。）
    extra = {"MB_SMOKE_SETTINGS": "1", "MB_NO_UI_POPUP": "1"}
    procs = []
    for d in (dir_a, dir_b):
        env = dict(os.environ, MB_DATA_DIR=d, MB_ALLOW_SELF="1", MB_SMOKE_SECONDS="22",
                   MB_AUTO_CONFIRM="1", **extra)
        import subprocess
        procs.append(subprocess.Popen([exe, "--smoke"], cwd=os.path.dirname(exe), env=env,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    time.sleep(16)   # 建链 + 设置页刷新十几轮

    pt.kill_all(exe)
    for p in procs:
        try:
            p.terminate()
        except Exception:
            pass

    la = pt.read_text(pt.new_log(dir_a))
    lb = pt.read_text(pt.new_log(dir_b))

    opened = ("设置页已打开" in la) or ("设置页已打开" in lb)
    linked = ("已连接对端" in la) and ("已连接对端" in lb)
    errs = []
    for tag, text in (("A", la), ("B", lb)):
        for i, ln in enumerate(text.splitlines(), 1):
            if any(m in ln for m in ERROR_MARKERS):
                errs.append(f"{tag}: {ln.strip()}")

    print(f"  设置页已打开：{'是' if opened else '否'}")
    print(f"  已建链：{'是' if linked else '否'}")
    print(f"  界面异常条数：{len(errs)}")

    ok = opened and linked and not errs
    print("  " + ("[PASS] " if ok else "[FAIL] ") + name)
    if errs:
        print("  ---- 异常明细（最多 8 条）----")
        for e in errs[:8]:
            print("   ", e)
    if not ok and not errs:
        print("  ---- A 日志尾部 ----")
        print("\n".join(la.splitlines()[-12:]))
    return ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tf", default="net8.0-windows")
    args = ap.parse_args()
    exe = pt.exe_for(args.tf)
    print(f"目标框架：{args.tf}\n可执行文件：{exe}")
    pt.kill_all(exe)

    tmp = tempfile.mkdtemp(prefix="mb-uicrash-")
    results = []
    try:
        results.append(run_case(exe, tmp, "1_一主一从_开设置页", 39401, 39402))
    finally:
        pt.kill_all(exe)
        shutil.rmtree(tmp, ignore_errors=True)

    print("\n================ 汇总 ================")
    print(f"通过 {sum(results)}/{len(results)}")
    sys.exit(0 if all(results) else 1)


if __name__ == "__main__":
    main()
