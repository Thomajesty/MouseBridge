using System.Windows.Threading;
using MouseBridge.Input;

namespace MouseBridge.Files;

/// <summary>
/// 拖拽监视：资源管理器拖文件时系统会创建 "SysDragImage" 窗口，据此判定"正在拖拽"。
/// 当鼠标（带着拖拽物）顶到"朝对端那一侧"的屏幕边缘时，抛出 HandoverRequested，
/// 由 Orchestrator 决定是否把这次拖拽交给对端电脑继续（跨机拖拽投递）。
/// </summary>
public class DragWatcher
{
    DispatcherTimer? _timer;
    bool _dragActive;
    bool _handoverFired;

    /// <summary>是否允许拦截（主控端本机控制时 / 被控端正被对端操作时）。</summary>
    public Func<bool>? CanIntercept;
    public Func<string>? PeerSide;      // 对端在本机的哪一侧

    /// <summary>
    /// 边缘命中后的**轻量前置判断**：在钩子/定时器线程上同步执行，只做"够不够格投"的检查
    /// （已连接、开关打开、拖的是文件而不是网页图片等）。
    /// 返回 false 就不消耗本次机会 —— 用户把鼠标挪开再推过来还能重试。
    /// 真正的重活（解析资源管理器选中项）仍交给 UI 线程的 HandoverRequested。
    /// </summary>
    public Func<bool>? HandoverArmed;
    public event Action? DragStarted;
    public event Action? DragEnded;
    public event Action? HandoverRequested;   // 拖到朝对端的边缘

    public bool DragActive => _dragActive;

    public void Start()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += Tick;
        _timer.Start();
        Log.Info("拖拽监视已启动");
    }

    public void Stop() { _timer?.Stop(); _timer = null; }

    void Tick(object? s, EventArgs e)
    {
        try
        {
            bool lbutton = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
            bool shellDrag = Native.FindWindow("SysDragImage", null) != IntPtr.Zero;
            bool active = lbutton && shellDrag;

            if (active && !_dragActive)
            {
                _dragActive = true;
                _handoverFired = false;
                DragStarted?.Invoke();
            }
            else if (!active && _dragActive)
            {
                _dragActive = false;
                _handoverFired = false;
                DragEnded?.Invoke();
            }

            if (_dragActive && !_handoverFired && (CanIntercept?.Invoke() ?? false))
            {
                Native.GetCursorPos(out var p);
                if (AtPeerEdge(p.X, p.Y))
                {
                    // 轻量前置判断（同步）：不合格就不算数，用户挪开再推还能重试
                    if (!(HandoverArmed?.Invoke() ?? true))
                    {
                        Log.Debug("拖拽到边缘但当前不可投递，保留本次机会");
                        return;
                    }
                    _handoverFired = true;
                    HandoverRequested?.Invoke();
                }
            }
        }
        catch (Exception ex) { Log.Debug("DragWatcher: " + ex.Message); }
    }

    /// <summary>
    /// 鼠标是否顶到了"朝对端那一侧"的边缘。
    /// 阈值与 Orchestrator 的边缘切换保持一致（用 W/H 的边界，不额外留 2px 死区）——
    /// 以前这里比切换判定窄 1px，会出现"鼠标已经贴边了但拖拽投递不触发"。
    /// </summary>
    bool AtPeerEdge(int x, int y)
    {
        var vs = Native.VirtualScreen();
        return (PeerSide?.Invoke()) switch
        {
            "right" => x >= vs.X + vs.W - 1,
            "left" => x <= vs.X,
            "top" => y <= vs.Y,
            "bottom" => y >= vs.Y + vs.H - 1,
            _ => false,
        };
    }
}
