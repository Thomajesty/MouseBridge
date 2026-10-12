using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using MouseBridge.Core;

namespace MouseBridge.Files;

/// <summary>与资源管理器交互：取前台窗口选中项（拖拽投递的源）、解析光标所在文件夹（拖拽投递的目标）。</summary>
public static class ExplorerHelper
{
    static object? _shell;
    static Type? _shellType;
    static readonly object Gate = new();

    /// <summary>前台窗口是否是资源管理器窗口或桌面。</summary>
    public static bool IsExplorerForeground() => Input.Native.IsShellForeground();

    // ---------------- 光标下的文件夹（拖拽投递目标） ----------------

    public sealed record DropTarget(string Dir, bool IsRealFolder, string Label);

    /// <summary>
    /// 解析"鼠标当前所在位置对应哪个文件夹"：
    /// 资源管理器窗口 → 该窗口打开的目录；桌面 → 桌面目录；其他窗口 → null。
    /// 这是"像同一台电脑两个文件夹窗口之间拖动"的关键：松手时落到光标下的文件夹。
    /// </summary>
    public static string? FolderUnderCursor()
    {
        try
        {
            Input.Native.GetCursorPos(out var p);
            var child = Input.Native.WindowFromPoint(p);
            if (child == IntPtr.Zero) return null;
            var root = Input.Native.GetAncestor(child, Input.Native.GA_ROOT);
            if (root == IntPtr.Zero) root = child;
            var cls = Input.Native.ClassNameOf(root);
            Log.Debug($"光标下窗口 class={cls} hwnd=0x{root.ToInt64():X}");
            if (cls is "CabinetWClass" or "ExploreWClass") return FolderOfExplorerWindow(root);
            if (cls is "Progman" or "WorkerW") return DesktopDir();
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug("解析光标下文件夹失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>按配置解析出本次投递的落地目标（无法解析时回退到默认目录）。</summary>
    public static DropTarget ResolveDropTarget(Config cfg)
    {
        var real = FolderUnderCursor();
        if (!string.IsNullOrEmpty(real))
        {
            string label;
            try { label = new DirectoryInfo(real!).Name; } catch { label = real!; }
            if (string.IsNullOrEmpty(label)) label = real!;   // 盘符根目录
            return new DropTarget(real!, true, label);
        }

        var def = ResolveDropDir(cfg);
        string dl;
        try { dl = new DirectoryInfo(def).Name; } catch { dl = def; }
        if (string.IsNullOrEmpty(dl)) dl = def;
        return new DropTarget(def, false, dl);
    }

    /// <summary>取某个资源管理器窗口当前打开的目录。</summary>
    public static string? FolderOfExplorerWindow(IntPtr hwnd)
    {
        try
        {
            dynamic? shell = ShellInstance();
            if (shell == null) return null;
            foreach (var w in (IEnumerable)shell.Windows())
            {
                try
                {
                    long h = 0;
                    try { h = Convert.ToInt64(((dynamic)w).HWND); } catch { continue; }
                    if (h != hwnd.ToInt64()) continue;
                    var doc = ((dynamic)w).Document;
                    var folder = doc?.Folder;
                    var self = folder?.Self;
                    string? path = self?.Path;
                    if (!string.IsNullOrEmpty(path)) return path;
                    // 特殊文件夹（此电脑 / 回收站）没有文件系统路径
                    return null;
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Debug("查询资源管理器目录失败: " + ex.Message); ResetShell(); }
        return null;
    }

    static object? ShellInstance()
    {
        lock (Gate)
        {
            try
            {
                _shellType ??= Type.GetTypeFromProgID("Shell.Application");
                if (_shellType == null) return null;
                return _shell = Activator.CreateInstance(_shellType);
            }
            catch (Exception ex) { Log.Debug("创建 Shell.Application 失败: " + ex.Message); return null; }
        }
    }

    public static void ResetShell()
    {
        lock (Gate)
        {
            if (_shell != null) { try { Marshal.ReleaseComObject(_shell); } catch { } }
            _shell = null;
        }
    }

    public static string DesktopDir()
    {
        var d = Input.Native.GetKnownFolder(Input.Native.FOLDERID_Desktop);
        if (string.IsNullOrEmpty(d)) d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return d;
    }

    // ---------------- 资源管理器选中项（拖拽投递源） ----------------

    /// <summary>
    /// 取前台资源管理器窗口当前选中的文件路径。
    /// 注意：桌面（Progman/WorkerW）没有 Shell.Application 窗口对象，用 Ctrl+C 路线。
    /// </summary>
    public static List<string> GetForegroundSelection()
    {
        var result = new List<string>();
        var h = Input.Native.GetForegroundWindow();
        var cls = Input.Native.ClassNameOf(h);
        if (cls is not ("CabinetWClass" or "ExploreWClass")) return result;

        object? shellObj = null;
        try
        {
            shellObj = ShellInstance();
            dynamic? shell = shellObj;
            if (shell == null) return result;
            foreach (var w in (IEnumerable)shell.Windows())
            {
                try
                {
                    long hwnd = 0;
                    try { hwnd = Convert.ToInt64(((dynamic)w).HWND); } catch { continue; }
                    if (hwnd != h.ToInt64()) continue;
                    foreach (var it in (IEnumerable)((dynamic)w).Document.SelectedItems())
                    {
                        try
                        {
                            string p = (string)((dynamic)it).Path;
                            if (!string.IsNullOrEmpty(p)) result.Add(p);
                        }
                        catch { }
                    }
                    break;
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Debug("获取资源管理器选择失败: " + ex.Message); ResetShell(); }
        return result;
    }

    /// <summary>按配置解析投递接收目录（默认目录）并确保存在。</summary>
    public static string ResolveDropDir(Config cfg)
    {
        string dir;
        try { dir = Input.Native.GetFolderPath(cfg.DropDir); }
        catch { dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        if (string.IsNullOrWhiteSpace(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}
