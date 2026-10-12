# -*- coding: utf-8 -*-
"""
资源键体检：抓「代码/样式里引用了、但 Theme.xaml 里根本没定义」的 WPF 资源键。

为什么需要它（真实事故）：
    v1.6.5 给设置页加反向控制徽标时，代码里写了
        FindResource("StrokeBrush")
    而 Theme.xaml 里根本没有 StrokeBrush 这个键（正确的叫 LineStrongBrush）。
    因为 WPF 资源键是**运行时**按字符串查的，编译器完全看不见：

        · dotnet build        → 0 错误 0 警告，照样通过
        · --selftest          → 22 项全过（自检不加载设置页）
        · 打包                 → 三个包正常产出
        · 真机一连接          → 界面线程抛 ResourceReferenceKeyNotFoundException，
                                设置页每个刷新周期崩一次，全局异常处理弹窗

    也就是说这条路能一路绿灯走到用户手里才炸。所以补一道静态体检，
    把「引用的键 ⊆ 定义的键」变成可执行断言。改样式/加控件后跑一下就能兜住。

查什么：
    定义方：所有 .xaml 里的 x:Key="X"
    引用方：① .cs 里 FindResource("X") / TryFindResource("X") 的**字符串字面量**
            ② .xaml 里 {StaticResource X} / {DynamicResource X}

不查什么：
    · FindResource(key) 这种传变量的（静态分析看不到值，AskWindow.Res 属于此类，
      但它自带 `is Brush` 类型判断 + 兜底色，找不到也不会崩，所以豁免是安全的）
    · {x:Static}、{Binding}、TemplateBinding、系统主题键

用法：
    python tools/resource_key_check.py           # 体检，有问题退出码 1
    python tools/resource_key_check.py --list    # 顺便列出全部已定义键
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SRC = os.path.join(ROOT, "MouseBridge")

RE_XAML_KEY = re.compile(r'x:Key\s*=\s*"([^"]+)"')
RE_XAML_REF = re.compile(r'\{(?:StaticResource|DynamicResource)\s+([A-Za-z_][\w.]*)\s*\}')

# 认这些"按名字取资源"的函数。除了 WPF 自带的两个，还有本工程自己的薄包装：
#   · SettingsWindow.Res(key) → TryFindResource(key) as Brush（取不到返回 null，配色缺失但不崩）
#   · AskWindow.Res(key, fallbackColor) → 取不到用兜底色
# 包装函数虽然不会崩，但键名写错就会静默用兜底色/不设色，属于"看起来没事其实错了"，
# 所以照样要静态查出来。（曾因为只认 FindResource，漏掉 Res() 里的坏键。）
LOOKUP_FUNCS = ("FindResource", "TryFindResource", "Res")
_F = "|".join(LOOKUP_FUNCS)
RE_CS_REF = re.compile(r'(?<![\w.])(?:' + _F + r')\s*\(\s*"([^"]+)"')
# 三元里的两个字面量：FindResource(x ? "A" : "B") / Res(cond ? "A" : "B")
RE_CS_TERNARY = re.compile(r'(?<![\w.])(?:' + _F + r')\s*\(\s*[^,)]*?\?\s*"([^"]+)"\s*:\s*"([^"]+)"')

# 这些键由 WPF 主题/系统提供，或允许代码兜底，不在 Theme.xaml 里也正常
ALLOW_MISSING = {
    # AskWindow.Res 用变量查表，且带 `is Brush` 判断 + 兜底色；其实参在这里会以字面量形式
    # 出现在调用处（Res("...", color)），并非 FindResource 引用，故不会进来。
}


def walk(ext):
    for base, _dirs, files in os.walk(SRC):
        if os.sep + "obj" + os.sep in base + os.sep or os.sep + "bin" + os.sep in base + os.sep:
            continue
        for f in files:
            if f.lower().endswith(ext):
                yield os.path.join(base, f)


def read(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read()
    except (OSError, UnicodeDecodeError):
        return ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true", help="列出全部已定义的资源键")
    args = ap.parse_args()

    defined = {}          # key -> [定义文件]
    for p in walk(".xaml"):
        for k in RE_XAML_KEY.findall(read(p)):
            defined.setdefault(k, []).append(os.path.relpath(p, ROOT))

    if args.list:
        print("已定义资源键：")
        for k in sorted(defined):
            print(f"  {k}  ({', '.join(defined[k])})")
        print()

    problems = []         # (键, 引用位置)

    for p in walk(".cs"):
        rel = os.path.relpath(p, ROOT)
        text = read(p)
        keys = set(RE_CS_REF.findall(text))
        for a, b in RE_CS_TERNARY.findall(text):
            keys.add(a)
            keys.add(b)
        for k in sorted(keys):
            if k not in defined and k not in ALLOW_MISSING:
                problems.append((k, rel))

    for p in walk(".xaml"):
        rel = os.path.relpath(p, ROOT)
        for k in set(RE_XAML_REF.findall(read(p))):
            if k not in defined and k not in ALLOW_MISSING:
                problems.append((k, rel))

    print(f"已定义资源键 {len(defined)} 个")
    if not problems:
        print("资源键体检通过：所有引用都能找到定义。")
        return 0

    print(f"\n发现 {len(problems)} 处引用了不存在的资源键"
          "（运行时才会抛 ResourceReferenceKeyNotFoundException）：\n")
    seen = set()
    for k, where in problems:
        if (k, where) in seen:
            continue
        seen.add((k, where))
        # 给个最接近的候选，省得再去翻 Theme.xaml
        cand = sorted(defined, key=lambda d: (len(set(d) ^ set(k)), d))[:3]
        print(f"  [FAIL] \"{k}\"  ← {where}")
        print(f"          是不是想写：{', '.join(cand)}？")
    print("\n修复：把键改成上面候选里的正确名字，或到 Ui/Theme.xaml 里补上定义。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
