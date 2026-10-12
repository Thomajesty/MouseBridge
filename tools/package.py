# -*- coding: utf-8 -*-
"""打包 MouseBridge，产出三个交付包 + md5。

三个包各自的作用：
  1. MouseBridge-<版本>-win-x64.exe      自包含单文件（含 .NET 8 运行时，约 65MB，Win10/11 免安装）
  2. MouseBridge-<版本>-net8-x64.exe     框架依赖（约 2MB，需已装 .NET 8 桌面运行时）
  3. MouseBridge-<版本>-win7-32bit.zip   .NET Framework 4.8 版（Win7 SP1 / 32 位系统可用，
                                         exe + 几个 dll 的文件夹，目标机需有 .NET Framework 4.8）

为什么要第三个包：.NET 8 从 .NET 6 起就把最低系统要求提到 Win10 1607，在 Win7 上
根本起不来（不是配置问题）。要让 Win7 和 32 位系统能用，只能降到 .NET Framework 4.8
（微软最后一个支持 Win7 SP1 的版本）。两个目标共用同一套源码，差异收敛在 Core/Compat.cs。

用法：python tools/package.py
"""
import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PROJ = os.path.join(ROOT, "MouseBridge")
DIST = os.path.join(ROOT, "dist")
PUB = os.path.join(ROOT, "build", "publish")

NET8 = "net8.0-windows"
NET48 = "net48"

README_NET48 = """\
鼠下快传 MouseBridge v{ver} —— Windows 7 / 32 位兼容版
=========================================================
运行要求
  · Microsoft .NET Framework 4.8
      - Windows 10 1903 及以上：系统已内置，直接可用
      - Windows 7 SP1 / 8 / 8.1：需要先安装一次（离线安装包约 110MB）
        https://dotnet.microsoft.com/download/dotnet-framework/net48

使用步骤
  1. 把【整个文件夹】拷到目标电脑的任意目录 —— 不要只拷 MouseBridge.exe，
     同目录的那些 dll 是运行必需的；
  2. 双击 MouseBridge.exe 即可，绿色免安装（不写注册表、不装服务）；
  3. 首次运行如果弹出 Windows 防火墙授权，请点「允许」（局域网连接要用）；
  4. 两台电脑都装好后，在任意一台的「连接」页填上对方的 IP，点「连接」就通了。

关于这一版
  · 本版是 AnyCPU：32 位系统上自动以 32 位运行，64 位系统上以 64 位运行，一个包通吃；
  · 界面图标全部用矢量路径绘制，不依赖 Segoe Fluent/MDL2 字体
    （Win7 没有这些字体，用字体字形会显示成方框）；
  · Win7 的 SChannel 要支持 TLS 1.2 需要系统补丁 KB3140245。如果开着 TLS 连不上，
    在「连接」页把「TLS 加密」取消勾选即可 —— 身份验证仍然由连接密码保证。

和 Win10/11 版的区别
  · Win10/11 请优先用 MouseBridge-{ver}-win-x64.exe（自包含单文件，什么都不用装）；
  · 本版必须依赖 .NET Framework 4.8；功能与界面完全一致。
"""


def version():
    s = open(os.path.join(PROJ, "Core", "AppInfo.cs"), encoding="utf-8").read()
    m = re.search(r'Version\s*=\s*"([^"]+)"', s)
    if not m:
        raise SystemExit("读不到版本号")
    return m.group(1)


def run(args, cwd, quiet=False):
    if not quiet:
        print("  $", " ".join(args))
    r = subprocess.run(args, cwd=cwd, text=True, capture_output=True)
    if r.returncode != 0:
        print(r.stdout[-4000:])
        print(r.stderr[-4000:])
        raise SystemExit(f"命令失败：{args[0]}")
    return r.stdout


def publish_net8(self_contained, outdir):
    if os.path.isdir(outdir):
        shutil.rmtree(outdir, ignore_errors=True)
    args = ["dotnet", "publish", "-c", "Release", "-f", NET8, "-r", "win-x64", "-o", outdir,
            "-v", "q", "--nologo", "--self-contained", "true" if self_contained else "false"]
    if self_contained:
        args += ["-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                 "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none"]
    else:
        args += ["-p:PublishSingleFile=true", "-p:DebugType=none"]
    run(args, PROJ)
    exes = [f for f in os.listdir(outdir) if f.lower().endswith(".exe")]
    if not exes:
        raise SystemExit("没有产出 exe：" + outdir)
    return os.path.join(outdir, exes[0])


def publish_net48(outdir):
    """net48 不支持单文件发布，产出的是 exe + 若干 dll 的文件夹。"""
    if os.path.isdir(outdir):
        shutil.rmtree(outdir, ignore_errors=True)
    run(["dotnet", "publish", "-c", "Release", "-f", NET48, "-o", outdir,
         "-v", "q", "--nologo"], PROJ)
    exe = os.path.join(outdir, "MouseBridge.exe")
    if not os.path.isfile(exe):
        raise SystemExit("没有产出 net48 的 MouseBridge.exe：" + outdir)
    return outdir


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def safe_replace(src, dst):
    """把 src 覆盖到 dst，目标被占用时自动绕过。

    为什么需要：dist 下的成品 exe 经常被别的进程以"读"方式打开 —— 杀毒软件实时扫描、
    资源管理器缩略图、或某个预览面板持有句柄。此时直接 shutil.copy2 会抛
    PermissionError(WinError 32) 并**中断整个打包**，前面两个包白打了。

    Windows 有个特性：**被打开的文件通常可以重命名，只是不能覆盖写**。
    所以这里退化成"先把旧文件改名让位，再拷新的，最后尽力删掉旧文件"。
    删不掉也不要紧（下次打包会被再覆盖），绝不能因此让打包失败。
    """
    try:
        shutil.copy2(src, dst)
        return
    except PermissionError:
        pass
    stale = dst + ".old"
    n = 1
    while os.path.exists(stale):
        stale = f"{dst}.old{n}"
        n += 1
    os.rename(dst, stale)          # 被占用的文件改名通常能成功
    shutil.copy2(src, dst)
    try:
        os.remove(stale)
    except OSError as e:
        print(f"  · 旧文件暂被占用，已让位为 {os.path.basename(stale)}（可稍后手动删除）")


def zip_dir(srcdir, zippath):
    if os.path.exists(zippath):
        os.remove(zippath)
    with zipfile.ZipFile(zippath, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name in sorted(os.listdir(srcdir)):
            full = os.path.join(srcdir, name)
            if os.path.isfile(full):
                # 守卫：包内文件名必须全 ASCII。
                # 中文名会带 UTF-8 文件名标志位，Win7 资源管理器不认 → 解压成乱码，
                # 用户会以为"包坏了"。这条守卫让将来误加中文名在打包阶段就报错，
                # 而不是等用户下载后发现乱码。
                if not all(ord(c) < 128 for c in name):
                    raise SystemExit(
                        f"包内文件名必须全部为 ASCII（否则 Win7 解压乱码），但发现：{name!r}\n"
                        f"请改成英文名后重新打包。")
                z.write(full, name)
    return zippath


def main():
    v = version()
    os.makedirs(DIST, exist_ok=True)
    print(f"鼠下快传 MouseBridge v{v} 打包")

    print("[1/3] 自包含单文件版（Win10/11，免安装）…")
    exe_full = publish_net8(True, os.path.join(PUB, "full"))
    dst_full = os.path.join(DIST, f"MouseBridge-{v}-win-x64.exe")
    safe_replace(exe_full, dst_full)

    print("[2/3] 框架依赖轻量版（需 .NET 8 桌面运行时）…")
    exe_lite = publish_net8(False, os.path.join(PUB, "lite"))
    dst_lite = os.path.join(DIST, f"MouseBridge-{v}-net8-x64.exe")
    safe_replace(exe_lite, dst_lite)

    print("[3/3] Win7 / 32 位兼容版（.NET Framework 4.8）…")
    d48 = os.path.join(PUB, "net48")
    publish_net48(d48)
    # 说明文件必须用 **ASCII 文件名**（ReadMe.txt），不能用「使用说明.txt」。
    #
    # 原因：zip 规范里中文名会带上 UTF-8 文件名标志位（flag bit 11）。Win10/11 认这个标志，
    # 但 **Win7 的资源管理器不认**，解压出来会变成 `õ¢+þö¿޻¦µÄ.txt` 这种乱码 ——
    # 用户看到一堆乱码文件名，第一反应是"包坏了/版本不对"。这正是 Win7 兼容包最不该出的问题。
    # 内容仍是中文（utf-8-sig 带 BOM，Win7 记事本能正确显示），只把**文件名**改成 ASCII。
    with open(os.path.join(d48, "ReadMe.txt"), "w", encoding="utf-8-sig") as f:
        f.write(README_NET48.format(ver=v))
    dst_zip = os.path.join(DIST, f"MouseBridge-{v}-win7-32bit.zip")
    zip_dir(d48, dst_zip)

    # 兼容旧习惯：dist/MouseBridge.exe 始终指向自包含版
    legacy = os.path.join(DIST, "MouseBridge.exe")
    safe_replace(dst_full, legacy)

    lines = []
    for p in (dst_full, dst_lite, dst_zip, legacy):
        m = md5(p)
        size = os.path.getsize(p) / 1048576.0
        lines.append(f"{m}  {os.path.basename(p)}  ({size:.1f} MB)")
        print(f"  {os.path.basename(p):<38} {size:6.1f} MB  {m}")

    with open(os.path.join(DIST, "md5.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print("-> dist/md5.txt")
    return 0


if __name__ == "__main__":
    sys.exit(main())
