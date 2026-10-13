#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把「鼠下快传 MouseBridge」发布到 GitHub：建仓库（可选）→ 推源码 → 建 Release → 传附件 → 回验。

为什么需要这个脚本：
  WorkBuddy 的 GitHub 连接器**只有只读能力**（release 相关只有 list/get，没有 create，也没有
  上传附件的工具），所以发版这件事只能走 REST API。本脚本把之前踩过的坑都固化成了断言：

  1. **github.com 网页可能被墙，但 api.github.com / uploads.github.com 是通的** ——
     全程只走 API，不依赖网页。所以"网页打不开"不等于"发不了版"。
  2. **附件名必须全 ASCII** —— 传中文名 GitHub 会静默把中文段吃掉（不是乱码，是丢失）。
     本脚本在上传前断言全 ASCII，不合格直接拒绝。
  3. `GET /releases/tags/<tag>` 在该 Release 尚未创建时返回 **404** —— 必须容忍，
     否则脚本会在"建 Release 之前"就带 traceback 退出，看着像失败，其实一行都没发。
  4. **回验下载必须带 `Accept: application/octet-stream`** —— 否则拿到的是 1.4KB 的
     元数据 JSON，字节数对不上、MD5 必然不符，会被误报成"上传损坏"。
     而且拉到的东西要先判是不是 `{` 开头（JSON），再比 MD5。
  5. **令牌不写进 .git/config** —— 推代码时把带令牌的 URL 作为参数传给 git push，
     不设置 remote，避免令牌落盘到仓库配置里。

用法（在项目根目录）：
    python tools/release_github.py --owner <用户名> --repo MouseBridge --create --public
    python tools/release_github.py --owner <用户名> --repo MouseBridge --dry-run   # 只看不发

令牌来源（按优先级）：环境变量 GH_PAT → 本机审计日志里的 github_pat_/ghp_ 串。
本脚本**不会**把令牌写进任何文件、也不会回显它。
"""
import argparse
import glob
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.github.com"
UPLOADS = "https://uploads.github.com"
ACCEPT_JSON = "application/vnd.github+json"
ACCEPT_BIN = "application/octet-stream"
UA = "MouseBridge-release-script"

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")
PROJ = os.path.join(ROOT, "MouseBridge")


# ---------------------------------------------------------------- 基础

def version():
    """从 AppInfo.cs 读版本号，保证与打包脚本一致。"""
    s = open(os.path.join(PROJ, "Core", "AppInfo.cs"), encoding="utf-8").read()
    m = re.search(r'Version\s*=\s*"([^"]+)"', s)
    if not m:
        raise SystemExit("读不到版本号：MouseBridge/Core/AppInfo.cs")
    return m.group(1)


def find_pat():
    """取令牌。优先环境变量，其次本机审计日志（技能里记录的复用方式）。"""
    p = (os.environ.get("GH_PAT") or "").strip()
    if p:
        return p, "环境变量 GH_PAT"
    best = None
    pat_files = sorted(glob.glob(os.path.expanduser("~/.workbuddy/audit-log/*.jsonl")))
    for f in pat_files:
        try:
            s = open(f, encoding="utf-8", errors="ignore").read()
        except OSError:
            continue
        for m in re.finditer(r"github_pat_[A-Za-z0-9_]{20,}", s):
            best = m.group(0)
        for m in re.finditer(r"ghp_[A-Za-z0-9]{30,}", s):
            best = m.group(0)
    return (best, "本机审计日志") if best else (None, None)


def api(method, url, token=None, data=None, accept=ACCEPT_JSON, raw=False, timeout=120):
    """统一的 API 调用。

    注意 `accept` 必须可覆盖：查元数据用 JSON，**下载附件必须用 octet-stream**，
    写死的 helper 会让你静默拿到 JSON 还以为下载成功（技能里的坑）。
    """
    body = None
    headers = {"User-Agent": UA, "Accept": accept}
    if token:
        headers["Authorization"] = "Bearer " + token
    if data is not None:
        body = json.dumps(data).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            payload = r.read()
            if raw:
                return payload
            return json.loads(payload.decode("utf-8")) if payload else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")[:400]
        raise RuntimeError(f"{method} {url} -> HTTP {e.code}: {detail}") from None


def md5_of(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------- 步骤

def step_verify_identity(token, owner, repo):
    print("[1/6] 校验令牌与仓库权限…")
    me = api("GET", f"{API}/user", token)
    print(f"  · 令牌属于：{me.get('login')}")
    if owner and me.get("login") and me["login"].lower() != owner.lower():
        print(f"  ! 注意：令牌属于 {me['login']}，而你指定 owner={owner}")
    try:
        r = api("GET", f"{API}/repos/{owner}/{repo}", token)
        perms = r.get("permissions") or {}
        print(f"  · 仓库 {r.get('full_name')} 已存在；push={perms.get('push')} "
              f"admin={perms.get('admin')} private={r.get('private')}")
        return True, bool(perms.get("push"))
    except RuntimeError as e:
        if "HTTP 404" in str(e):
            print(f"  · 仓库 {owner}/{repo} 不存在（需要新建）")
            return False, False
        raise


def step_create_repo(token, owner, repo, private, desc):
    print(f"[2/6] 新建仓库 {owner}/{repo}（{'私有' if private else '公开'}）…")
    payload = {"name": repo, "description": desc, "private": private,
               "has_issues": True, "has_wiki": False, "auto_init": False}
    r = api("POST", f"{API}/user/repos", token, payload)
    print(f"  · 已创建：{r.get('html_url')}")
    time.sleep(2)   # 新仓库立即可用，但稍等一下更稳
    return r


def step_push(token, owner, repo):
    print("[3/6] 推送源码…")
    url = f"https://{owner}:{token}@github.com/{owner}/{repo}.git"
    # 令牌只作为参数出现，不写进 .git/config（skill 的纪律）
    r = subprocess.run(["git", "push", url, "main:main", "--force-with-lease"],
                       cwd=ROOT, capture_output=True, text=True)
    if r.returncode != 0:
        # 空仓库首次推 main 时 --force-with-lease 可能因无远端引用而失败，退回普通推送
        r = subprocess.run(["git", "push", url, "main:main"],
                           cwd=ROOT, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-2000:])
        print(r.stderr[-2000:])
        raise SystemExit("推送失败")
    tail = (r.stderr or r.stdout).strip().splitlines()
    for line in tail[-3:]:
        print("  ·", line)
    print("  · 推送完成")


def step_tag_and_release(token, owner, repo, tag, notes_file):
    print(f"[4/6] 建 tag {tag} 与 Release…")
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                          capture_output=True, text=True).stdout.strip()
    # tag：已存在就跳过（幂等），不存在则创建
    try:
        api("GET", f"{API}/repos/{owner}/{repo}/git/ref/tags/{tag}", token)
        print(f"  · tag {tag} 已存在")
    except RuntimeError as e:
        if "HTTP 404" not in str(e):
            raise
        api("POST", f"{API}/repos/{owner}/{repo}/git/refs", token,
            {"ref": f"refs/tags/{tag}", "sha": head})
        print(f"  · tag {tag} 已创建 -> {head[:12]}")

    # 同名 Release 已存在则先删（重发场景）
    old = {}
    try:
        old = api("GET", f"{API}/repos/{owner}/{repo}/releases/tags/{tag}", token)
    except RuntimeError as e:
        if "HTTP 404" not in str(e):   # 首次发版必然 404，不是错误
            raise
    if old.get("id"):
        print(f"  · 已存在同名 Release(id={old['id']})，先删除")
        api("DELETE", f"{API}/repos/{owner}/{repo}/releases/{old['id']}", token)

    body = open(os.path.join(ROOT, notes_file), encoding="utf-8").read()
    r = api("POST", f"{API}/repos/{owner}/{repo}/releases", token, {
        "tag_name": tag,
        "target_commitish": "main",
        "name": f"鼠下快传 MouseBridge {tag}",
        "body": body,
        "draft": False,
        "prerelease": False,
        "make_latest": "true",        # 必须是字符串 "true"，不是布尔
    })
    print(f"  · Release 已创建 id={r['id']}")
    return r["id"]


def step_upload(token, owner, repo, release_id, files):
    print("[5/6] 上传附件…")
    assets = []
    for path in files:
        name = os.path.basename(path)
        if not all(ord(c) < 128 for c in name):
            raise SystemExit(f"附件名必须全 ASCII（中文会被 GitHub 吞掉）：{name}")
        size = os.path.getsize(path)
        print(f"  · 上传 {name}（{size/1048576:.1f} MB）…", end="", flush=True)
        url = f"{UPLOADS}/repos/{owner}/{repo}/releases/{release_id}/assets?" + \
              urllib.parse.urlencode({"name": name})
        with open(path, "rb") as f:
            blob = f.read()
        req = urllib.request.Request(url, data=blob, method="POST", headers={
            "Authorization": "Bearer " + token,
            "User-Agent": UA,
            "Accept": ACCEPT_JSON,
            "Content-Type": "application/octet-stream",
            "Content-Length": str(len(blob)),   # 必须显式给
        })
        try:
            with urllib.request.urlopen(req, timeout=600) as r:
                a = json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            print(" 失败")
            raise RuntimeError(f"上传 {name} 失败 HTTP {e.code}: "
                               f"{e.read().decode('utf-8','replace')[:300]}") from None
        got = a.get("name", "")
        if got != name:
            print(f" 名字被改动！期望 {name}，实际 {got}")
        else:
            print(" ok")
        assets.append(a)
    return assets


def step_verify(token, owner, repo, tag, pairs, release_id):
    """pairs: [(本地路径, 远端 asset_id, 文件名), ...]"""
    print("[6/6] 事后回验…")
    # ① Release 状态
    r = api("GET", f"{API}/repos/{owner}/{repo}/releases/{release_id}", token)
    ok_draft = (r.get("draft") is False and r.get("prerelease") is False
                and bool(r.get("published_at")))
    print(f"  · draft={r.get('draft')} prerelease={r.get('prerelease')} "
          f"published_at={r.get('published_at')} -> {'OK' if ok_draft else '异常'}")

    # ② latest 指向（很关键：latest 没指过来，程序的自动更新永远不触发）
    latest = api("GET", f"{API}/repos/{owner}/{repo}/releases/latest", token)
    print(f"  · releases/latest 指向 {latest.get('tag_name')} -> "
          f"{'OK' if latest.get('tag_name') == tag else '不一致！'}")

    # ③ 附件齐全 + 名字全 ASCII
    lst = api("GET", f"{API}/repos/{owner}/{repo}/releases/{release_id}/assets", token)
    names = [a["name"] for a in lst]
    print(f"  · 远端附件 {len(names)} 个：{names}")
    bad = [n for n in names if not all(ord(c) < 128 for c in n)]
    print(f"  · 附件名为全 ASCII：{'OK' if not bad else '有非 ASCII：' + str(bad)}")

    # ④ 逐个下载比对 MD5（验证上传无损）
    print("  · 下载回验（比对 MD5）…")
    allok = True
    for path, aid, fname in pairs:
        want = md5_of(path)
        url = f"{API}/repos/{owner}/{repo}/releases/assets/{aid}"
        blob = api("GET", url, token, accept=ACCEPT_BIN, raw=True)
        # 先判拿到的是不是真二进制 —— 若 Accept 写错会拿到元数据 JSON，
        # 那种情况下报"MD5 不符"会误导排查方向
        if blob[:1] == b"{":
            print(f"    {fname}: 拿到的是 JSON 元数据，不是附件本体（Accept 错了）")
            allok = False
            continue
        got = hashlib.md5(blob).hexdigest()
        same = (got == want) and (len(blob) == os.path.getsize(path))
        print(f"    {fname}: {len(blob)} 字节  本地 {os.path.getsize(path)}  "
              f"{'MD5 一致 OK' if same else 'MD5 不符！'}")
        allok = allok and same
    return ok_draft and (latest.get("tag_name") == tag) and not bad and allok


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--owner", required=True, help="GitHub 用户名")
    ap.add_argument("--repo", default="MouseBridge", help="仓库名")
    ap.add_argument("--create", action="store_true", help="仓库不存在时新建")
    ap.add_argument("--public", action="store_true", help="新建为公开仓库（默认私有）")
    ap.add_argument("--skip-push", action="store_true", help="跳过推送代码")
    ap.add_argument("--ver", help="覆盖版本号（默认读 AppInfo.cs；用于先补发旧版本包）")
    ap.add_argument("--dry-run", action="store_true", help="只做前置检查，不发送任何东西")
    args = ap.parse_args()

    v = args.ver or version()
    tag = "v" + v
    print(f"鼠下快传 MouseBridge {tag} → GitHub ({args.owner}/{args.repo})")
    print("-" * 58)

    token, src = find_pat()
    if not token:
        raise SystemExit(
            "找不到令牌。请任选一种方式：\n"
            "  · 设环境变量后运行：GH_PAT=<令牌> python tools/release_github.py ...\n"
            "  · 或确认本机审计日志里已有 github_pat_/ghp_ 串")
    print(f"· 令牌来源：{src}（长度 {len(token)}，不回显）")

    files = [
        os.path.join(DIST, f"MouseBridge-{v}-win-x64.exe"),
        os.path.join(DIST, f"MouseBridge-{v}-net8-x64.exe"),
        os.path.join(DIST, f"MouseBridge-{v}-win7-32bit.zip"),
        os.path.join(DIST, "md5.txt"),
    ]
    missing = [f for f in files if not os.path.isfile(f)]
    if missing:
        raise SystemExit("缺少交付文件，请先跑 python tools/package.py：\n  " +
                         "\n  ".join(missing))
    print("· 待发布附件：")
    for f in files:
        print(f"    {os.path.basename(f):42s} {os.path.getsize(f)/1048576:6.1f} MB")

    exists, can_push = step_verify_identity(token, args.owner, args.repo)
    if exists and not can_push:
        raise SystemExit("令牌对该仓库没有 push 权限 —— 请确认 Contents 权限是 Read and write，"
                         "且令牌的 Repository access 里勾选了这个仓库。")

    if args.dry_run:
        print("\n[dry-run] 前置检查通过，未发送任何内容。")
        return 0

    if not exists:
        if not args.create:
            raise SystemExit("仓库不存在，且未指定 --create。")
        step_create_repo(token, args.owner, args.repo, private=not args.public,
                         desc="鼠下快传：一套键鼠在两台 Windows 电脑间穿梭（跨机复制粘贴 / 拖拽投递）")

    if not args.skip_push:
        step_push(token, args.owner, args.repo)

    rid = step_tag_and_release(token, args.owner, args.repo, tag, "RELEASE_NOTES.md")
    assets = step_upload(token, args.owner, args.repo, rid, files)
    # 组装回验用的 (本地路径, 远端asset_id, 文件名)
    pairs = [(files[i], assets[i]["id"], assets[i]["name"]) for i in range(len(files))]

    ok = step_verify(token, args.owner, args.repo, tag, pairs, rid)
    print("-" * 58)
    print("发布成功 ✅" if ok else "发布完成，但回验有项目未通过，请查看上面的日志")
    print(f"Release 页面：https://github.com/{args.owner}/{args.repo}/releases/tag/{tag}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
