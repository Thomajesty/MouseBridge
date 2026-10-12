using System.Runtime.InteropServices;
using MouseBridge.Net;

namespace MouseBridge.Input;

public enum HookMode { Off, Monitor, Capture }

public struct KbdEvt
{
    public int Vk, Scan;
    public bool Ext, Down;
    public bool IsInjected;
}

/// <summary>
/// 全局低级鼠标/键盘钩子，运行在带消息泵的专用线程上。
/// - Monitor：只观察不拦截（本机控制时）
/// - Capture：拦截全部物理输入并上报事件（正在远控对端时）
/// 自己注入的事件（dwExtraInfo == Magic）永远放行，防止回环。
/// </summary>
public class HookEngine
{
    IntPtr _hMouse, _hKbd;
    uint _threadId;
    Native.LowLevelHookProc _mouseProc = null!, _kbdProc = null!;

    public volatile HookMode Mode = HookMode.Off;

    /// <summary>
    /// 远控（Capture）期间，若前台是本程序自己的窗口，就别吞物理**键盘** ——
    /// 否则用户在自家设置页的输入框里打不出字。Orchestrator 启动时置 true。
    ///
    /// 注意：这条口子**只管键盘、不再管鼠标**（v1.6.9 起）。鼠标那边曾经也按"前台是
    /// 自家窗口"放行，结果只要设置页开着，它就一直是前台 —— 于是用户刚把鼠标推到对端，
    /// 下一帧就被判定"前台是自家界面"、立刻弹回本机，8 毫秒一次来回抖动，看起来像卡死。
    /// 而且远端期间物理光标是被钉在屏幕中心的（防粘滞），"指针在哪儿"本身也不可信。
    /// 远控中想操作本机界面，请用 Ctrl+Alt+1 拉回（热键在 Capture 下同样有效）。
    /// </summary>
    public volatile bool LetThroughOwnWindows = true;

    /// <summary>
    /// 反向验证开关：置 1 时恢复 v1.6.8 及以前"Capture 期间前台是自家窗口就不吞鼠标"的旧行为。
    /// 仅 tools/own_window_test.py 使用 —— 用它证明"设置页一打开就跳不过去"这条测试是有效的。
    /// </summary>
    static readonly bool BugOwnWindow = Environment.GetEnvironmentVariable("MB_BUG_OWN_WINDOW") == "1";

    /// <summary>
    /// Capture 期间的鼠标放行闸门（钩子线程高频调用，实现必须轻量、无锁）：
    /// 返回 true 时物理鼠标不吞、归本机 —— 专供"自家弹窗等待用户点击"用
    /// （连接确认窗 / 反向控制请求窗）。这类弹窗若不放行，用户在远控中根本点不到
    /// 「允许」按钮，只能干等超时被拒。注意它**不是**给设置页用的：设置页是常驻窗口，
    /// 一旦放行就会回到"前台永远是自家界面 → 事件双投 + 控制权弹回"的老问题。
    /// </summary>
    public Func<bool>? MouseLetThrough;

    /// <summary>Monitor 模式下对按键的同步裁决：返回 true = 吞掉（用于 Ctrl+V 拦截、ScrollLock 锁屏等）。</summary>
    public Func<KbdEvt, bool>? KbdFilter;

    public event Action<int, int>? MouseMove;          // 虚拟桌面坐标
    public event Action<int, bool>? Button;            // 0=左 1=右 2=中 3=X1 4=X2
    public event Action<int, int>? Wheel;              // dx, dy
    public event Action<int, int, bool, bool>? Key;    // vk, scan, ext, down

    public void Start()
    {
        _mouseProc = MouseProc;
        _kbdProc = KbdProc;
        var t = new Thread(Run) { IsBackground = true, Name = "MouseBridge.Hooks" };
        t.Start();
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        Install();
        Mode = HookMode.Monitor;

        // 看门狗：Windows 会在低级钩子回调超时（约 300ms）时**静默摘掉**钩子，
        // 而且永远不会告诉我们。表现就是"键鼠突然全失灵，重启程序才好"。
        // 这里定期检查钩子句柄是否还有效；一旦失效就用 SetWindowsHookEx 在**本线程**
        // （本来就是钩子线程，消息泵就在下面）重新装一次，把钩子救回来。
        var watchdog = new System.Threading.Timer(_ => EnsureHooks(), null, 5000, 5000);
        try
        {
            while (Native.GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { }
        }
        finally
        {
            watchdog.Dispose();
        }
    }

    /// <summary>安装 / 重装鼠标键盘钩子。返回是否两个都装成功。</summary>
    bool Install()
    {
        var hMod = Native.GetModuleHandle(null);
        try { if (_hMouse != IntPtr.Zero) Native.UnhookWindowsHookEx(_hMouse); } catch { }
        try { if (_hKbd != IntPtr.Zero) Native.UnhookWindowsHookEx(_hKbd); } catch { }
        _hMouse = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, hMod, 0);
        _hKbd = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbdProc, hMod, 0);
        bool ok = _hMouse != IntPtr.Zero && _hKbd != IntPtr.Zero;
        if (ok) Log.Info("输入钩子已安装");
        else Log.Error($"安装钩子失败 mouse={_hMouse} kbd={_hKbd} err={Marshal.GetLastWin32Error()}");
        return ok;
    }

    /// <summary>钩子自愈：句柄被系统回收（或安装失败）时重装。由看门狗定时器调用（钩子线程）。</summary>
    void EnsureHooks()
    {
        try
        {
            if (_hMouse != IntPtr.Zero && _hKbd != IntPtr.Zero) return;
            Log.Warn("检测到输入钩子已失效（多半是回调超时被系统摘掉），正在重装…");
            Install();
        }
        catch (Exception ex) { Log.Error("重装钩子失败", ex); }
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hs = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (hs.dwExtraInfo != (IntPtr)Proto.Magic)
            {
                var msg = wParam.ToInt32();
                switch (msg)
                {
                    case Native.WM_MOUSEMOVE:
                        MouseMove?.Invoke(hs.pt.X, hs.pt.Y);
                        break;
                    case Native.WM_LBUTTONDOWN: Button?.Invoke(0, true); break;
                    case Native.WM_LBUTTONUP: Button?.Invoke(0, false); break;
                    case Native.WM_RBUTTONDOWN: Button?.Invoke(1, true); break;
                    case Native.WM_RBUTTONUP: Button?.Invoke(1, false); break;
                    case Native.WM_MBUTTONDOWN: Button?.Invoke(2, true); break;
                    case Native.WM_MBUTTONUP: Button?.Invoke(2, false); break;
                    case Native.WM_XBUTTONDOWN: Button?.Invoke((hs.mouseData & 0xFFFF) == 2 ? 4 : 3, true); break;
                    case Native.WM_XBUTTONUP: Button?.Invoke((hs.mouseData & 0xFFFF) == 2 ? 4 : 3, false); break;
                    case Native.WM_MOUSEWHEEL: Wheel?.Invoke(0, (short)((hs.mouseData >> 16) & 0xFFFF)); break;
                    case Native.WM_MOUSEHWHEEL: Wheel?.Invoke((short)((hs.mouseData >> 16) & 0xFFFF), 0); break;
                }
                if (Mode == HookMode.Capture)
                {
                    // 一律吞掉：物理鼠标此刻归对端，本机不再响应。
                    //
                    // 以前这里给"前台是自家窗口"留了口子，结果设置页一打开就永远是前台 ——
                    // 用户在自家界面上动一下鼠标，事件既进系统又转发给对端（双重效果），
                    // 而且 Orchestrator 那边还会因此把控制权弹回本机，来回抖动像卡死。
                    // 想操作本机界面：Ctrl+Alt+1 拉回，或把鼠标推回返回侧边缘。
                    //
                    // BugOwnWindow 只为反向验证测试保留（tools/own_window_test.py 用它
                    // 复现旧行为，证明那条测试真的抓得到这个 bug），正常运行时恒为 false。
                    // MouseLetThrough：自家确认弹窗开着时放行，让用户点得到「允许」按钮。
                    if (!(MouseLetThrough?.Invoke() == true) &&
                        !(BugOwnWindow && LetThroughOwnWindows && Native.IsOwnProcessForeground()))
                        return (IntPtr)1; // 吞掉，交给对端
                }
            }
        }
        return Native.CallNextHookEx(_hMouse, nCode, wParam, lParam);
    }

    IntPtr KbdProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hs = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (hs.dwExtraInfo != (IntPtr)Proto.Magic)
            {
                var msg = wParam.ToInt32();
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                var evt = new KbdEvt
                {
                    Vk = (int)hs.vkCode,
                    Scan = (int)hs.scanCode,
                    Ext = (hs.flags & Native.LLKHF_EXTENDED) != 0,
                    Down = down,
                    IsInjected = false,
                };
                Key?.Invoke(evt.Vk, evt.Scan, evt.Ext, evt.Down);
                if (Mode == HookMode.Capture)
                {
                    // 前台是本程序自己的窗口时不吞（同上，保证自家界面可打字）
                    if (!(LetThroughOwnWindows && Native.IsOwnProcessForeground()))
                        return (IntPtr)1;
                }
                else if (Mode == HookMode.Monitor && KbdFilter != null && KbdFilter(evt))
                    return (IntPtr)1;
            }
        }
        return Native.CallNextHookEx(_hKbd, nCode, wParam, lParam);
    }

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
