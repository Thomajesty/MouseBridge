using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using MouseBridge.Clipboard;
using MouseBridge.Core;
using MouseBridge.Files;
using MouseBridge.Input;
using MouseBridge.Net;
using MouseBridge.Ui;

namespace MouseBridge.Control;

public enum BadgeState { None, Synced, PendingFile, ReadyFile }

/// <summary>光环状态。颜色语义见 IndicatorWindow。</summary>
public enum IndState
{
    Disconnected,   // 灰
    Local,          // 绿：控制本机
    RemoteCtl,      // 橙：正在控制对端
    Controlled,     // 红：正被对端控制
    Transferring,   // 青：正在传文件
    Dragging,       // 紫：拖拽中
    DropReady,      // 青绿：松手即可投递
    DropBlocked,    // 红：此处不能投递
}

/// <summary>光环要显示的一帧状态。</summary>
public readonly record struct IndView(IndState State, BadgeState Badge, double Progress, string? Text, string? Detail);

/// <summary>
/// 中枢：连接管理、边缘切换状态机、剪贴板/文件/拖拽投递的调度、热键。
/// 角色语义：
///   server（主控端）：键鼠所在机器，把输入转发给对端并注入；
///   client（被控端）：接收注入；也接收剪贴板/文件。
/// 剪贴板 / 文件 / 跨机拖拽 均为双向对称。
/// </summary>
public class Orchestrator : IDisposable
{
    public Config Cfg { get; }

    readonly HookEngine _hook = new();
    readonly ClipboardService _clip = new();
    Discovery _disc = new();
    DragWatcher _drag = new();
    FileTransferService _xfer;
    IndicatorWindow? _indicator;
    ToastWindow? _toast;
    ControlBanner? _banner;          // 被控端：右上角"正在被控制"横幅

    SecureLink? _link;
    /// <summary>
    /// 真正"赢得会话"的那条链路。与 <see cref="_link"/> 的区别：_link 是当前占用的槽位（会被
    /// 同时拨号的两条链路反复改写），_activeLink 只在确认自己是最终保留者时才赋值，
    /// 用来挡住"两条链路几乎同时建起"时重复的会话初始化与日志输出。
    /// </summary>
    SecureLink? _activeLink;
    TcpListener? _listener;
    CancellationTokenSource _netCts = new();
    readonly ManualResetEventSlim _connectKick = new(false);   // 置位=让客户端连接循环立刻醒来尝试连接
    /// <summary>
    /// 会话级角色反转。两台都设成"主控端"（或都被控端）时连接能建起来，但没人控制谁，
    /// 表现是"连上了却完全不能用"。这里按机器名做确定性仲裁：名字大的让位当被控端，
    /// 两边各自算一遍结果相反且稳定，不需要额外协商消息。只在本次会话内有效，不落盘。
    /// </summary>
    volatile bool _roleSwap;
    long _lastAttemptTick;
    string _lastTarget = "";

    // —— 状态（跨线程访问，volatile）——
    public volatile bool RemoteActive;          // server：正在控制对端
    public volatile bool BeingControlled;       // client：正被对端控制
    public volatile bool LockToScreen;
    public volatile bool PeerDragActive;        // 对端正在拖拽（抑制切回）
    volatile bool _dragActiveLocal;             // 本机正在拖拽（抑制切出）
    public volatile BadgeState Badge;
    public volatile bool Connected;

    // —— 切换光标状态（server，钩子线程访问）——
    double _cx, _cy;                            // 对端虚拟桌面上的光标位置
    int _lastPhysX, _lastPhysY;
    bool _armedOut, _armedBack;
    long _edgeSince;
    DispInfo _clientDisp = new(0, 0, 0, 0);
    string _sessionSide = "right";              // 对端相对本机方向（会话生效值）

    // —— 跨机拖拽投递 ——
    volatile bool _dropHandover;                // 源端：已发起跨机拖拽，等待松手提交
    long _dropOutId;
    long _dropOutTotal;
    int _dropOutCount;
    volatile DropIn? _dropIn;                   // 目标端：对端拖进来的会话
    string? _dropHoverDir;
    bool _dropHoverReal;

    sealed class DropIn
    {
        public long Id;
        public int Count;
        public long Total;
        public bool Committed;
    }

    // —— 物料 ——
    long _idSeq = DateTime.UtcNow.Ticks;
    volatile PendingRecv? _pending;
    // 发送中的物料表。**所有**读写都必须持 _outGate：
    //   写：OnDragHandover / 剪贴板推文件时登记；读：收 progress 回调时查；删：传输结束。
    // 这些访问散落在钩子线程、UI 线程（UiUtil.Run 里）、网络读循环线程三处，
    // 早前是裸 Dictionary，三方并发 Rehash 时轻则读到陈旧项、重则抛
    // InvalidOperationException（"集合已修改"）把整条读循环带崩。加锁后一律短临界区。
    readonly object _outGate = new();
    readonly Dictionary<long, List<FileScanner.Item>> _outgoing = new();
    readonly Dictionary<long, string> _outgoingVia = new();   // id → 触发方式（剪贴板 / 跨机拖拽）

    class PendingRecv
    {
        public long Id;
        public long Total;
        public bool Requested;    // 已请求传输
        public bool Drop;         // 投递模式（拖拽）
        public string? TargetDir; // 投递目标目录
    }

    // —— 传输进度（网络线程写，UI 泵读）——
    volatile bool _xferBusy;
    volatile bool _xferSending;
    long _xferSent, _xferTotal;
    string _xferLabel = "";
    long _lastToastMs;

    // —— 光环文字：状态切换时短暂显示 ——
    IndState _lastIndState = IndState.Disconnected;
    long _labelUntil;

    DispatcherTimer? _uiPump;
    DispatcherTimer? _hoverPump;

    public Orchestrator(Config cfg)
    {
        Cfg = cfg;
        _xfer = new FileTransferService(SendViaLinkAsync);
    }

    /// <summary>本机在**本次连接**里是不是主控端（配置角色 XOR 会话仲裁结果）。</summary>
    public bool IsServerRole => (Cfg.Role == "server") ^ _roleSwap;

    /// <summary>配置里写死的角色（不受会话仲裁影响），供界面显示与握手使用。</summary>
    public bool IsConfiguredServer => Cfg.Role == "server";

    public string PeerName => _link?.Peer.Name ?? "";
    /// <summary>到对端的往返时延（毫秒）；未连接或还没测出来时为 -1。仅用于界面展示。</summary>
    public long PeerRttMs => _link?.RttMs ?? -1;
    public string MyName => Cfg.DisplayName;
    /// <summary>当前正在尝试连接的对端地址（未连接时给状态栏用，让用户看到确实在连）。</summary>
    public string CurrentTarget => _lastTarget;

    /// <summary>
    /// 反向验证开关：置 1 时恢复 v1.6.8 及以前"远控期间前台是自家窗口就把控制权弹回本机"的旧行为。
    /// 只有 tools/own_window_test.py 会用 —— 用来证明"设置页一打开就跳不到对端"这条测试真的抓得到
    /// 那个 bug（只修代码不修测试，测试会白过）。
    /// </summary>
    static readonly bool BugOwnWindow = Environment.GetEnvironmentVariable("MB_BUG_OWN_WINDOW") == "1";

    /// <summary>
    /// 自家确认弹窗（连接确认 / 反向控制请求）正开着。远控（Capture）期间它为真时：
    /// 钩子放行物理鼠标（见 <see cref="HookEngine.MouseLetThrough"/>）、
    /// Orchestrator 不再把鼠标位移转发给对端 —— 用户要能点到弹窗上的「允许/拒绝」。
    /// 只针对弹窗，不给设置页用：设置页是常驻窗口，放行就会回到"事件双投 + 控制权弹回"的老问题。
    /// </summary>
    volatile bool _askOpen;

    // ================= 启动 / 应用配置 =================

    public void Start()
    {
        AppPaths.Ensure();
        Staging.CleanAll();

        _hook.KbdFilter = KbdFilter;
        _hook.MouseMove += OnHookMouseMove;
        _hook.Button += OnHookButton;
        _hook.Wheel += OnHookWheel;
        _hook.Key += OnHookKey;
        // 远控期间前台若是本程序自己的窗口，就不吞**键盘**（鼠标另说，见 HookEngine 注释）
        _hook.LetThroughOwnWindows = true;
        // 远控期间自家确认弹窗开着 → 物理鼠标归本机，用户才能点到「允许/拒绝」
        _hook.MouseLetThrough = () => _askOpen;
        _hook.Start();

        _clip.Changed += OnClipboardChanged;
        _clip.DisplayChanged += SendDispChange;
        _clip.Start();

        _xfer.Progress += OnXferProgress;
        _xfer.RecvDone += OnXferRecvDone;
        _xfer.Failed += OnXferFailed;
        _xfer.SendDone += OnXferSendDone;

        SetupDragWatcher();

        UiUtil.Run(() =>
        {
            _indicator = new IndicatorWindow();
            ApplyLiveConfig();
            _toast = new ToastWindow();
            // 被控端右上角常驻横幅：正被控制时出现，带「暂停接管」按钮
            _banner = new ControlBanner();
            _banner.PauseRequested += () => ReleaseControl("被控端主动暂停接管");
        });

        StartNetworking();
        StartDiscovery();

        _uiPump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiPump.Tick += (_, _) => PumpUiState();
        UiUtil.Run(() => _uiPump?.Start());

        _hoverPump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverPump.Tick += (_, _) => PumpDropHover();
        UiUtil.Run(() => _hoverPump?.Start());

        Log.Info($"启动完成 角色={Cfg.Role} 本机名={MyName} 版本={AppInfo.Version}");
    }

    /// <summary>设置保存后调用：重启网络与发现，其余组件读取同一 Cfg 实例自然生效。</summary>
    public void ApplyConfig()
    {
        Log.Info("应用新配置");
        _hook.Mode = HookMode.Monitor;
        LastConnectError = "";
        Interlocked.Exchange(ref _attemptIdx, 0);
        // 用户主动重连 = 一次**新的邀请**，解除"对方拒绝过"的暂停闸门。
        // 这是"拒绝一次后不再重复被邀请，除非再次被邀请"里的那个"再次被邀请"。
        _declinedByPeer = false;

        // 会话级状态统一归零（含反向控制四件套、注入键补抬起、屏幕几何等）。
        // 以前这里只清了一半，剩下的（_revAllowed/_revGranted/ReverseControlled/LockToScreen…）
        // 会跨会话残留：改完配置重连后，主控端可能一上来就以为自己"正被反向控制"，
        // 于是 OnHookMouseMove 最前面那段"本机鼠标一动就收回"每帧都跑，把正常操作吞掉。
        ResetSessionState("应用新配置");

        // 先摘掉引用再关：这样 OnLinkClosed 会判定"不是当前链路"直接返回，
        // 不会在用户点「连接」重连时弹一条"连接已断开"的干扰提示。
        //
        // 注意：正因为 OnLinkClosed 会提前返回，它里面那句 Connected = false 不会执行 ——
        // 所以这里必须自己把 Connected 归零。否则会留下一个"看起来还连着"的僵尸状态：
        //   · ClientLoop 的 if (!Connected) 恒为假 → 再也不去拨号
        //   · ServerLoop 的 if (Connected) 为真 → 对方连进来会被直接关掉
        // 用户看到的现象就是"第一次能连，断开后第二次怎么都连不上"。
        SecureLink? old;
        lock (_linkGate) { old = _link; _link = null; _inLink = null; _outLink = null; _activeLink = null; }
        // MB_BUG_NO_RESET_CONNECTED=1 时故意跳过归零，用来证明回归测试确实能抓到这个 bug
        // （tools/reconnect_test.py 用例 C）。正常运行时这个环境变量不存在。
        if (Environment.GetEnvironmentVariable("MB_BUG_NO_RESET_CONNECTED") != "1")
            Connected = false;
        try { old?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;

        // 取消并**释放**上一轮的取消源。只 Cancel 不 Dispose 的话，每次点"保存并应用"
        // 都会留下一个注册了内部定时器资源的 CTS（还有它派生出去的 linked token），
        // 长时间反复改配置会持续泄漏。先 Cancel 再 Dispose 是标准配对写法。
        var prevCts = _netCts;
        _netCts = new CancellationTokenSource();
        try { prevCts.Cancel(); } catch { }
        try { prevCts.Dispose(); } catch { }

        _disc.Stop();
        StartNetworking();
        StartDiscovery();
        UiUtil.Run(ApplyLiveConfig);
    }

    void SetupDragWatcher()
    {
        _drag = new DragWatcher
        {
            // 主控端：本机控制时（未在控对端）+ 已连接；被控端：正被对端控制时。
            // 必须带上 Connected —— 没连上时不该触发投递，否则会白耗掉一次机会。
            CanIntercept = () => IsServerRole ? (Connected && !RemoteActive) : BeingControlled,
            PeerSide = () => _sessionSide,
            // 轻量前置判断（同步，跑在定时器线程）：
            //   · 跨机拖拽功能没开 → 不投
            //   · 没连接 / 接管被暂停 → 不投
            //   · 前台不是资源管理器 → 不投（桌面拖拽没有 Shell.Application 窗口对象，
            //     以前到 UI 线程才判、白耗一次机会；这里提前拦掉，用户挪开重推仍可投）
            HandoverArmed = () =>
            {
                if (!Cfg.DragDropAcrossMachines || !Connected || !Cfg.TakeoverEnabled) return false;
                return Native.IsShellForeground();
            },
        };
        _drag.DragStarted += () =>
        {
            if (IsServerRole) _dragActiveLocal = true;
            else PostMsg(new Msg { T = "drag_active", On = true });
        };
        _drag.DragEnded += () =>
        {
            if (IsServerRole) _dragActiveLocal = false;
            else PostMsg(new Msg { T = "drag_active", On = false });
        };
        _drag.HandoverRequested += () => UiUtil.Run(OnDragHandover);
        _drag.Start();
    }

    void ApplyLiveConfig()
    {
        _indicator?.Configure(Cfg.IndicatorEnabled, Cfg.IndicatorSize, Cfg.ShowBadge, Cfg.IndicatorText, Cfg.ShowProgressArc);
    }

    /// <summary>设置页拖动滑块时实时生效（不落盘）。</summary>
    public void SetIndicatorLive(int size, bool text, bool arc)
    {
        Cfg.IndicatorSize = size; Cfg.IndicatorText = text; Cfg.ShowProgressArc = arc;
        UiUtil.Run(ApplyLiveConfig);
    }

    /// <summary>设置页勾选/取消"显示光标光环"时实时生效。</summary>
    public void SetIndicatorEnabledLive(bool on)
    {
        Cfg.IndicatorEnabled = on;
        UiUtil.Run(ApplyLiveConfig);
    }

    /// <summary>设置页改"显示物料徽标"时实时生效。</summary>
    public void SetBadgeLive(bool on)
    {
        Cfg.ShowBadge = on;
        UiUtil.Run(ApplyLiveConfig);
    }

    /// <summary>诊断/出图用：为 true 时 UI 状态泵不再覆盖光环（--preview-halo）。</summary>
    public bool DebugHaloMode;

    /// <summary>诊断/出图用：强行把光环设成指定状态（--preview-halo）。</summary>
    public void DebugShowHalo(IndState st, string? text, string? detail, double progress)
    {
        UiUtil.Run(() =>
        {
            ApplyLiveConfig();
            _indicator?.SetView(st, BadgeState.None, progress, text, detail);
        });
    }

    /// <summary>托盘/热键：跳到对端。</summary>
    public void HotkeyJumpToPeer()
    {
        if (!IsServerRole) return;
        if (!Connected) { UiUtil.Run(() => _toast?.Show(AppInfo.Title, "尚未连接对端", null, 2000)); return; }
        if (RemoteActive) return;
        UiUtil.Run(() => SwitchToRemote(CursorY()));
    }

    /// <summary>托盘/热键：拉回本机。</summary>
    public void HotkeyBackToLocal()
    {
        if (!IsServerRole) return;
        if (!RemoteActive) return;
        UiUtil.Run(SwitchToLocal);
    }

    public void SetTakeover(bool on)
    {
        Cfg.TakeoverEnabled = on;
        Cfg.Save();
        if (!on && RemoteActive) SwitchToLocal();
        UiUtil.Run(() => _toast?.Show(AppInfo.Title, on ? "已恢复接管" : "已暂停接管（鼠标不再跨屏）", null, 2200));
    }

    /// <summary>
    /// 被控端主动收回控制权（右上角横幅的「暂停接管」按钮，或本机热键触发）。
    /// 告知主控端"回到你自己那边"，本机随即退出被控态，横幅收起。
    /// </summary>
    public void ReleaseControl(string reason)
    {
        if (!BeingControlled) return;
        BeingControlled = false;
        PostMsg(new Msg { T = "remote_off" });     // 让主控端把鼠标带回本机
        ReleaseInjectedKeys("被控端收回了控制权");
        lock (_keyGate) { _injectedDownKeys.Clear(); _injectedDownBtns.Clear(); }
        UiUtil.Run(() =>
        {
            _banner?.Hide();
            _toast?.Show("已暂停接管", "已收回鼠标键盘控制权（" + reason + "）。", null, 2400);
        });
        Log.Info("被控端主动收回控制权：" + reason);
    }

    // ================= 网络 =================

    DispInfo MyDisp()
    {
        var (x, y, w, h) = Native.VirtualScreen();
        return new DispInfo(x, y, w, h);
    }

    /// <summary>
    /// v1.5.0：两端都监听、两端都能主动连接。
    /// 之前的版本只有"被控端"会拨号，"主控端"纯监听——于是用户在**主控端**点「连接」毫无反应
    /// （它就是那台只等别人来连的机器）。现在角色只决定"谁控制谁"，不再决定"谁能发起连接"，
    /// 谁填了地址/扫到了对端，谁就能连上去。
    /// </summary>
    void StartNetworking()
    {
        _ = Task.Run(ServerLoop);
        _ = Task.Run(ClientLoop);
    }

    async Task ServerLoop()
    {
        var ct = _netCts.Token;
        try
        {
            // 端口可能被上一轮的监听套接字短暂占着（ApplyConfig 刚 Stop 掉又要重绑）。
            // 绑不上就重试几次 —— 绝不能像以前那样"静默退出"：那会让本机彻底不再监听，
            // 对方永远连不进来，而界面上一点异常都看不到（用户只会说"第二次连不上了"）。
            Exception? last = null;
            for (int i = 0; i < 12 && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Any, Cfg.Port);
                    _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _listener.Start();
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (i == 0) Log.Warn($"监听端口 {Cfg.Port} 暂不可用，正在重试：{ex.Message}");
                    try { _listener?.Stop(); } catch { }
                    _listener = null;
                    try { await Task.Delay(400, ct); } catch (OperationCanceledException) { return; }
                }
            }
            // 12 次快重试（约 5 秒）仍失败：多半是端口被**别的进程**长时间占着，或防火墙拦了。
            // 关键：不能 return —— 那等于本机从此永不监听，界面只显示"连不上"，用户完全无从下手。
            // 改成转入低频重试（每 10 秒一次），端口一旦释放就能自动恢复监听。
            while (_listener == null && !ct.IsCancellationRequested)
            {
                LastConnectError = $"端口 {Cfg.Port} 被占用，本机无法监听（正在重试）";
                Log.Warn($"监听端口 {Cfg.Port} 失败，10 秒后重试" + (last != null ? "：" + last.Message : ""));
                try { await Task.Delay(10000, ct); } catch (OperationCanceledException) { return; }
                try
                {
                    _listener = new TcpListener(IPAddress.Any, Cfg.Port);
                    _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _listener.Start();
                    LastConnectError = "";
                    Log.Info($"监听端口 {Cfg.Port} 已恢复（此前被占用）");
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { _listener?.Stop(); } catch { }
                    _listener = null;
                }
            }
            if (_listener == null) return;   // 仅在取消时才走到这
            Log.Info($"监听端口 {Cfg.Port}（角色={Cfg.Role}）");
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync();
                if (Connected) { try { tcp.Close(); } catch { } continue; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var link = await SecureLink.AcceptAsync(tcp, Cfg.TlsEnabled, Cfg.TlsEnabled ? CertProvider.GetOrCreate() : null);
                        var ok = await link.HandshakeAsync(true, Cfg.Password, MyName, "", MyDisp(), Cfg.PeerSide,
                            Cfg.Role, Environment.MachineName,
                            drivesConfirm: AmConfirmSide,
                            askUser: AskUserConfirm);
                        if (ok) OnLinkUp(link, inbound: true);
                        else
                        {
                            Log.Info($"未接受来自 {link.RemoteAddress} 的连接：{link.LastError}");
                            link.Close();
                        }
                    }
                    catch (Exception ex) { Log.Warn("接受连接失败: " + ex.Message); try { tcp.Close(); } catch { } }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        // 退出时 _listener.Stop() 会让挂起的 Accept 抛 SocketException（"已中止 I/O 操作"）。
        // 只有"确实在收尾"时才当正常；否则（比如端口被别人抢了）必须留下痕迹，
        // 不然本机静悄悄地不再监听，用户只会看到"连不上"却查不到原因。
        catch (SocketException ex)
        {
            if (!_netCts.IsCancellationRequested)
                Log.Error($"监听循环因 Socket 错误退出（本机将无法被连接）：{ex.Message}", ex);
        }
        catch (Exception ex) { Log.Error("监听循环异常", ex); }
    }

    async Task ClientLoop()
    {
        var ct = _netCts.Token;
        while (!ct.IsCancellationRequested)
        {
            if (!Connected)
            {
                try
                {
                    var (host, port) = ResolveTarget();
                    if (host != null)
                    {
                        _lastTarget = host + ":" + port;
                        _lastAttemptTick = Monotonic.Ms;
                        Log.Info($"连接对端 {host}:{port} …");
                        // 这个超时**只管 TCP 建连**，管不到握手 —— 别误读：
                        // 下面 tCts 只作用在 ConnectAsync（WithToken 等待的那个 Task）上，
                        // HandshakeAsync 用的是它自己内部的两段超时（认证 6s / 等待人工确认
                        // SecureLink.ConfirmWaitMs）。所以这里的秒数与"对方要问多久"无关。
                        //
                        // （曾一度以为是外层 3.5s 掐死了对方的确认等待、从而导致反复重连，
                        //   实测发现不成立：把延长去掉、让对端犹豫 9 秒，照样一次连上。
                        //   真正让地址来回跳的是候选列表里混进了自动发现的地址，见 Targets()。）
                        using var tCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        tCts.CancelAfter(TcpReadyWaitMs);
                        var link = await SecureLink.ConnectAsync(host, port, Cfg.TlsEnabled).WithToken(tCts.Token);
                        var ok = await link.HandshakeAsync(false, Cfg.Password, MyName, "", MyDisp(), Cfg.PeerSide,
                            Cfg.Role, Environment.MachineName,
                            drivesConfirm: AmConfirmSide,
                            askUser: AskUserConfirm,
                            onPeerAsking: () =>
                            {
                                WaitConfirmPeer = link.Peer.Name.Length > 0 ? link.Peer.Name : host;
                                Log.Info("对端正在征求它本机用户的确认，等待中");
                                // 防御性放宽：目前这行不起作用（见上方 TcpReadyWaitMs 处的说明，
                                // 外层 token 传不进 HandshakeAsync）。保留它是为了将来一旦把 token
                                // 接进握手流程，不会因为外层还是 3.5 秒而把对方的确认窗口掐断。
                                tCts.CancelAfter(PeerConfirmWaitMs);
                                OnUiHint?.Invoke();
                            });
                        WaitConfirmPeer = "";
                        OnUiHint?.Invoke();
                        if (!ok)
                        {
                            // 把"为什么连不上"讲清楚：密码不对 / 对方拒绝 / 对方没启动服务
                            Log.Warn($"连接 {host}:{port} 未成功：{link.LastError}");
                            LastConnectError = link.LastError;
                            bool declined = link.PeerDeclined;
                            link.Close();

                            if (declined && Environment.GetEnvironmentVariable("MB_BUG_NO_DECLINE_GATE") != "1")
                            {
                                // 对方**用户**明确点了「拒绝」。
                                //
                                // 这时必须停下自动重连，而不是换地址继续试：轮换重试会让对方
                                //（只要它开着「连接需确认」）被确认框一遍遍弹出来 —— 这正是
                                // 用户反馈的"拒绝一次后还一直被邀请"。
                                // 尊重对方的选择，等本机用户重新点「连接」再发起
                                //（ApplyConfig / ReconnectNow 会清掉这个闸门）。
                                _declinedByPeer = true;
                                LastConnectError = "对方拒绝了本次连接。已停止自动重连，确认无误后点「连接」重试。";
                                Log.Info("对方拒绝连接 → 暂停自动重连，等待用户手动重试");
                                OnUiHint?.Invoke();

                                // 原地挂住：不轮换地址、不去敲对方的门，直到用户重新发起
                                while (_declinedByPeer && !ct.IsCancellationRequested)
                                    await DelayConnect(1000, ct);
                                continue;
                            }

                            NextAttempt();                  // 换下一个候选地址再试
                            await DelayConnect(2500, ct);
                            continue;
                        }
                        // 证书指纹钉扎：首次记录，之后不一致则拒绝（防中间人）
                        var thumb = link.RemoteThumb ?? "";
                        if (Cfg.TlsEnabled)
                        {
                            if (string.IsNullOrEmpty(Cfg.PinnedServerThumb))
                            {
                                Cfg.PinnedServerThumb = thumb; Cfg.Save();
                                Log.Info($"已记录对端证书指纹 {thumb}");
                            }
                            else if (!string.Equals(Cfg.PinnedServerThumb, thumb, StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Error($"对端证书指纹变化！期望 {Cfg.PinnedServerThumb}，实际 {thumb}，拒绝连接");
                                LastConnectError = "对端证书指纹变化，已拒绝（重装过软件？可在高级页重置信任）";
                                UiUtil.Run(() => _toast?.Show("安全警告", "对端证书发生变化，已拒绝连接。若确认是重装软件，请在设置中重置信任。", null, 8000));
                                link.Close();
                                await DelayConnect(3000, ct);
                                continue;
                            }
                        }
                        LastConnectError = "";
                        OnLinkUp(link, inbound: false);
                    }
                    else _lastTarget = "";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { Log.Debug("连接超时，重试"); NextAttempt(); }
                catch (TimeoutException) { Log.Debug("连接超时，重试"); NextAttempt(); }
                catch (Exception ex) { Log.Debug("连接失败: " + ex.Message); NextAttempt(); }
            }
            // 已知目标（填了地址或发现过对端）就快速重试；一无所知时稍等发现报文
            bool hasTarget = Targets().Count > 0;
            await DelayConnect(Connected ? 2500 : (hasTarget ? 700 : 1200), ct);
        }
    }

    /// <summary>可被 ReconnectNow/发现事件打断的等待——踢一下就立刻醒来去连接。</summary>
    async Task DelayConnect(int ms, CancellationToken ct)
    {
        try { await Task.Run(() => _connectKick.Wait(ms, ct), ct); }
        catch (OperationCanceledException) { }
        _connectKick.Reset();
    }

    /// <summary>设置页「立即重新连接」/ 托盘触发：立刻醒来按当前配置连一次。</summary>
    public void ReconnectNow()
    {
        Log.Info("手动触发重新连接");
        // 用户主动发起 = 新的邀请：解除"被对方拒绝"的暂停闸门，并从头轮候选地址。
        _declinedByPeer = false;
        Interlocked.Exchange(ref _attemptIdx, 0);
        _connectKick.Set();
    }

    /// <summary>本机局域网 IPv4 列表（给设置页展示，方便在对面机器填地址）。</summary>
    public static string LocalIpsText()
    {
        try { return string.Join("、", Discovery.LocalIPv4()); }
        catch { return ""; }
    }

    /// <summary>本机所有可用 IPv4（多网卡时界面要把每个都列出来）。</summary>
    public static List<string> LocalIps() => Discovery.LocalIPv4();

    (string? host, int port) ResolveTarget()
    {
        var list = Targets();
        if (list.Count == 0) return (null, Cfg.Port);
        // 用 Interlocked 读：_attemptIdx 会被连接循环（多条并发路径）和 ApplyConfig 同时读写，
        // 普通 int 读写虽不会撕裂，但"读—改—写"序列会丢更新，导致候选地址轮换卡住。
        int idx = Interlocked.CompareExchange(ref _attemptIdx, 0, 0);
        if (idx < 0) idx = 0;
        var t = list[idx % list.Count];
        return (t.host, t.port);
    }

    int _attemptIdx;

    /// <summary>
    /// **TCP 建连**的等待上限（不含握手）。对端不在线 / 端口没开时几秒就能判断出来，
    /// 不必按 TCP 默认的 20 多秒干等。
    /// </summary>
    const int TcpReadyWaitMs = 3500;

    /// <summary>
    /// 防御性上界：**当前不生效**（外层 token 传不进 HandshakeAsync，详见 ClientLoop 里的说明）。
    /// 真实起作用的是 <c>SecureLink.ConfirmWaitMs</c>，以及它必须大于 <c>AskUserConfirm</c>
    /// 给用户的时间 —— 那才是三方时长里真正需要保证的一环。
    /// </summary>
    const int PeerConfirmWaitMs = 80000;

    /// <summary>
    /// 对方用户明确拒绝过本次连接，自动重连已暂停。
    ///
    /// 由 <c>ClientLoop</c> 在收到"被拒绝"时置位；用户重新点「连接」/「立即重连」时清除。
    /// 存在的意义：被拒绝之后继续自动轮换重试，会让对方（开着「连接需确认」时）
    /// 被确认框反复弹出来 —— 那是打扰，不是健壮。
    /// </summary>
    volatile bool _declinedByPeer;

    /// <summary>把候选下标推进到下一个（线程安全）。</summary>
    void NextAttempt() => Interlocked.Increment(ref _attemptIdx);

    /// <summary>
    /// 依次尝试的候选地址。一台机器可能有多个网卡/多个网段地址（例如同时有 192.168.1.2 和
    /// 192.168.31.228），扫到的地址不一定就是能通的那个 —— 所以这里返回**列表**，连接循环
    /// 逐个轮着试，而不是死磕第一个。
    /// 被控端只在"用户手填了地址"时才主动拨号：它的本职是等服务，不该自己去连网段里的陌生机器；
    /// 但跨路由器时被控端可能在被 NAT 的那一侧（外面连不进来），所以保留手填地址这条主动通路。
    /// </summary>
    List<(string host, int port)> Targets()
    {
        var list = new List<(string host, int port)>();

        var manual = ParseManualTarget();
        if (manual != null)
        {
            // 用户手填了地址 = **明确指定**目标，就只用它，不再掺入自动发现的结果。
            //
            // 以前这里还会把扫到/发现到的地址追加在后面，配合 ResolveTarget 的"轮换"，
            // 结果就是：用户明明填了 192.168.31.228，界面却在 31.228 与自动发现的
            // 192.168.1.2 之间来回跳 —— 用户看到的是"我填的地址被改掉了"，非常困惑。
            // 手填优先于自动发现，这是最基本的预期。
            //
            // 顺带一个好处：list 只有一项时 ResolveTarget 的 idx % count 恒为 0，
            // 手填的地址不会被"轮换"跳过。
            list.Add(manual.Value);
            // MB_BUG_MIX_DISCOVERED=1 时不 return，沿用旧版"继续追加发现结果"的行为，
            // 用来证明 decline_test.py 用例 2 确实能抓到它（防止测试假通过）。
            if (Environment.GetEnvironmentVariable("MB_BUG_MIX_DISCOVERED") == "1")
                Log.Warn("[测试钩子] 手填地址后仍混入发现结果（复现旧缺陷）");
            else
                return list;
        }

        if (Cfg.AutoDiscover)
        {
            bool autoDial = IsServerRole;          // 主控端才自动连扫到/发现的对端
            if (autoDial)
            {
                foreach (var p in _disc.Peers())
                    if (!list.Any(t => t.host == p.Host && t.port == p.Port))
                        list.Add((p.Host, p.Port));
            }
        }
        return list;
    }

    /// <summary>解析「对端地址」里的手填值（支持 主机 / 主机:端口 / [v6]:端口）。</summary>
    (string host, int port)? ParseManualTarget()
    {
        var h = Cfg.PeerHost.Trim();
        if (h.Length == 0) return null;
        int port = Cfg.Port;
        if (h.StartsWith("["))
        {
            int close = h.IndexOf(']');
            if (close > 0)
            {
                var tail = h.Substring(close + 1);
                if (tail.StartsWith(":") && int.TryParse(tail.Substring(1), out var p6)) port = p6;
                h = h.Substring(1, close - 1);
            }
        }
        else
        {
            int i = h.LastIndexOf(':');
            if (i > 0 && int.TryParse(h.Substring(i + 1), out var p)) { port = p; h = h.Substring(0, i); }
        }
        h = h.Trim();
        if (h.Length == 0) return null;
        if (IsSelfAddress(h))
        {
            // 填的是本机地址：连上去只会连到自己（还会自我认证成功，变成"自己控自己"）
            if (_lastSelfWarn != h) { _lastSelfWarn = h; Log.Warn($"对端地址 {h} 是本机地址，已忽略"); }
            return null;
        }
        return (h, port);
    }

    string _lastSelfWarn = "";

    /// <summary>用户在「对端地址」里填的是不是本机地址（设置页据此提示）。</summary>
    public bool TargetIsSelf => IsSelfAddress(Cfg.PeerHost.Trim());

    static bool IsSelfAddress(string host)
    {
        try
        {
            if (host.Length == 0) return false;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return !Discovery.AllowSelfLoopback;
            if (!IPAddress.TryParse(host, out var ip)) return false;
            return Discovery.IsLocal(ip);
        }
        catch { return false; }
    }

    readonly object _linkGate = new();
    SecureLink? _inLink, _outLink;

    /// <summary>
    /// 用来做"谁让位/谁保留链路"的排序键，必须两端算法一致、结论相反。
    /// 优先用【计算机名+显示名】：计算机名在同一网段内本就必须唯一，
    /// 但万一两台机器真的同名（克隆系统镜像很常见），显示名还能兜一层；
    /// 只有显示名也一样时才彻底无法区分（同时也说明这两台机器在网络里没法共存）。
    /// 对端没带计算机名（老版本）时两端都退化成只用显示名，保证口径一致。
    /// </summary>
    static string KeyFor(string machine, string display) =>
        string.IsNullOrEmpty(machine) ? display : machine + "\u0001" + display;

    static void RoleKeys(SecureLink link, string myName, out string myKey, out string peerKey)
    {
        bool peerHasMachine = !string.IsNullOrEmpty(link.Peer.Machine);
        myKey = KeyFor(peerHasMachine ? Environment.MachineName : "", myName);
        peerKey = KeyFor(link.Peer.Machine ?? "", link.Peer.Name);
    }

    /// <summary>
    /// 链路建立。inbound=true 表示这条是对方连过来的。
    /// 现在两端都会主动拨号，所以必须防"同时建两条链路"：那样两边各用一条，
    /// 输入会双份、状态会错乱。用排序键做确定性取舍——两台机器算出来的结论互为相反，
    /// 最终保留的一定是同一条物理链路。
    ///
    /// 注意：判断"槽位是否被占"不能看 SecureLink.Alive，那个标志要等 Start() 才置位，
    /// 而 Start() 是在本方法末尾调用的 —— 两个线程几乎同时进来的话，
    /// 后进来的那条会看到 Alive=false 从而漏判，两条链路就都留在手上。
    /// 所以这里统一用槽位引用是否为空来判断，并且把 Start() 放进锁内。
    /// </summary>
    void OnLinkUp(SecureLink link, bool inbound)
    {
        RoleKeys(link, MyName, out var myKey, out var peerKey);
        bool iAmLower = string.CompareOrdinal(myKey, peerKey) < 0;
        // 这次是"给同一会话换一条底层链路"，还是"真的新连上"？
        // 同时拨号时两条链路会先后到达：先到的那条已经宣告过会话了，后到的按排序键胜出、
        // 把先到的关掉。若不加区分，同一个会话会被宣告两次（日志两条"已连接对端"、
        // 弹两个提示），看起来像去重失效。用 _activeLink 区分。
        bool replacedExisting = false;

        lock (_linkGate)
        {
            bool hadActive = _activeLink != null;

            var same = inbound ? _inLink : _outLink;
            if (same != null)
            {
                Log.Info($"同方向已有链路，丢弃多余的{(inbound ? "入站" : "出站")}连接");
                link.Close();
                return;
            }

            var other = inbound ? _outLink : _inLink;
            // 排序键小的那台"作为发起方"保留自己发起的链路；两端结论必然指向同一条物理链路
            bool keepInitiator = iAmLower;
            bool preferThis = inbound != keepInitiator;

            if (other != null && !preferThis)
            {
                Log.Info($"同时建起两条链路 → 保留另一条（本机键 {myKey} < 对端键 {peerKey} = {iAmLower}，本条 inbound={inbound}）");
                link.Close();
                return;
            }
            if (other != null)
            {
                Log.Info($"同时建起两条链路 → 保留本条（本机键 {myKey} < 对端键 {peerKey} = {iAmLower}，本条 inbound={inbound}），关掉另一条");
                if (inbound) _outLink = null; else _inLink = null;
                if (ReferenceEquals(_link, other)) _link = null;   // 让它自己的 OnClosed 直接跳过收尾
                other.Close();
            }
            if (_link != null)
            {
                link.Close();
                return;
            }

            if (inbound) _inLink = link; else _outLink = link;
            _link = link;
            link.OnMsg += OnNetMsg;
            link.OnClosed += () => OnLinkClosed(link);
            link.Start();          // 放进锁内：后续进来的调用立刻能看到这条链路
            Connected = true;
            // —— 关键：在锁内就给自己打上"我是当前会话链路"的记号 ——
            // 两条链路几乎同时建起时，另一条线程可能紧接着把本链 Close 掉。
            // 只有拿到这个记号的那条才允许继续做会话初始化与日志输出，
            // 否则日志上会看到两条"已连接对端"（建链次数=2），让人误以为去重失效。
            if (!ReferenceEquals(_link, link)) return;
            _activeLink = link;
            replacedExisting = hadActive;
        }

        // 到这里为止仍可能是"刚被关掉"的情况（Close 发生在锁外），再核一次
        lock (_linkGate)
        {
            if (!ReferenceEquals(_link, link) || !ReferenceEquals(_activeLink, link)) return;
        }

        // 剪贴板「以最新为准」的时间轴基准：每次建立链路时归零重算
        _linkStartMs = Monotonic.Ms;
        _localClipMs = -1;
        _remoteClipMs = -1;
        _lastLocalClipStamp = -1;
        // 注意：_lastLocalClip（内容）**不清空**。它是"这台机器最近复制过什么"的本地事实，
        // 断了重连之后对端按 Ctrl+V 索取时仍然应该能拿到 —— 这正是"复制粘贴两侧共用"
        // 需要的：在 A 复制、断线、重连、到 B 按粘贴，也该粘得出来。

        // —— 会话角色仲裁：两端角色相同时（都主控/都被控）没人控制谁，按排序键让位 ——
        _roleSwap = ResolveRoleSwap(link, myKey, peerKey);

        if (IsServerRole)
        {
            _clientDisp = link.Peer.Disp;
            _sessionSide = Cfg.PeerSide;
        }
        else
        {
            // 被控端视角：主控端在对端配置方向的对面。
            // 顺带把主控端的屏幕几何也记下来 —— 反向控制时要用它算远端坐标。
            _clientDisp = link.Peer.Disp;
            _sessionSide = link.Peer.Side switch
            {
                "right" => "left",
                "left" => "right",
                "top" => "bottom",
                "bottom" => "top",
                _ => "left",
            };
        }

        // 反向控制：把本机（主控端）的「允许被控端控制」开关状态同步给对端，
        // 对端据此决定"移动鼠标时该不该申请接管"。每次建链都同步一次，改配置也重发。
        _revAllowed = false;
        _revPending = false;
        _revGranted = false;
        _revDeclinedThisSession = false;
        ReverseControlled = false;
        PostMsg(new Msg { T = "rev_cfg", Ok = Cfg.AllowReverseControl });

        if (replacedExisting)
        {
            // 同一会话换了底层链路（同时拨号去重的正常结果）：会话状态已按新链路重算完毕，
            // 但不能再报一次"已连接对端"——那不是新连接，重复宣告会让人误以为连了两次。
            Log.Info($"会话底层链路已切换（{link.Peer.Name}，同时拨号去重，仍为同一会话）");
            return;
        }

        Log.Info($"已连接对端 {link.Peer.Name}（角色={link.Peer.Role} 本端角色={(_roleSwap ? "已仲裁反转" : "按配置")} " +
                 $"本端作为{(IsServerRole ? "主控端" : "被控端")}，对端屏幕 {link.Peer.Disp.W}x{link.Peer.Disp.H}）");

        string tip = _roleSwap
            ? $"已连接 {link.Peer.Name}：两端角色相同，已按机器名自动把本机作为{(IsServerRole ? "主控端" : "被控端")}"
            : $"已连接 {link.Peer.Name}";
        UiUtil.Run(() => _toast?.Show(AppInfo.Title, tip, null, 3200));
    }

    /// <summary>两端角色相同时的确定性仲裁：排序键大的那台让位，改成相反角色。</summary>
    bool ResolveRoleSwap(SecureLink link, string myKey, string peerKey)
    {
        var peerRole = link.Peer.Role ?? "";
        if (peerRole.Length == 0) return false;        // 老版本对端没带角色信息，保持配置角色
        if (peerRole != Cfg.Role) return false;        // 一主一从，正常
        bool swap = string.CompareOrdinal(myKey, peerKey) > 0;
        if (swap)
            Log.Warn($"两端角色都是 {Cfg.Role}：按排序键让位，本机本次会话改当{(Cfg.Role == "server" ? "被控端" : "主控端")}");
        else
            Log.Warn($"两端角色都是 {Cfg.Role}：对端将让位，本机保持{Cfg.Role switch { "server" => "主控端", _ => "被控端" }}");
        return swap;
    }

    /// <summary>
    /// 本次会话里本机是不是"被控端"。判定口径与 <see cref="ResolveRoleSwap"/> 完全一致，
    /// 所以两端算出来的结论必然相反 —— 人工确认就由被控端那一侧负责。
    /// </summary>
    static bool SessionIsClient(string myRole, string peerRole, string myKey, string peerKey)
    {
        bool larger = string.CompareOrdinal(myKey, peerKey) > 0;
        bool isServer = (peerRole.Length == 0 || peerRole != myRole)
            ? myRole == "server"
            : (myRole == "server") ^ larger;
        return !isServer;
    }

    bool AmConfirmSide(SecureLink link)
    {
        RoleKeys(link, MyName, out var myKey, out var peerKey);
        return SessionIsClient(Cfg.Role, link.Peer.Role ?? "", myKey, peerKey);
    }

    /// <summary>等待对端确认时显示在状态栏的对端名（空=不在等待）。</summary>
    public volatile string WaitConfirmPeer = "";

    /// <summary>最近一次连接失败的原因（状态栏直接显示：密码不对 / 对方拒绝 / 没启动服务）。</summary>
    public volatile string LastConnectError = "";

    /// <summary>界面提示有变化（等待确认、连接阶段变化）时触发，让设置页立刻刷新。</summary>
    public event Action? OnUiHint;

    /// <summary>最近一次被本机用户拒绝的对端（"地址|名字"）与时刻，用于拒绝静默窗口。</summary>
    string _lastDeclinedKey = "";
    long _lastDeclinedAt = -100000;
    readonly object _declineGate = new();

    /// <summary>
    /// 刚拒绝过同一对端时，这段时间内不再弹确认框。
    /// 15 秒足够挡住对端 2~3 秒一次的重试风暴，又不至于妨碍用户想清楚后手动重连。
    /// </summary>
    const long DeclineSilenceMs = 15000;

    /// <summary>
    /// 被控端：对端密码验证通过后，问本机用户是否允许。
    /// 关闭「每次连接需确认」或服务已停止时直接给结论，不弹窗。
    /// </summary>
    async Task<bool> AskUserConfirm(SecureLink link)
    {
        if (!Cfg.ServiceOn)
        {
            Log.Info($"服务已停止，直接拒绝来自 {link.RemoteAddress} 的连接");
            return false;
        }
        if (!Cfg.ConfirmOnConnect)
        {
            Log.Info("已关闭连接确认，直接允许");
            return true;
        }
        var auto = Environment.GetEnvironmentVariable("MB_AUTO_CONFIRM");
        if (auto == "1" || auto == "deny")
        {
            // 自动化测试用：不弹窗，但仍完整走一遍 confirm_req / confirm_result 协议
            bool allow = auto == "1";
            Log.Info($"测试模式（MB_AUTO_CONFIRM={auto}）：自动{(allow ? "允许" : "拒绝")}本次连接");
            await Task.Delay(150);
            return allow;
        }
        // 支持 MB_AUTO_CONFIRM=<毫秒数>：先等这么久再允许，模拟"用户反复看了一阵才点允许"。
        //
        // 没有这个就没法覆盖真正出问题的那条路径 —— 用 allow/deny 立刻作答的话，
        // 握手在几百毫秒内就结束了对丝权利要求就结束了，压根碰不到"等待窗口够不够长"这件事。
        // 而真实场景里人类往往要十几秒到几十秒才点，旧版 3.5 秒一刀切正是在这里翻车的。
        if (auto != null && int.TryParse(auto, out int waitMs) && waitMs > 0)
        {
            Log.Info($"测试模式（MB_AUTO_CONFIRM={waitMs}ms）：模拟用户犹豫 {waitMs} 毫秒后允许");
            await Task.Delay(waitMs);
            return true;
        }

        // —— 拒绝静默窗口：别被对端的"重试风暴"刷屏 ——
        //
        // 对端（尤其是还在自动重试的一方）连不上时每隔 2~3 秒就会重连一次，每次都走到
        // 这里 → 确认框一个接一个弹出来，用户根本点不过来，观感就是"拒绝了还一直来问"。
        // 刚拒绝过同一对端时直接静默拒绝；过了窗口再放行（用户可能改主意了）。
        var key = link.RemoteAddress + "|" + (link.Peer.Name ?? "");
        lock (_declineGate)
        {
            if (key == _lastDeclinedKey && Monotonic.Ms - _lastDeclinedAt < DeclineSilenceMs)
            {
                Log.Info($"{(Monotonic.Ms - _lastDeclinedAt) / 1000.0:F0} 秒前刚拒绝过 {key}，" +
                         "本次静默拒绝（不再弹窗）");
                return false;
            }
        }

        var tcs = new TaskCompletionSource<bool>();
        UiUtil.Run(() =>
        {
            try
            {
                var w = new AskWindow(link.Peer.Name ?? "", link.RemoteAddress);
                w.Answer.ContinueWith(t => { _askOpen = false; tcs.TrySetResult(t.Result); }, TaskScheduler.Default);
                _askOpen = true;   // 弹窗开着 → 远控期间放行本机鼠标，用户才点得到按钮
                w.Show();
                Log.Info($"已弹出连接确认窗（来自 {link.Peer.Name} {link.RemoteAddress}）");
            }
            catch (Exception ex)
            {
                _askOpen = false;
                Log.Error("弹出连接确认窗失败，按拒绝处理", ex);
                tcs.TrySetResult(false);
            }
        });

        // 变量名不能叫 allow —— 上面测试模式分支里已经有一个同名的了，
        // C# 不允许内层与外层同名（哪怕外层声明在后面）。
        bool accepted;
        var done = await Task.WhenAny(tcs.Task, Task.Delay(70000));
        if (done != tcs.Task)
        {
            Log.Warn("本机用户一直没回应，按拒绝处理");
            accepted = false;
        }
        else accepted = await tcs.Task;

        if (!accepted)
        {
            // 记下来：接下来 DeclineSilenceMs 内对同一对端不再弹窗
            lock (_declineGate) { _lastDeclinedKey = key; _lastDeclinedAt = Monotonic.Ms; }
        }
        return accepted;
    }

    /// <summary>
    /// 把"只属于当前会话"的状态全部清零。断开与重新应用配置时都必须调用，否则会留下
    /// 僵尸状态：例如链断了但 <see cref="ReverseControlled"/> 还是 true，下一帧
    /// <c>OnHookMouseMove</c> 最前面那段"本机鼠标一动就收回"就会在**根本没连接**的情况下
    /// 反复触发；<c>_revDeclinedThisSession</c> 不为 false 则新会话里被控端永远不再申请反向控制。
    ///
    /// 注意三件事：
    ///   ① 本方法**不发报文、不碰 UI**（只清状态）——底层链路可能已经没了；
    ///   ② <c>ReleaseInjectedKeys</c> 必须调，否则链路断时对端注入的按键会一直"按着"；
    ///   ③ <c>_lastLocalClip</c> 内容**不清**（那是本机最近复制过什么的本地事实，
    ///      重连后对端索取剪贴板仍应能拿到），只清它与对端时间轴对齐用的 stamp。
    /// </summary>
    void ResetSessionState(string why)
    {
        _roleSwap = false;
        _lastTarget = "";
        WaitConfirmPeer = "";
        _pending = null;
        lock (_outGate) { _outgoing.Clear(); _outgoingVia.Clear(); }
        PeerDragActive = false;
        _dropHandover = false;
        _dropIn = null;

        // —— 反向控制四件套 + 会话内"不再自动申请"闸门 ——
        _revAllowed = false;
        _revPending = false;
        _revGranted = false;
        _revDeclinedThisSession = false;
        ReverseControlled = false;

        // 屏幕几何是"对端那台机器"的属性，会话结束就作废；否则下次连到别的机器会
        // 拿旧几何算坐标。
        _clientDisp = new DispInfo(0, 0, 0, 0);
        _armedOut = false;
        _armedBack = false;

        _xferBusy = false;
        _xferSending = false;

        if (RemoteActive)
        {
            RemoteActive = false;
            _hook.Mode = HookMode.Monitor;
        }
        if (BeingControlled)
        {
            BeingControlled = false;
            UiUtil.Run(() => _banner?.Hide());
        }
        // 键鼠回本机（若之前在对端），并把对端注入的按键补一遍抬起
        ReleaseInjectedKeys(why);
    }

    /// <summary>某条链路断开。dead 是具体断开的那条——重复链路的收尾不能把当前链路的状态清空。</summary>
    void OnLinkClosed(SecureLink dead)
    {
        lock (_linkGate)
        {
            if (ReferenceEquals(_inLink, dead)) _inLink = null;
            if (ReferenceEquals(_outLink, dead)) _outLink = null;
            if (!ReferenceEquals(_link, dead)) return;   // 不是当前在用的那条，忽略
            _link = null;
            if (ReferenceEquals(_activeLink, dead)) _activeLink = null;
            // 关键：Connected 必须**在锁内**归零。
            // 它和 _link 是一对——只要 _link 有效，别的线程读 Connected 就该为真。
            // 放到锁外写的话，会出现"新链路刚在 OnLinkUp 锁内把 Connected 置 true，
            // 紧接着这条旧链路的锁外 Connected=false 又把它抹掉"的竞态：界面显示未连接、
            // ClientLoop 以为该拨号，于是又建一条链路 —— 现象是"连上后立刻掉线、反复重连"。
            // 只在确认没有新链路接管时才归零，避免误伤刚接手的新会话。
            if (_link == null && _activeLink == null) Connected = false;
        }
        bool wasRemote = RemoteActive;
        ResetSessionState("链路断开");
        // 连接断了还在传的，标成"已中断"，免得台账里永远停在"进行中"
        foreach (var r in TransferJournal.Snapshot().Where(x => x.IsRunning))
            TransferJournal.Finish(r.Id, XferState.Interrupted, null, "连接断开");
        UiUtil.Run(() => _toast?.Show(AppInfo.Title,
            wasRemote ? "连接断开，已回到本机" : "连接已断开", null, wasRemote ? 3000 : 2500));
        Log.Warn("连接断开");
    }

    Task SendViaLinkAsync(Msg m, byte[]? p)
    {
        var l = _link;
        if (l == null || !l.Alive) throw new IOException("未连接");
        return l.SendAsync(m, p);
    }

    // —— 键盘：记录被对端注入且仍处于按下状态的键 ——
    // 主控端在 Capture 模式下会把物理按键一路转发过来；如果这时鼠标移回本机、或者链路突然断开，
    // 对端就收不到对应的抬起事件，表现是"某个键像被一直按住"（Ctrl/Shift 卡住最明显）。
    // 所以在每次切换/断线时补一遍抬起。
    readonly object _keyGate = new();
    readonly Dictionary<int, (int Scan, bool Ext)> _injectedDownKeys = new();
    readonly HashSet<int> _injectedDownBtns = new();   // 仍按下的鼠标键（0=左 1=右 2=中 3=X1 4=X2）

    void ReleaseInjectedKeys(string why)
    {
        List<KeyValuePair<int, (int Scan, bool Ext)>> held;
        List<int> heldBtns;
        lock (_keyGate)
        {
            held = _injectedDownKeys.ToList();
            heldBtns = _injectedDownBtns.ToList();
            _injectedDownKeys.Clear();
            _injectedDownBtns.Clear();
        }
        if (held.Count == 0 && heldBtns.Count == 0) return;
        foreach (var kv in held)
        {
            try { Injector.Key(kv.Key, kv.Value.Scan, kv.Value.Ext, false); } catch { }
        }
        foreach (var b in heldBtns)
        {
            try { Injector.Button(b, false); } catch { }
        }
        Log.Info($"已补发 {held.Count} 个按键 / {heldBtns.Count} 个鼠标键抬起（{why}），避免对端卡键");
    }

    void PostMsg(Msg m) => _link?.Post(m);

    void SendDispChange() => PostMsg(new Msg { T = "disp_change", X = MyDisp().X, Y = MyDisp().Y, W = MyDisp().W, H = MyDisp().H });

    void StartDiscovery()
    {
        if (!Cfg.AutoDiscover) return;
        _disc.OnPeerSeen -= KickConnect;      // 防重复订阅（ApplyConfig 会反复 Start）
        _disc.OnPeerSeen += KickConnect;
        _disc.Start(Cfg.Role, MyName, Cfg.Port);
    }

    void KickConnect()
    {
        if (!Connected) _connectKick.Set();   // 听到主控端出现 → 连接循环立刻去连
    }

    public Discovery Disc => _disc;

    // ================= 网络消息 =================

    void OnNetMsg(Msg m, byte[]? p)
    {
        try
        {
            switch (m.T)
            {
                case "remote_on":
                    BeingControlled = true;
                    ReverseControlled = m.Vk == 1;      // Vk=1 表示这是"反向"接管
                    lock (_keyGate) { _injectedDownKeys.Clear(); _injectedDownBtns.Clear(); }
                    Log.Info(ReverseControlled ? "对端（被控端）反向接管本机" : "对端开始控制本机");
                    // 屏幕右上角亮出"正在被控制"横幅，用户随时能一键收回控制权
                    UiUtil.Run(() => _banner?.ShowControlledBy(PeerName, reverse: ReverseControlled, pauseKey: HotkeyHint("D")));
                    break;
                case "remote_off":
                    BeingControlled = false;
                    ReverseControlled = false;
                    ReleaseInjectedKeys("对端已回到本机");
                    UiUtil.Run(() => _banner?.Hide());
                    // 本机正在控制对端时对方收回了控制权，本机也要切回自己
                    if (RemoteActive)
                    {
                        UiUtil.Run(() =>
                        {
                            SwitchToLocal();
                            _toast?.Show("已暂停接管", "对端收回了控制权，鼠标已回到本机。", null, 2400);
                        });
                    }
                    Log.Info("对端已回到本机");
                    break;
                case "drag_active":
                    PeerDragActive = m.On;
                    break;
                case "disp_change":
                    // 任何一侧都可能需要知道对端屏幕几何（正向由主控端用，反向由被控端用）
                    _clientDisp = new DispInfo(m.X, m.Y, m.W, m.H);
                    break;

                case "mouse_abs": if (BeingControlled) Injector.MouseAbs(m.Nx, m.Ny); break;
                case "mouse_btn":
                    if (m.Btn == 0 && !m.Down && _dropIn is { Committed: false }) UiUtil.Run(CommitDropIn);
                    if (BeingControlled)
                    {
                        // 和键盘一样记录"仍按下的鼠标键"：切回本机 / 断线时补发抬起。
                        // 否则对端在我们拖拽/框选途中拔线，左键会一直"按着"——表现为
                        // 桌面像卡住、点哪儿都在拖框。键盘早就补了抬起，鼠标键以前漏了。
                        lock (_keyGate)
                        {
                            if (m.Down) _injectedDownBtns.Add(m.Btn);
                            else _injectedDownBtns.Remove(m.Btn);
                        }
                        Injector.Button(m.Btn, m.Down);
                    }
                    break;
                case "wheel": if (BeingControlled) Injector.Wheel(m.Dx, m.Dy); break;
                case "key":
                    if (BeingControlled)
                    {
                        // 记录按下的键：切回本机 / 断线时统一补发抬起，避免对端"卡键"
                        lock (_keyGate)
                        {
                            if (m.Down) _injectedDownKeys[m.Vk] = (m.Scan, m.Ext);
                            else _injectedDownKeys.Remove(m.Vk);
                        }
                        Injector.Key(m.Vk, m.Scan, m.Ext, m.Down);
                    }
                    break;

                case "clip_meta": OnClipMeta(m); break;
                case "clip_data": OnClipData(m, p); break;
                // 对端按了 Ctrl+V，向本机索取"当前最新剪贴板"。
                // 这就是"两侧复制粘贴共用"的关键一环：无论用户在哪一台按粘贴，
                // 都会拿到两台机器里**最新的那一份**，而不是各自本地的旧内容。
                case "clip_req": OnClipReq(m); break;

                // —— 反向控制（被控端反过来控制主控端）——
                case "rev_req": OnRevRequest(m); break;                 // 主控端：对方申请接管
                case "rev_result": OnRevResult(m.Ok, m.MsgText); break;  // 被控端：申请有结果了
                case "rev_off": OnRevOff(); break;
                case "rev_cfg":
                    // 主控端同步"允许被控端控制"的开关状态，让被控端知道自己能不能申请
                    _revAllowed = m.Ok;
                    if (!_revAllowed)
                    {
                        _revPending = false;
                        _revGranted = false;
                        _revDeclinedThisSession = false;
                    }
                    Log.Info($"反向控制许可已同步：{(m.Ok ? "允许" : "不允许")}");
                    break;

                case "drop_begin": OnDropBegin(m); break;
                case "drop_commit": UiUtil.Run(CommitDropIn); break;
                case "drop_cancel": OnDropCancel(); break;

                case "file_req":
                    // 在读循环线程上，必须持锁取值（items 是 List 引用，取出后即可解锁，
                    // 后面 SendFilesAsync 只读这份 List，不再碰字典）
                    List<FileScanner.Item>? reqItems = null;
                    string reqVia = "剪贴板";
                    lock (_outGate)
                    {
                        if (_outgoing.TryGetValue(m.Id, out var it))
                        {
                            reqItems = it;
                            reqVia = _outgoingVia.TryGetValue(m.Id, out var v) ? v : "剪贴板";
                        }
                    }
                    if (reqItems != null)
                    {
                        _xferSending = true;
                        _xferBusy = true;
                        _xferSent = 0;
                        _xferTotal = Math.Max(reqItems.Sum(x => x.Size), 1);
                        int cnt = reqItems.Count(x => !x.IsDir);
                        _xferLabel = $"发送 {cnt} 个文件";
                        TransferJournal.Begin(m.Id, true, _xferTotal, cnt, _xferLabel, PeerName, reqVia);
                        _ = Task.Run(() => _xfer.SendFilesAsync(m.Id, reqItems));
                    }
                    break;
                case "file_hdr":
                case "file_chunk":
                case "file_done":
                case "xfer_done":
                case "xfer_fail":
                    _xfer.OnMsg(m, p);
                    break;

                case "bye": _link?.Close(); break;
            }
        }
        catch (Exception ex) { Log.Error($"处理消息 {m.T} 异常", ex); }
    }

    // ================= 边缘切换 =================
    // 主控端：鼠标推到"对端那一侧"的边缘 → 去控制对端（RemoteActive）。
    // 被控端：只有拿到反向控制授权后，才能反过来去控制主控端（同样用 RemoteActive 表示）。

    void OnHookMouseMove(int x, int y)
    {
        int dx = x - _lastPhysX, dy = y - _lastPhysY;
        _lastPhysX = x; _lastPhysY = y;

        if (!IsServerRole)
        {
            OnClientMouseMove(x, y, dx, dy);
            return;
        }

        // —— 反向控制中（被控端正控制本机）：本机鼠标一动，立刻收回控制权 ——
        // 这就是用户说的"控制哪一台由操作哪个鼠标键盘来确认"：谁动自己的鼠标，控制权就归谁。
        // 必须放在最前面 —— 否则会掉进下面的"边缘推出去"分支，用户在本机动鼠标不但收不回
        // 控制权，反而可能把键鼠又推给对端。
        if (ReverseControlled)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 2) return;   // 极微小抖动忽略，避免误收
            Log.Info("本机鼠标移动 → 收回控制权");
            UiUtil.Run(SwitchToLocal);
            return;
        }

        if (!RemoteActive)
        {
            if (!Connected || LockToScreen || !Cfg.TakeoverEnabled || _clientDisp.W <= 0) { ArmOutIfAway(x, y); return; }
            var vs = Native.VirtualScreen();
            bool atEdge = _sessionSide switch
            {
                "right" => x >= vs.X + vs.W - 1,
                "left" => x <= vs.X,
                "top" => y <= vs.Y,
                "bottom" => y >= vs.Y + vs.H - 1,
                _ => false,
            };
            if (!atEdge) { ArmOutIfAway(x, y); _edgeSince = 0; return; }
            if (!_armedOut) return;
            if (Cfg.SwitchDelayMs > 0)
            {
                if (_edgeSince == 0) { _edgeSince = Monotonic.Ms; return; }
                if (Monotonic.Ms - _edgeSince < Cfg.SwitchDelayMs) return;
            }
            // 正在拖本机文件时，不直接跨屏（由 DragWatcher 触发跨机拖拽投递）
            if (_dragActiveLocal && Cfg.DragDropAcrossMachines) return;
            SwitchToRemote(y);
        }
        else
        {
            if (_clientDisp.W <= 0) return;
            // 自家确认弹窗（连接确认/反向控制请求）开着：钩子已放行本机鼠标，
            // 这里也不能再转发位移或把光标钉回中心，否则用户点不到「允许」按钮。
            if (_askOpen) return;
            // 这里的"自家窗口就弹回本机"判定已于 v1.6.9 移除 ——
            // 设置页一旦打开，它就**永远**是前台窗口，于是用户刚把鼠标推到对端、下一帧就被
            // 判成"前台是自家界面"、立刻弹回，实测 8 毫秒一个来回（见 mb-20261013.log
            // 的 00:45:34~00:45:42 那段），主观感受就是"打开设置页后软件卡住了、跳不过去"。
            // 远控期间想操作本机界面，走 Ctrl+Alt+1 或把鼠标推回返回侧边缘即可。
            if (BugOwnWindow && Native.IsOwnProcessForeground())
            {
                SwitchToLocal();
                return;
            }
            // 物理光标在 Capture 模式下仍会移动，撞到本机屏幕边缘后就停住、dx/dy 变 0，
            // 于是 _cx 不再累加 —— 表现为"指针粘滞在局部推不动"。做法：远端期间把物理
            // 光标一直钳在屏幕中心，这样任何方向的位移都能被读到，且永远不会饱和。
            ClampPhysicalCursorToCenter();
            _cx += dx; _cy += dy;
            var cd = _clientDisp;
            _cx = MathX.Clamp(_cx, cd.X, cd.X + cd.W - 1);
            _cy = MathX.Clamp(_cy, cd.Y, cd.Y + cd.H - 1);
            // 回程：只要指针贴到"返回侧"边缘就放回本机。原先要求 _armedBack（先离开边缘
            // 4px 才武装）再精确命中 1.5px 边缘，实际几乎不可能命中，用户感觉"回不去"。
            // 宽限 300ms：刚进场时 _cx 就停在返回边缘（cd.X）且"进场即武装"，
            // 此时切换动作自身产生的零增量鼠标事件（SetCursorPos 到屏幕中心）会
            // 立刻命中回程判定、把控制权弹回本机 —— 用户"慢慢蹭过边缘"时必现
            // "推过去又弹回来"。给 300ms 让第一个有效位移先把 _cx 推离边缘。
            bool atBackEdge = _sessionSide switch
            {
                "right" => _cx <= cd.X + 1.5,
                "left" => _cx >= cd.X + cd.W - 2.5,
                "top" => _cy >= cd.Y + cd.H - 2.5,
                "bottom" => _cy <= cd.Y + 1.5,
                _ => false,
            };
            // 宽限期内不累计：切换瞬间残留的输入事件会和中心钳位拉扯，
            // 若让它们预支去抖计数，宽限一过就立刻弹回，去抖就白做了。
            // 另要求本帧位移"像人手"（单帧 ≤120px）：外力瞬移（其他程序 SetCursorPos、
            // 远程桌面改分辨率、虚拟机同步光标）会造成上千像素的假位移，人手再快
            // 也达不到 —— 瞬移帧不得参与回程计数，否则会被误判成"用户贴边想回来"。
            bool humanStep = Math.Abs(dx) <= 120 && Math.Abs(dy) <= 120;
            _backEdgeHits = atBackEdge && humanStep && Monotonic.Ms - _remoteEnterMs >= 300
                ? _backEdgeHits + 1 : 0;
            if (atBackEdge && _backEdgeHits >= 4 && _armedBack && Monotonic.Ms - _remoteEnterMs >= 300
                && !PeerDragActive && !LockToScreen && !_dropHandover)
            { SwitchToLocal(); return; }
            // 离边缘超过 6px 就武装返回（进远端时已置 true，无需先"离开"一次）
            if (!atBackEdge && DistanceFromBackEdge(cd) > 6) _armedBack = true;

            double nx = (_cx - cd.X) / Math.Max(1, cd.W - 1);
            double ny = (_cy - cd.Y) / Math.Max(1, cd.H - 1);
            PostMsg(new Msg { T = "mouse_abs", Nx = nx, Ny = ny });
        }
    }

    /// <summary>
    /// 远端期间把物理光标钉在虚拟桌面中心。物理光标被 Capture 钩子吞掉后不会影响
    /// 对端（对端位置由 mouse_abs 决定），但必须让它停在屏幕中心，否则撞边后增量恒为 0。
    /// </summary>
    void ClampPhysicalCursorToCenter()
    {
        // 不能只用"虚拟桌面包围盒的中心"：L 形/错位排列的多显示器里，包围盒中心
        // 可能落在**没有屏幕覆盖**的空洞上，SetCursorPos 到那里会被系统吸附到最近的
        // 屏幕边缘，撞边后 dx 依然恒为 0 —— 就是老版本那种"鼠标卡住不动"的复现路径。
        // 正确做法：选一块真实存在的屏幕（优先含包围盒中心的那块，否则主屏），取它的中心。
        int cx, cy;
        var center = Native.ScreenContaining(
            Native.VirtualScreen().X + Native.VirtualScreen().W / 2,
            Native.VirtualScreen().Y + Native.VirtualScreen().H / 2);
        if (center.W > 0 && center.H > 0)
        {
            cx = center.X + center.W / 2; cy = center.Y + center.H / 2;
        }
        else
        {
            var vs = Native.VirtualScreen();
            cx = vs.X + vs.W / 2; cy = vs.Y + vs.H / 2;
        }
        if (_lastPhysX == cx && _lastPhysY == cy) return;   // 已经在中心就不重复调用
        Native.SetCursorPos(cx, cy);
        _lastPhysX = cx; _lastPhysY = cy;
    }

    void ArmOutIfAway(int x, int y)
    {
        var vs = Native.VirtualScreen();
        double d = _sessionSide switch
        {
            "right" => vs.X + vs.W - 1 - x,
            "left" => x - vs.X,
            "top" => y - vs.Y,
            "bottom" => vs.Y + vs.H - 1 - y,
            _ => 999,
        };
        if (d > 4) _armedOut = true;
    }

    double DistanceFromBackEdge(DispInfo cd) => _sessionSide switch
    {
        "right" => _cx - cd.X,
        "left" => cd.X + cd.W - 1 - _cx,
        "top" => cd.Y + cd.H - 1 - _cy,
        "bottom" => _cy - cd.Y,
        _ => 999,
    };

    // ================= 反向控制（被控端反过来控制主控端） =================
    //
    // 需求场景：用户平时坐在主控端推键鼠过去控制对端；但有时人就在被控端这台电脑前
    // （比如被控端是笔记本放在手边），此时想反过来去操作主控端。
    //
    // 设计原则（对齐"控制哪一台由操作哪个鼠标键盘来确认"）：
    //   · 反向控制必须**先获得主控端许可**（主控端设置页的「允许被控端控制」开关）；
    //   · 许可后，被控端**移动自己的鼠标**即视为"我要接管" —— 这就是"哪个鼠标在动"；
    //   · 接管期间主控端进入被控态（右上角红横幅 + 暂停热键可随时收回）；
    //   · 主控端**移动自己的鼠标**即视为"我要收回"，控制权立刻回来。

    /// <summary>被控端：是否已拿到主控端的反向控制许可（对端 AllowReverseControl 的开值）。</summary>
    volatile bool _revAllowed;
    /// <summary>被控端：已发出 rev_req，正在等主控端回应。</summary>
    volatile bool _revPending;
    /// <summary>
    /// 被控端：主控端已同意本次反向控制，就等本机鼠标动一下就真的接管。
    ///
    /// 必须和 <see cref="_revAllowed"/> 分开：_revAllowed 表示"对端开着许可开关"（长期状态），
    /// _revGranted 表示"这次申请已经被批准、待接管"（一次性）。早前把两者混成一个标志，
    /// 结果 OnRevResult 批准后又回到"有许可但没接管"的分支 → 每帧鼠标移动都重发一次
    /// rev_req（实测 8ms 一条，日志瞬间刷屏），两边永远停在"已获许可，等待本机鼠标动作"。
    /// </summary>
    volatile bool _revGranted;
    /// <summary>主控端：当前正被被控端反向控制。</summary>
    public volatile bool ReverseControlled;
    /// <summary>被控端：本次会话是否主动放弃过反向控制（避免鼠标一抖就再申请，刷屏弹窗）。</summary>
    volatile bool _revDeclinedThisSession;

    /// <summary>
    /// 主控端改动了「允许被控端控制」开关 → 就地同步给对端，不需要重连。
    /// 关掉许可时顺便把自己这边的被控状态收干净（对方正在控制就断开）。
    /// </summary>
    public void NotifyReverseAllowedChanged()
    {
        PostMsg(new Msg { T = "rev_cfg", Ok = Cfg.AllowReverseControl });
        Log.Info($"反向控制许可 → {(Cfg.AllowReverseControl ? "允许" : "不允许")}");
        if (!Cfg.AllowReverseControl && ReverseControlled)
        {
            ReverseControlled = false;
            BeingControlled = false;
            ReleaseInjectedKeys("主控端关闭了反向控制许可");
            lock (_keyGate) { _injectedDownKeys.Clear(); _injectedDownBtns.Clear(); }
            UiUtil.Run(() => _banner?.Hide());
            PostMsg(new Msg { T = "rev_off" });
        }    }

    /// <summary>本机（主控端）是否开启了「允许被控端控制」。</summary>
    public bool AllowReverseControl => Cfg.AllowReverseControl;

    /// <summary>被控端：对端（主控端）是否已允许本机反向控制 —— 设置页徽标用。</summary>
    public bool ReverseAllowedByPeer => _revAllowed && !IsServerRole;

    /// <summary>本机是否正处在"反向控制对端"的状态（被控端拿到了实际控制权）—— 指示器/界面用。</summary>
    public bool ReverseActive => RemoteActive && !IsServerRole;

    /// <summary>本机是不是"当前正在控制对端"的那一方（正向或反向都算）。</summary>
    public bool Controlling => RemoteActive;

    /// <summary>
    /// 被控端侧的鼠标移动处理。只有三种情况要做事：
    ///   ① 正在反向控制主控端 → 转发坐标（与主控端正向控制对端时完全对称）；
    ///   ② 有许可但还没接管，且用户真的在动鼠标 → 申请接管（"哪个鼠标在动"就是这个）；
    ///   ③ 其它情况什么都不做，保持本机正常操作。
    /// </summary>
    void OnClientMouseMove(int x, int y, int dx, int dy)
    {
        if (RemoteActive)
        {
            // —— 反向接管中：和主控端一样，把物理光标钉在中心，按增量推远端坐标 ——
            if (_clientDisp.W <= 0) return;
            // 同上：确认弹窗开着时鼠标归本机（v1.6.9）。
            if (_askOpen) return;
            // 同上（v1.6.9）：反向接管期间也不再按"前台是自家窗口"强制弹回，理由一致。
            if (BugOwnWindow && Native.IsOwnProcessForeground()) { SwitchToLocal(); return; }
            ClampPhysicalCursorToCenter();
            _cx += dx; _cy += dy;
            var cd = _clientDisp;
            _cx = MathX.Clamp(_cx, cd.X, cd.X + cd.W - 1);
            _cy = MathX.Clamp(_cy, cd.Y, cd.Y + cd.H - 1);

            bool atBackEdge = _sessionSide switch
            {
                "right" => _cx <= cd.X + 1.5,
                "left" => _cx >= cd.X + cd.W - 2.5,
                "top" => _cy >= cd.Y + cd.H - 2.5,
                "bottom" => _cy <= cd.Y + 1.5,
                _ => false,
            };
            // 宽限期内不累计：切换瞬间残留的输入事件会和中心钳位拉扯，
            // 若让它们预支去抖计数，宽限一过就立刻弹回，去抖就白做了。
            // 另要求本帧位移"像人手"（单帧 ≤120px）：外力瞬移（其他程序 SetCursorPos、
            // 远程桌面改分辨率、虚拟机同步光标）会造成上千像素的假位移，人手再快
            // 也达不到 —— 瞬移帧不得参与回程计数，否则会被误判成"用户贴边想回来"。
            bool humanStep = Math.Abs(dx) <= 120 && Math.Abs(dy) <= 120;
            _backEdgeHits = atBackEdge && humanStep && Monotonic.Ms - _remoteEnterMs >= 300
                ? _backEdgeHits + 1 : 0;
            if (atBackEdge && _backEdgeHits >= 4 && _armedBack && Monotonic.Ms - _remoteEnterMs >= 300
                && !LockToScreen && !_dropHandover) { SwitchToLocal(); return; }
            if (!atBackEdge && DistanceFromBackEdge(cd) > 6) _armedBack = true;

            double nx = (_cx - cd.X) / Math.Max(1, cd.W - 1);
            double ny = (_cy - cd.Y) / Math.Max(1, cd.H - 1);
            PostMsg(new Msg { T = "mouse_abs", Nx = nx, Ny = ny });
            return;
        }

        // —— 已获批准：鼠标一动就真的接管（"控制哪一台由操作哪个鼠标键盘确认"） ——
        if (_revGranted && Connected && Cfg.TakeoverEnabled && !_revDeclinedThisSession)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 4) return;   // 抖动不算"想动手"
            _revGranted = false;
            SwitchToRemote(0);
            return;
        }

        // —— 申请反向接管：有许可、已连接、接管没被暂停、且这次会话还没被拒过 ——
        if (_revAllowed && Connected && Cfg.TakeoverEnabled && !_revPending && !_revDeclinedThisSession
            && BeingControlled == false && _clientDisp.W > 0)
        {
            // "操作哪个鼠标键盘就控制哪一台"：用户**动了自己的鼠标**，就说明他要用这台，
            // 于是替他去申请控制主控端。位移太小（抖动）不算。
            if (Math.Abs(dx) + Math.Abs(dy) < 4) return;
            _revPending = true;
            PostMsg(new Msg { T = "rev_req", Name = MyName });
            UiUtil.Run(() => _toast?.Show("请求反向控制",
                $"正在向 {PeerName} 申请鼠标键盘控制权…", null, 2200));
            Log.Info("已发出反向控制申请 rev_req");
            return;
        }

        // —— 正在接管中的人想收回：鼠标移到"返回侧"边缘就还原 ——
        // （这与主控端的回程逻辑对称；见 SwitchToLocal 里对两侧的统一处理）
    }

    /// <summary>被控端：收到主控端对反向控制申请的答复。</summary>
    void OnRevResult(bool allow, string? reason)
    {
        _revPending = false;
        if (!allow)
        {
            _revAllowed = false;
            _revGranted = false;
            _revDeclinedThisSession = true;
            Log.Info("反向控制申请被拒绝：" + (reason ?? ""));
            UiUtil.Run(() => _toast?.Show("反向控制被拒绝",
                string.IsNullOrEmpty(reason) ? "主控端没有允许本次反向控制" : reason!, null, 3200));
            return;
        }
        // 批准：置 _revGranted，等本机鼠标动一下就真的接管（见 OnClientMouseMove）。
        // 注意这里**只**置 _revGranted、不再重发 rev_req —— 否则会每帧刷一条报文。
        _revGranted = true;
        Log.Info("反向控制已获许可，本机鼠标一动即接管");
        UiUtil.Run(() => _toast?.Show("可以反向控制",
            $"已允许。移动本机鼠标即可控制 {PeerName}；把鼠标推回边缘退出。", null, 3600));
    }

    /// <summary>主控端：收到被控端的反向控制申请。</summary>
    void OnRevRequest(Msg m)
    {
        if (!Cfg.AllowReverseControl)
        {
            PostMsg(new Msg { T = "rev_result", Ok = false, MsgText = "主控端未开启「允许被控端控制」" });
            Log.Info("拒绝反向控制申请：本机未开启许可");
            return;
        }
        if (!Cfg.TakeoverEnabled)
        {
            PostMsg(new Msg { T = "rev_result", Ok = false, MsgText = "主控端已暂停接管" });
            return;
        }
        if (RemoteActive)
        {
            // 本机正推着键鼠在对端，物理上没法同时被控 —— 先拉回本机再谈
            UiUtil.Run(SwitchToLocal);
        }

        string who = m.Name ?? PeerName;
        if (!Cfg.ConfirmReverseControl)
        {
            AllowReverse(who, auto: true);
            return;
        }

        // 弹窗询问。
        //
        // **绝不能在这里同步等待**：本方法运行在 SecureLink 的读循环线程上，
        // 一阻塞就收不到任何后续报文（包括心跳 ping），2 秒内对端就会判定心跳超时断链。
        // 原来的 GetAwaiter().GetResult() 会在"用户 60 秒不点弹窗"时把链路活活等死。
        // 现在改成彻底异步：弹窗照常弹，答复通过 ContinueWith 回调回来，读循环立刻返回。
        var tcs = new TaskCompletionSource<bool>();
        UiUtil.Run(() =>
        {
            try
            {
                var w = new AskWindow(who, "请求反向控制本机鼠标键盘", 45, reverse: true);
                w.Answer.ContinueWith(t => { _askOpen = false; tcs.TrySetResult(t.Result); }, TaskScheduler.Default);
                _askOpen = true;   // 同连接确认窗：弹窗开着 → 放行本机鼠标
                w.Show();
            }
            catch (Exception ex)
            {
                _askOpen = false;
                Log.Error("弹出反向控制确认窗失败，按拒绝处理", ex);
                tcs.TrySetResult(false);
            }
        });

        // 超时与用户答复竞速，全部在后台线程完成，不占用读循环。
        _ = Task.Run(async () =>
        {
            try
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(60000)).ConfigureAwait(false);
                bool allow = done == tcs.Task && tcs.Task.Result;
                if (allow) AllowReverse(who, auto: false);
                else
                {
                    PostMsg(new Msg { T = "rev_result", Ok = false, MsgText = "主控端拒绝了本次反向控制" });
                    Log.Info("本机用户拒绝了反向控制申请（或超时未响应）");
                }
            }
            catch (Exception ex) { Log.Error("处理反向控制答复失败", ex); }
        });
    }

    /// <summary>主控端：同意把控制权交给被控端。</summary>
    void AllowReverse(string who, bool auto)
    {
        ReverseControlled = true;
        BeingControlled = true;
        lock (_keyGate) { _injectedDownKeys.Clear(); _injectedDownBtns.Clear(); }
        PostMsg(new Msg { T = "rev_result", Ok = true });
        UiUtil.Run(() => _banner?.ShowControlledBy(who, reverse: true, pauseKey: HotkeyHint("D")));
        Log.Info($"已允许「{who}」反向控制本机（{(auto ? "免确认" : "人工确认")}）");
        UiUtil.Run(() => _toast?.Show("已交出控制权",
            auto ? $"「{who}」可以控制本机（可在设置里关掉免确认）" : $"「{who}」现在可以控制本机", null, 3000));
    }

    /// <summary>
    /// 收到「反向控制结束」。
    ///
    /// 两个方向都要处理，因为 rev_off 是双向的：
    ///   · 本机是主控端 → 对端（被控端）主动退出了反向控制，把被控状态收干净；
    ///   · 本机是被控端 → 主控端动了自己的鼠标收回控制权，本机必须**真的退出 Capture**
    ///     （否则物理键鼠还全被吞着转发，用户会以为程序卡死了）。
    /// </summary>
    void OnRevOff()
    {
        if (!IsServerRole)
        {
            // 本机是被控端：结束反向接管，回到本机
            if (!RemoteActive) { _revGranted = false; _revPending = false; return; }
            _revGranted = false;
            _revPending = false;
            _revDeclinedThisSession = true;   // 被收回后本次会话不再自动重新申请
            Log.Info("主控端收回了控制权 → 退出反向控制");
            UiUtil.Run(SwitchToLocal);
            return;
        }

        if (!ReverseControlled && !BeingControlled) return;
        ReverseControlled = false;
        BeingControlled = false;
        ReleaseInjectedKeys("对端退出了反向控制");
        lock (_keyGate) { _injectedDownKeys.Clear(); _injectedDownBtns.Clear(); }
        UiUtil.Run(() => _banner?.Hide());
        Log.Info("对端退出了反向控制");
    }

    /// <summary>
    /// 接管对端键鼠（正向：主控端推过去；反向：被控端拿到许可后接管主控端）。
    /// 两侧共用同一套状态：RemoteActive=本机在控制对端，对端则进入 BeingControlled。
    /// </summary>
    void SwitchToRemote(int serverY)
    {
        var vs = Native.VirtualScreen();
        var cd = _clientDisp;
        bool reverse = !IsServerRole;      // 被控端发起 = 反向控制
        RemoteActive = true;
        _hook.Mode = HookMode.Capture;
        _armedOut = false; _armedBack = false; _edgeSince = 0;
        _remoteEnterMs = Monotonic.Ms;

        double nyN = vs.H > 0 ? (serverY - vs.Y) / (double)vs.H : 0.5;
        _cx = cd.X;
        _cy = cd.Y + nyN * (cd.H - 1);
        // 物理光标挪到屏幕中心：远端期间它会一直钉在那里，保证位移增量始终有效
        int pcx = vs.X + vs.W / 2, pcy = vs.Y + vs.H / 2;
        Native.SetCursorPos(pcx, pcy);
        _lastPhysX = pcx; _lastPhysY = pcy;
        _armedBack = true;              // 进场即武装返回，指针一碰到返回侧边缘就能回来
        PostMsg(new Msg { T = "remote_on", Nx = 0, Ny = nyN, Vk = reverse ? 1 : 0 });
        Log.Info(reverse ? "→ 反向接管主控端" : "→ 切换到对端");
        UiUtil.Run(() => _toast?.Show(reverse ? "已接管" : "已切换",
            reverse ? $"正在控制 {PeerName}（反向；鼠标移回边缘退出）"
                    : $"正在控制 {PeerName}（鼠标移回对端边缘返回）", null, 1800));
    }

    /// <summary>把键鼠交还对端（正向/反向通用），本机回到 Monitor 模式。</summary>
    void SwitchToLocal()
    {
        var vs = Native.VirtualScreen();
        var cd = _clientDisp;
        // 三种情形要分开处理，不能只看角色：
        //   ① 被控端主动退出反向控制        （本机是被控端 + RemoteActive）
        //   ② 主控端动鼠标、从被控端手里收回（本机是主控端 + ReverseControlled）
        //   ③ 正向控制回程                  （本机是主控端 + RemoteActive）
        bool revExit = !IsServerRole && RemoteActive;       // ①：我方是被控端且在反向控制对端
        bool revReclaim = IsServerRole && ReverseControlled; // ②：我方是被控端在反向控制我
        RemoteActive = false;
        _hook.Mode = HookMode.Monitor;
        _armedOut = false; _armedBack = false;

        double nyN = cd.H > 1 ? (_cy - cd.Y) / (cd.H - 1) : 0.5;
        double nxN = cd.W > 1 ? (_cx - cd.X) / (cd.W - 1) : 0.5;
        int sx = _sessionSide switch
        {
            "right" => vs.X + vs.W - 1,
            "left" => vs.X,
            "top" => vs.X + (int)(nxN * (vs.W - 1)),
            "bottom" => vs.X + (int)(nxN * (vs.W - 1)),
            _ => vs.X + vs.W - 1,
        };
        int sy = _sessionSide switch
        {
            "top" => vs.Y,
            "bottom" => vs.Y + vs.H - 1,
            _ => vs.Y + (int)(nyN * (vs.H - 1)),
        };

        if (revExit)
        {
            // 反向退出要单独告诉主控端"我不控你了"，并且本次会话不再自动申请，
            // 否则用户一放下鼠标就被立刻重新接管，体验上像"甩不掉"。
            PostMsg(new Msg { T = "rev_off" });
            _revDeclinedThisSession = true;
            _revGranted = false;
            _revPending = false;
        }
        else if (revReclaim)
        {
            // 我方（主控端）动了自己的鼠标 → 收回控制权并通知被控端"你别控了"。
            // 走 rev_off 而不是 remote_off：被控端据此结束自己的 Capture 模式、解除按键注入，
            // 否则它那边会一直以为还在控制本机，键盘也卡在对端。
            PostMsg(new Msg { T = "rev_off" });
            ReverseControlled = false;
            BeingControlled = false;
            ReleaseInjectedKeys("主控端收回控制权");
            lock (_keyGate) { _injectedDownKeys.Clear(); _injectedDownBtns.Clear(); }
            UiUtil.Run(() => _banner?.Hide());
        }
        else PostMsg(new Msg { T = "remote_off" });

        Native.SetCursorPos(sx, sy);
        _lastPhysX = sx; _lastPhysY = sy;
        Log.Info(revReclaim ? "← 已收回控制权（本机鼠标动作）"
                : revExit ? "← 退出反向控制，回到本机" : "← 回到本机");
        UiUtil.Run(() => _toast?.Show(
            revReclaim ? "已收回" : "已返回",
            revReclaim ? "控制权已回到本机"
                       : revExit ? "已交还控制权，回到本机" : "回到本机控制", null, 1600));
    }

    void OnHookButton(int btn, bool down)
    {
        if (btn == 0 && !down)
        {
            if (_dropHandover) { CommitDropOut(); return; }
            if (_dropIn is { Committed: false }) { UiUtil.Run(CommitDropIn); }
        }
        if (RemoteActive) PostMsg(new Msg { T = "mouse_btn", Btn = btn, Down = down });
    }

    void OnHookWheel(int dx, int dy)
    {
        if (RemoteActive) PostMsg(new Msg { T = "wheel", Dx = dx, Dy = dy });
    }

    void OnHookKey(int vk, int scan, bool ext, bool down)
    {
        // —— 保命热键：无论在什么状态都必须能收回本机鼠标 ——
        // 远控时钩子是 Capture 模式（吞掉全部按键），一旦"回程边缘"判定没命中，
        // 用户就会被锁在对端、本机鼠标点哪儿都没反应。所以"拉回本机"的判定放在
        // 最前面、且不受 Cfg.HotkeysEnabled 影响 —— 它必须永远有效。
        //
        // C2 起热键组合可自定义，所以这里同时接受两套：**默认的 Ctrl+Alt+1**（永远认，
        // 即便用户改成了别的组合 —— 保命键不能因为改设置就失灵）与**用户当前选定的组合**。
        if (down && vk == Native.VK_1 && RemoteActive)
        {
            bool ctrl = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
            bool alt = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
            bool shift = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;

            bool defaultCombo = ctrl && alt && !shift;
            bool chosenCombo = HotkeyModsDown();

            if (defaultCombo || chosenCombo)
            {
                Log.Info($"热键 {HotkeyHint("1")}：强制拉回本机");
                UiUtil.Run(SwitchToLocal);
                return;
            }
        }

        if (!RemoteActive) return;

        if (down && vk == Native.VK_ESCAPE && _dropHandover)
        {
            CancelDropOut();
            return;
        }
        if (down && vk == Native.VK_SCROLL && Cfg.LockKeyEnabled)
        {
            LockToScreen = !LockToScreen;
            Log.Info($"ScrollLock：{(LockToScreen ? "已锁定当前屏幕" : "已解除锁定")}");
            UiUtil.Run(() => _toast?.Show("Scroll Lock", LockToScreen ? "已锁定到当前屏幕（再按解除）" : "已解除锁定", null, 1800));
            return; // 吞掉，不转发
        }

        // 远控状态下热键必须在本机就地生效，不能转发给对端 ——
        // Capture 模式下钩子会吞掉全部按键，如果这里不拦，Ctrl+Alt+1「拉回本机」会被当成
        // 普通按键发给对端，用户就只能靠把鼠标推回边缘才能回来（L/D 同理）。
        if (down && Cfg.HotkeysEnabled && vk is Native.VK_1 or Native.VK_2 or Native.VK_L or Native.VK_D)
        {
            if (HotkeyModsDown())
            {
                switch (vk)
                {
                    case Native.VK_1:                                        // 拉回本机
                        UiUtil.Run(SwitchToLocal);
                        return;
                    case Native.VK_2:                                        // 已经在对端，忽略
                        return;
                    case Native.VK_L:                                        // 锁定/解锁当前屏幕
                        LockToScreen = !LockToScreen;
                        UiUtil.Run(() => _toast?.Show("锁定", LockToScreen ? $"已锁定到当前屏幕（{HotkeyHint("L")} 解除）" : "已解除锁定", null, 1800));
                        return;
                    case Native.VK_D:                                        // 暂停/恢复接管
                        UiUtil.Run(() => SetTakeover(!Cfg.TakeoverEnabled));
                        return;
                }
            }
        }

        // 远控中、前台是本程序自己的窗口（例如用户开着设置页又跳到了对端）：
        // 按键归本机，不再转发给对端。否则会出现"在自家输入框里打一行字，对端那边也跟着
        // 打一遍"的双投 —— 钩子那边为了让自家窗口能打字是不吞按键的（LetThroughOwnWindows）。
        // 必须放在热键分支**之后**：Ctrl+Alt+L/D 等远控热键要优先就地生效，不能被这里拦掉。
        if (Native.IsOwnProcessForeground()) return;

        PostMsg(new Msg { T = "key", Vk = vk, Scan = scan, Ext = ext, Down = down });
    }

    /// <summary>
    /// 当前用户在设置里选定的修饰键组合是否正被按下（C2 可自定义热键）。
    ///
    /// Cfg.HotkeyMods 取值：CtrlAlt（默认）/ CtrlShift / AltShift。
    /// 注意这里刻意做成"完全匹配"：选 CtrlShift 后按 Ctrl+Alt+2 **不该**触发，
    /// 否则冲突了换一套也没意义。但保命热键（拉回本机）另有一路兜底，见 OnHookKey 开头。
    /// </summary>
    bool HotkeyModsDown()
    {
        bool ctrl = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;
        bool alt = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
        bool shift = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;

        return (Cfg.HotkeyMods switch
        {
            "CtrlShift" => ctrl && shift && !alt,
            "AltShift" => alt && shift && !ctrl,
            _ => ctrl && alt && !shift,   // CtrlAlt（默认）
        });
    }

    /// <summary>把热键渲染成给用户看的文本，例如 HotkeyHint("2") → "Ctrl+Alt+2"。</summary>
    public string HotkeyHint(string key) => Cfg.HotkeyMods switch
    {
        "CtrlShift" => "Ctrl+Shift+" + key,
        "AltShift" => "Alt+Shift+" + key,
        _ => "Ctrl+Alt+" + key,
    };

    /// <summary>Monitor 模式下的按键裁决（钩子线程调用，必须快）：热键 + Ctrl+V 拦截 + ScrollLock 锁屏。</summary>
    bool KbdFilter(KbdEvt e)
    {
        if (e.Down && Cfg.HotkeysEnabled)
        {
            bool modsOk = HotkeyModsDown();
            if (modsOk)
            {
                switch (e.Vk)
                {
                    case Native.VK_2:   // 跳到对端（主控端正向 / 被控端反向索取）
                        if (Connected && !RemoteActive && Cfg.TakeoverEnabled)
                        {
                            if (IsServerRole) { UiUtil.Run(() => SwitchToRemote(CursorY())); return true; }
                            // 已批准过（或对端开着许可且本次会话还没被拒）→ 申请/立即接管
                            if (_revGranted)
                            {
                                _revGranted = false;
                                UiUtil.Run(() => SwitchToRemote(0));
                                return true;
                            }
                            if (_revAllowed && !_revPending && !_revDeclinedThisSession)
                            {
                                _revPending = true;
                                PostMsg(new Msg { T = "rev_req", Name = MyName });
                                UiUtil.Run(() => _toast?.Show("请求反向控制",
                                    $"正在向 {PeerName} 申请鼠标键盘控制权…", null, 2200));
                                return true;
                            }
                            UiUtil.Run(() => _toast?.Show("无法反向控制",
                                Cfg.TakeoverEnabled ? "主控端没有开启「允许被控端控制」" : "接管已暂停", null, 3000));
                        }
                        break;
                    case Native.VK_1:   // 拉回本机（两侧通用）
                        if (RemoteActive) { UiUtil.Run(SwitchToLocal); return true; }
                        break;
                    case Native.VK_L:   // 锁定/解锁当前屏幕
                        LockToScreen = !LockToScreen;
                        UiUtil.Run(() => _toast?.Show("锁定", LockToScreen ? $"已锁定到当前屏幕（{HotkeyHint("L")} 解除）" : "已解除锁定", null, 1800));
                        return true;
                    case Native.VK_D:   // 暂停/恢复接管
                        UiUtil.Run(() => SetTakeover(!Cfg.TakeoverEnabled));
                        return true;
                }
            }
        }

        if (IsServerRole && !RemoteActive && e.Vk == Native.VK_SCROLL && e.Down && Cfg.LockKeyEnabled)
        {
            LockToScreen = !LockToScreen;
            UiUtil.Run(() => _toast?.Show("Scroll Lock", LockToScreen ? "已锁定到当前屏幕（再按解除）" : "已解除锁定", null, 1800));
            return true; // 吃掉 ScrollLock
        }

        // Ctrl+V 拦截：有待接收的文件物料且前台是资源管理器/桌面。
        // 注意必须判 e.Down —— 以前漏了这个，V 键的**抬起**也会被当一次触发，
        // 出现"按一下 V 却请求了两次传输"的怪现象。
        if (!e.Down) return false;

        bool ctrlDown = (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0;

        // —— 两侧复制粘贴共用的入口：在本机按 Ctrl+V 时，顺手向对端要一次"最新剪贴板" ——
        // 用户的心智是"我复制过的东西，两台机器上按粘贴都该出来"。本机剪贴板有可能
        // 比对端旧（对端刚复制、同步还在路上），所以粘贴前主动索取一次；对端只在
        // 它那份确实更新时才回发（见 OnClipReq），不会把本机刚复制的内容冲掉。
        if (e.Vk == 0x56 && ctrlDown && (_pending == null || _pending.Drop))
            PostMsg(new Msg { T = "clip_req", Stamp = _localClipMs });

        if (!IsServerRole && BeingControlled) return false;
        if (IsServerRole && RemoteActive) return false;
        if (e.Vk == 0x56 && _pending is { Requested: false, Drop: false } && ctrlDown)
        {
            if (Native.IsShellForeground())
            {
                lock (_pendingGate)
                {
                    if (_pending is { Requested: false, Drop: false })
                    {
                        _pending.Requested = true;
                        PostMsg(new Msg { T = "file_req", Id = _pending.Id });
                        UiUtil.Run(() => _toast?.Show("正在接收文件", "从对端接收文件中…", null, 0));
                        Log.Info("拦截 Ctrl+V，请求文件传输");
                        return true;
                    }
                }
            }
        }
        return false;
    }

    int CursorY()
    {
        Native.GetCursorPos(out var p);
        return p.Y;
    }

    readonly object _pendingGate = new();

    // ================= 剪贴板同步（双向） =================

    // 「以最新为准」的判据：
    //   两端各自把"最近一次复制/接收"的时刻记为**距本次连接建立以来的毫秒数**
    //   （_linkStartMs 在链路建立时置位），这样两端的数就落在同一个时间轴上了，
    //   可以互相比较。谁的数字更大谁就更晚、谁就该赢。
    //
    //   _localClipMs  = 本机最近一次「用户自己复制」（非远端写入）的时刻
    //   _remoteClipMs = 本机最近一次「把远端内容写进本机剪贴板」的时刻
    long _linkStartMs = 0;
    long _localClipMs = -100000;
    long _remoteClipMs = -100000;

    /// <summary>进入远端控制的时刻。回程判定有 300ms 宽限（见 OnHookMouseMove）。</summary>
    long _remoteEnterMs;

    /// <summary>回程边缘的连续命中帧数（去抖）。
    /// 切换瞬间残留的输入事件会和"物理光标钳回中心"互相拉扯，_cx 会被打回返回边缘
    /// 一两帧 —— 单帧命中就弹回会造成"推过去又弹回来"。连续 4 帧都贴在返回边缘
    /// 才认定是用户真的要回来（真实回程动作贴住边缘时约 100~150ms 即触发）。</summary>
    int _backEdgeHits;

    long ClipStamp() => Math.Max(0, Monotonic.Ms - _linkStartMs);

    async void OnClipboardChanged(ClipContent c)
    {
        try
        {
            if (!Connected) { Badge = BadgeState.None; return; }

            // 以最新为准：本机确实发生了复制（无论是鼠标在本机还是已推到对端，
            // 用户就是按了 Ctrl+C），就记下更晚的时刻并推给对端。谁更晚谁赢，
            // 不再像以前那样"鼠标在对端时本机一律不推送"（那会把用户刚复制的冲掉）。
            _localClipMs = ClipStamp();
            _lastLocalClip = c;                     // 供对端 clip_req 时重发
            await PushClipAsync(c, _localClipMs);
            _clip.MarkSent(c);                      // 断乒乓：这份内容推出去后，写回来的回声别再当新复制
        }
        catch (Exception ex) { Log.Error("同步剪贴板失败", ex); }
    }

    /// <summary>本机最近一次"用户自己复制"的内容，对端按 Ctrl+V 索取时回发它。</summary>
    volatile ClipContent? _lastLocalClip;
    long _lastLocalClipStamp = -1;

    /// <summary>
    /// 剪贴板单条内容的实际上限（字节）。
    ///
    /// 取三者最小值，多一道都不能省：
    ///   · 用户设的 ClipCapMB（默认 2048MB，是"我愿意传多大"的意愿上限）；
    ///   · 协议单帧上限 Proto.MaxFrame 里留给载荷的部分 —— 超了 Encode/ReadFrameSize 会
    ///     直接抛 InvalidDataException 把链路打断，比"不同步"严重得多；
    ///   · 留一帧头部余量（约 1KB 够用），免得 meta 字段把帧顶爆。
    ///
    /// 之前只在用户配置那一层判，配置默认值又大于协议上限，结果"设了 2GB 上限、
    /// 传 100MB 图片"会在编码阶段炸链路。现在统一收敛到这里。
    /// </summary>
    long ClipCapBytes()
    {
        long user = Cfg.ClipCapMB * 1024L * 1024L;
        long proto = Proto.MaxFrame - 1024L;         // 留一点帧头余量
        return Math.Max(64L * 1024, Math.Min(user, proto));
    }

    /// <summary>返回"是否放行"；超限时给一句节流提示（不重复刷屏）。</summary>
    bool ClipWithinCap(long size, string what)
    {
        long cap = ClipCapBytes();
        if (size <= cap) return true;
        ToastThrottled("剪贴板", $"{what}超过上限 {FileScanner.Human(cap)}，未同步");
        return false;
    }

    /// <summary>把一份剪贴板内容推给对端（带「以最新为准」时间戳）。</summary>
    async Task PushClipAsync(ClipContent c, long stamp)
    {
        long id = Interlocked.Increment(ref _idSeq);
        switch (c.Kind)
        {
            case ClipKind.Text:
                if (!Cfg.SyncText) return;
                var tb = Encoding.UTF8.GetBytes(c.Text);
                if (!ClipWithinCap(tb.Length, "文本")) return;
                await SendViaLinkAsync(new Msg { T = "clip_meta", Id = id, Kind = "text", Size = tb.Length, Stamp = stamp }, null);
                await SendViaLinkAsync(new Msg { T = "clip_data", Id = id, Kind = "text" }, tb);
                Badge = BadgeState.Synced;
                _lastLocalClipStamp = stamp;
                break;

            case ClipKind.Image:
                if (!Cfg.SyncImage) return;
                if (!ClipWithinCap(c.Png.Length, "图片")) return;
                await SendViaLinkAsync(new Msg { T = "clip_meta", Id = id, Kind = "image", Size = c.Png.Length, Stamp = stamp }, null);
                await SendViaLinkAsync(new Msg { T = "clip_data", Id = id, Kind = "image" }, c.Png);
                Badge = BadgeState.Synced;
                _lastLocalClipStamp = stamp;
                break;

            case ClipKind.Files:
                if (!Cfg.SyncFiles) return;
                var (items, total, fileCount) = FileScanner.Expand(c.Files);
                if (items.Count == 0) return;
                bool eager = Cfg.EagerReceive && total <= Cfg.EagerCapMB * 1024L * 1024L;
                lock (_outGate)
                {
                    _outgoing[id] = items;
                    _outgoingVia[id] = "剪贴板";
                    PruneOutgoing();
                }
                await SendViaLinkAsync(new Msg
                {
                    T = "clip_meta", Id = id, Kind = "files", Size = total, Count = fileCount,
                    Eager = eager, Files = FileScanner.ToEntries(items), Drop = false, Stamp = stamp
                }, null);
                Badge = BadgeState.Synced;
                _lastLocalClipStamp = stamp;
                Log.Info($"已发送文件物料 id={id} 共 {fileCount} 个文件 / {FileScanner.Human(total)}（预接收={eager}）");
                break;
        }
    }

    /// <summary>
    /// 对端按下了 Ctrl+V，向本机索取"当前最新的剪贴板"。
    ///
    /// 为什么需要这条：Ctrl+C 的内容是**推送**式同步的，如果推送时对端还没连上、
    /// 或者对端当时正被别的内容占着，那份内容就到不了对端。用户此时跑到对端按 Ctrl+V
    /// 会粘出旧东西，体验上就是"复制粘贴不能两台共用"。
    /// 收到索取后这里做一次**按需补发**：只在本机那份比对端手上的更新时才回发，
    /// 免得把对端刚复制的内容冲掉。
    /// </summary>
    void OnClipReq(Msg m)
    {
        var c = _lastLocalClip;
        if (c == null) { Log.Debug("对端索取剪贴板：本机还没有复制过内容，忽略"); return; }
        long mine = Math.Max(_lastLocalClipStamp, _localClipMs);

        // 「本机有没有比对方更新的内容」判据要小心"两边都没时间戳"的退化情形：
        // 刚重连时本机两个 stamp 都还是 -1（本会话没复制过），对端发来的 m.Stamp 也是 -1
        // （它那边同样没复制过）—— 此时 mine(-1) <= m.Stamp(-1) 恒成立，会**永远不补发**。
        // 但用户场景恰恰是"断线前在 A 复制、重连、到 B 按 Ctrl+V"，B 应当能拿到。
        // 所以只要本机确实持有内容（c != null）且两边都没时间戳，就照发（内容以本机为准）。
        bool bothUnknown = mine <= 0 && m.Stamp <= 0;
        if (!bothUnknown && mine <= m.Stamp)
        {
            Log.Debug($"对端索取剪贴板：本机内容不更新（本机 {mine}ms vs 对端 {m.Stamp}ms），不补发");
            return;
        }
        Log.Info($"对端索取剪贴板 → 补发本机最新内容（{c.Kind}，本机 {mine}ms / 对端 {m.Stamp}ms"
                 + (bothUnknown ? "，双方均无时间戳，按本机内容补发" : "") + "）");
        // 补发时用 mine，但若本机时间戳无效，就给个非零值，免得对端又落回"双方都无时间戳"分支
        _ = PushClipAsync(c, mine > 0 ? mine : ClipStamp());
    }

    /// <summary>只保留最近 12 份发送物料。<b>调用方必须已持 _outGate</b>（见各调用点）。</summary>
    void PruneOutgoing()
    {
        while (_outgoing.Count > 12)
        {
            long min = _outgoing.Keys.Min();
            _outgoing.Remove(min);
            _outgoingVia.Remove(min);
        }
    }

    void OnClipMeta(Msg m)
    {
        // 「以最新为准」：只有对端这次复制比本机最近一次本地复制更新，
        // 才允许它覆盖本机剪贴板。否则记下"这次被拒"，后续 clip_data 一起跳过。
        // m.Stamp == 0 兼容老对端（旧版不带时间戳）。
        //
        // 额外的接收侧容量校验：对端可能设了比本机大得多的上限，直接把一个本机
        // 根本装不下的内容推过来。这里按**本机**的 ClipCapBytes() 再判一次，
        // 超了就整个拒绝（比"收了一半内存爆掉"可控）。
        bool withinSelf = m.Size <= ClipCapBytes();
        bool accept = withinSelf && (m.Stamp == 0 || m.Stamp > _localClipMs);
        if (!withinSelf)
            Log.Info($"剪贴板：对端内容 {FileScanner.Human(m.Size)} 超过本机上限 " +
                     $"{FileScanner.Human(ClipCapBytes())}，拒绝");
        else if (!accept)
            Log.Info($"剪贴板：对端内容较旧（对端 {m.Stamp}ms vs 本机 {_localClipMs}ms），保留本机剪贴板");

        _acceptRemoteClip = accept;
        // 关键：把"是否放行"和**这一份内容**绑起来（id）。以前是个共享布尔，
        // 若紧随其后又来了另一条 clip_meta（比如用户连点两次复制），
        // 后一条把标志改掉，前一条的 clip_data 就会按错误结论处理。
        _acceptRemoteClipId = m.Id;
        if (accept) _remoteClipMs = m.Stamp;

        switch (m.Kind)
        {
            case "files":
                lock (_pendingGate)
                {
                    _xfer.CancelRecv();
                    _pending = new PendingRecv { Id = m.Id, Total = m.Size, Drop = m.Drop };
                }
                Badge = BadgeState.PendingFile;
                bool preload = (m.Drop || (m.Eager && Cfg.EagerReceive));
                if (preload)
                {
                    lock (_pendingGate) { if (_pending != null) _pending.Requested = true; }
                    _xfer.BeginRecv(m.Id, m.Size);
                    PostMsg(new Msg { T = "file_req", Id = m.Id });
                    string via = m.Drop ? "跨机拖拽" : (m.Eager && Cfg.EagerReceive ? "自动预接收" : "剪贴板");
                    TransferJournal.Begin(m.Id, false, m.Size, m.Count, $"接收 {m.Count} 个文件", PeerName, via);
                    _xferBusy = true; _xferSending = false; _xferSent = 0; _xferTotal = Math.Max(m.Size, 1);
                    _xferLabel = $"接收 {m.Count} 个文件";
                    Log.Info($"对端文件物料 id={m.Id}（{(m.Drop ? "拖拽投递" : "自动预接收")}，{FileScanner.Human(m.Size)}）");
                }
                else
                {
                    Log.Info($"对端文件物料 id={m.Id} 待按需接收（{FileScanner.Human(m.Size)}，在资源管理器里按 Ctrl+V）");
                    if (!m.Drop) UiUtil.Run(() => _toast?.Show("收到文件物料", $"共 {FileScanner.Human(m.Size)}，在资源管理器中按 Ctrl+V 接收", null, 3500));
                }
                break;

            case "text":
            case "image":
                // clip_data 紧随其后
                break;
        }
    }

    /// <summary>本次 clip_meta 是否被「以最新为准」判据放行（clip_data 跟随它）。
    /// <c>_acceptRemoteClipId</c> 记录放行的是**哪一份**（id）：只有紧随其后的、id 匹配的
    /// clip_data 才跟它走；否则回落成"不接受"，避免被后到的另一份 meta 串了。</summary>
    volatile bool _acceptRemoteClip = true;
    long _acceptRemoteClipId = -1;

    /// <summary>这份 clip_data 是否属于被放行的那份 meta。</summary>
    bool ClipAccepted(long id) => _acceptRemoteClip && _acceptRemoteClipId == id;

    void OnClipData(Msg m, byte[]? p)
    {
        try
        {
            if (p == null) return;
            if (!ClipAccepted(m.Id))
            {
                Log.Debug("剪贴板：该份内容未获放行（较旧 / 超限 / 已被更新的一份取代），跳过写入");
                return;
            }
            // 落盘前再核一次实际字节数：meta 里的 Size 是对端自报的，不能全信；
            // 真正要分配内存的是 p，按 p.Length 复判才是最后一道闸。
            if (p.LongLength > ClipCapBytes())
            {
                Log.Warn($"剪贴板：实收 {p.LongLength}B 超过本机上限，丢弃");
                return;
            }
            if (m.Kind == "text" && Cfg.SyncText)
            {
                var text = Encoding.UTF8.GetString(p);
                UiUtil.Run(() => { _clip?.Write(ClipContent.OfText(text)); });
                Badge = BadgeState.Synced;
                Log.Info($"收到文本 {p.Length}B，已写入本机剪贴板");
            }
            else if (m.Kind == "image" && Cfg.SyncImage)
            {
                UiUtil.Run(() => { _clip?.Write(ClipContent.OfImage(p)); });
                Badge = BadgeState.Synced;
                Log.Info($"收到图片 {p.Length}B，已写入本机剪贴板");
            }
        }
        catch (Exception ex) { Log.Error("处理剪贴板数据失败", ex); }
    }

    // ================= 跨机拖拽投递 =================
    // 体验目标：和"同一台电脑上两个文件夹窗口之间拖动"一致 ——
    //   按住文件推过屏幕边缘 → 光标（连同"拖拽态"）接着出现在对端 →
    //   在对端任意文件夹上方松开 → 文件直接落到那个文件夹里。
    // 实现要点：
    //   源端检测到"顶到朝对端的边缘" → 结束本机 Shell 拖拽（Esc，避免本地误落一份）
    //   → 发 drop_begin 并交出控制权；
    //   目标端实时解析"光标下的文件夹"，光环显示"松手保存到 XXX"；
    //   松手 → drop_commit → 按清单传输 → 落盘到该文件夹。

    void OnDragHandover()
    {
        if (!Cfg.DragDropAcrossMachines) return;
        if (!Connected || !Cfg.TakeoverEnabled) return;

        var sel = ExplorerHelper.GetForegroundSelection();
        if (sel.Count == 0)
        {
            UiUtil.Run(() => _toast?.Show("拖拽投递", "没识别到拖拽的文件。资源管理器里选中后拖到边缘即可；桌面拖拽请改用 Ctrl+C。", null, 5000));
            return;
        }
        BeginDropAsSource(sel);
    }

    void BeginDropAsSource(List<string> files)
    {
        var (items, total, count) = FileScanner.Expand(files);
        if (items.Count == 0) { UiUtil.Run(() => _toast?.Show("拖拽投递", "没有可投递的内容", null, 2500)); return; }

        long id = Interlocked.Increment(ref _idSeq);
        lock (_outGate)
        {
            _outgoing[id] = items;
            _outgoingVia[id] = "跨机拖拽";
            PruneOutgoing();
        }
        _dropOutId = id;
        _dropOutTotal = total;
        _dropOutCount = count;
        _dropHandover = true;

        // 结束本机这次 Shell 拖拽：否则在对端松手后本机也会跟着落一份
        Injector.Key(Native.VK_ESCAPE, 0, false, true);
        Injector.Key(Native.VK_ESCAPE, 0, false, false);

        PostMsg(new Msg
        {
            T = "drop_begin", Id = id, Size = total, Count = count,
            Files = FileScanner.ToEntries(items), Drop = true
        });
        Log.Info($"跨机拖拽开始 id={id}：{count} 个文件 / {FileScanner.Human(total)}");

        if (IsServerRole && !RemoteActive) SwitchToRemote(CursorY());

        UiUtil.Run(() => _toast?.Show("跨机拖拽",
            $"把光标移到 {PeerName} 的目标文件夹上方，松手即保存（Esc 取消）", null, 4000));
    }

    /// <summary>源端：物理左键松开 → 提交到对端。</summary>
    void CommitDropOut()
    {
        if (!_dropHandover) return;
        _dropHandover = false;
        PostMsg(new Msg { T = "drop_commit", Id = _dropOutId });
        Log.Info($"提交跨机拖拽 id={_dropOutId}");
        UiUtil.Run(() => _toast?.Show("投递中", $"{_dropOutCount} 个文件正在传输…", null, 2500));
    }

    void CancelDropOut()
    {
        if (!_dropHandover) return;
        _dropHandover = false;
        PostMsg(new Msg { T = "drop_cancel", Id = _dropOutId });
        lock (_outGate)
        {
            _outgoing.Remove(_dropOutId);
            _outgoingVia.Remove(_dropOutId);
        }
        Log.Info("已取消跨机拖拽");
        UiUtil.Run(() => _toast?.Show("已取消", "跨机拖拽已取消，文件未传输", null, 2500));
        if (IsServerRole && RemoteActive) SwitchToLocal();
    }

    /// <summary>目标端：收到对端拖进来的会话。</summary>
    void OnDropBegin(Msg m)
    {
        if (!Cfg.DragDropAcrossMachines) { PostMsg(new Msg { T = "drop_cancel", Id = m.Id }); return; }
        _dropIn = new DropIn { Id = m.Id, Count = m.Count, Total = m.Size };
        _dropHoverDir = null;
        _dropHoverReal = false;

        // 对端把光标交接回本机（反向拖拽：文件在被控端，投到主控端）
        if (IsServerRole && RemoteActive) SwitchToLocal();

        Log.Info($"对端开始跨机拖拽 id={m.Id}：{m.Count} 个文件 / {FileScanner.Human(m.Size)}");
        UiUtil.Run(() => _toast?.Show("对端正在拖拽", $"{m.Count} 个文件，移到目标文件夹上方松手即保存", null, 3000));
    }

    void OnDropCancel()
    {
        _dropIn = null;
        _dropHoverDir = null;
        Log.Info("对端取消了跨机拖拽");
        UiUtil.Run(() => _toast?.Show("投递已取消", "对端取消了本次拖拽投递", null, 2500));
    }

    /// <summary>目标端：实时解析光标下的文件夹（光环文字提示，150ms 一次）。</summary>
    void PumpDropHover()
    {
        var d = _dropIn;
        if (d == null || d.Committed) return;
        try
        {
            var t = ExplorerHelper.ResolveDropTarget(Cfg);
            bool changed = t.Dir != _dropHoverDir;
            _dropHoverDir = t.Dir;
            _dropHoverReal = t.IsRealFolder;
            if (changed) Log.Debug($"投递目标 → {t.Dir}（真实文件夹={t.IsRealFolder}）");
        }
        catch (Exception ex) { Log.Debug("解析投递目标失败: " + ex.Message); }
    }

    /// <summary>目标端：松手 → 按清单把文件落到光标所在文件夹。</summary>
    void CommitDropIn()
    {
        var d = _dropIn;
        if (d == null || d.Committed) return;
        d.Committed = true;

        var target = ExplorerHelper.ResolveDropTarget(Cfg);
        bool ok = target.IsRealFolder || Cfg.DropFallbackToDefaultDir;
        if (!ok)
        {
            _dropIn = null;
            PostMsg(new Msg { T = "drop_cancel", Id = d.Id });
            UiUtil.Run(() => _toast?.Show("已取消投递", "松手的位置不是文件夹（可在设置里改为落到默认目录）", null, 4000));
            return;
        }

        lock (_pendingGate)
        {
            _pending = new PendingRecv { Id = d.Id, Total = d.Total, Drop = true, TargetDir = target.Dir };
        }
        _xfer.BeginRecv(d.Id, d.Total);
        PostMsg(new Msg { T = "file_req", Id = d.Id });
        _xferBusy = true; _xferSending = false; _xferSent = 0; _xferTotal = Math.Max(d.Total, 1);
        _xferLabel = $"接收 {d.Count} 个文件";
        TransferJournal.Begin(d.Id, false, _xferTotal, d.Count, _xferLabel, PeerName, "跨机拖拽");
        _dropIn = null;
        Badge = BadgeState.PendingFile;
        Log.Info($"投递落地目录：{target.Dir}（真实文件夹={target.IsRealFolder}）");
        UiUtil.Run(() => _toast?.Show("正在接收", $"{(target.IsRealFolder ? "保存到" : "默认目录")} {target.Label}", null, 3000));
    }

    // ================= 传输进度 / 完成 =================

    void OnXferProgress(long id, long sent, long total)
    {
        _xferBusy = true; _xferSent = sent; _xferTotal = Math.Max(total, 1);
        TransferJournal.Progress(id, sent, total);
        var now = Monotonic.Ms;
        if (now - _lastToastMs > 150)
        {
            _lastToastMs = now;
            double mb = sent / 1048576.0, tot = _xferTotal / 1048576.0;
            int pct = (int)Math.Round(MathX.Clamp(sent / (double)_xferTotal, 0, 1) * 100);
            string lbl = string.IsNullOrEmpty(_xferLabel) ? (_xferSending ? "发送" : "接收") : _xferLabel;
            // 速度与剩余时间（C8）：光有百分比看不出"还要多久"，大文件传输时体验很差。
            // 用两次采样做滑动平均，避免瞬时抖动让读数乱跳。
            string speed = SpeedAndEta(sent, _xferTotal);
            // 只留一行过程文字："接收 51 个文件　2157.2 / 4555.4 MB　47%　34.2 MB/s　剩 1分10秒"
            string line = (tot >= 1
                ? $"{lbl}　{mb:F1} / {tot:F1} MB　{pct}%"
                : $"{lbl}　{sent / 1024.0:F0} / {_xferTotal / 1024.0:F0} KB　{pct}%")
                + speed;
            UiUtil.Run(() => _toast?.Show("文件传输", line, (double)sent / _xferTotal, 0));
        }
    }

    // —— 传输速度采样（C8）——
    long _spdLastTick = 0, _spdLastSent = 0;
    double _spdBytesPerSec = 0;

    /// <summary>算一次实时速度 + 预计剩余时间，拼成进度行后缀。数据不足时返回空串。</summary>
    string SpeedAndEta(long sent, long total)
    {
        long now = Monotonic.Ms;
        if (_spdLastTick == 0 || now - _spdLastTick > 1200)
        {
            long dt = now - _spdLastTick;
            if (_spdLastTick != 0 && dt > 200)
            {
                double inst = (sent - _spdLastSent) * 1000.0 / dt;
                // 指数平滑：旧值权重 0.6，新样本 0.4
                _spdBytesPerSec = _spdBytesPerSec <= 0 ? inst : _spdBytesPerSec * 0.6 + inst * 0.4;
            }
            _spdLastTick = now; _spdLastSent = sent;
        }
        if (_spdBytesPerSec <= 1024) return "";
        string spd = _spdBytesPerSec >= 1048576
            ? $"{_spdBytesPerSec / 1048576.0:F1} MB/s"
            : $"{_spdBytesPerSec / 1024.0:F0} KB/s";
        long remain = total - sent;
        if (remain <= 0) return "　" + spd;
        double secs = remain / _spdBytesPerSec;
        string eta;
        if (secs < 60) eta = $"剩 {secs:F0} 秒";
        else if (secs < 3600) eta = $"剩 {(int)(secs / 60)}分{(int)(secs % 60)}秒";
        else eta = $"剩 {(int)(secs / 3600)}时{(int)((secs % 3600) / 60)}分";
        return $"　{spd}　{eta}";
    }

    void OnXferSendDone(long id)
    {
        bool wasSending = _xferSending;
        _xferBusy = false;
        _xferSending = false;
        _xferSent = _xferTotal;                       // 收尾时把进度补满，避免"差一点不动"
        _spdLastTick = 0; _spdLastSent = 0; _spdBytesPerSec = 0;   // 重置速度采样，下次传输重新起算
        lock (_outGate)
        {
            _outgoing.Remove(id);
            _outgoingVia.Remove(id);
        }
        TransferJournal.Finish(id, XferState.Done, $"→ {PeerName}");
        if (wasSending)
        {
            double mb = _xferTotal / 1048576.0;
            string tip = mb >= 1 ? $"已发送 {_xferLabel.Replace("发送 ", "")}　共 {mb:F1} MB" : $"已发送 {_xferLabel.Replace("发送 ", "")}";
            UiUtil.Run(() => _toast?.Show("发送完成", tip, null, 2600));
        }
    }

    void OnXferRecvDone(long id, List<string> stagedPaths)
    {
        bool wasRecv = _xferBusy && !_xferSending;
        _xferBusy = false;
        _xferSent = _xferTotal;
        var pend = _pending;
        if (pend == null || pend.Id != id)
        {
            TransferJournal.Finish(id, XferState.Done, null, "已过期的接收会话，已清理");
            TryCleanupStaging(stagedPaths);
            return;
        }
        _pending = null;

        if (pend.Drop)
        {
            var dir = pend.TargetDir;
            TransferJournal.Finish(id, XferState.Done, dir, $"{stagedPaths.Count} 项已落地");
            _ = Task.Run(() => MoveToDropDir(stagedPaths, dir));
        }
        else
        {
            Badge = BadgeState.ReadyFile;
            TransferJournal.Finish(id, XferState.Done, "剪贴板暂存",
                $"已就绪 {stagedPaths.Count} 项，Ctrl+V 粘贴");
            UiUtil.Run(() =>
            {
                _clip?.Write(ClipContent.OfFiles(stagedPaths.ToArray()));
                _toast?.Show("文件就绪", $"已接收 {stagedPaths.Count} 项，Ctrl+V 即可粘贴", null, 2500);
            });
            // 若是因为拦截 Ctrl+V 触发的传输，完成后重新注入粘贴。
            // 注意：本方法运行在 FileTransferService 的写循环线程上，**不能在这里 Thread.Sleep**：
            // 一睡就卡住整个接收消息队列（包括后续的 xfer_done / 心跳）。挪到后台线程去等 + 注入。
            if (Native.IsShellForeground())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120).ConfigureAwait(false);
                        Injector.Combo(Native.VK_CONTROL, 0x56);
                        Log.Info("已重新注入 Ctrl+V 粘贴接收的文件");
                    }
                    catch (Exception ex) { Log.Warn("重新注入 Ctrl+V 失败: " + ex.Message); }
                });
            }
            TryCleanupStaging(stagedPaths);
        }
    }

    void MoveToDropDir(List<string> staged, string? overrideDir)
    {
        try
        {
            string dir = overrideDir ?? ExplorerHelper.ResolveDropDir(Cfg);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            int moved = 0;
            foreach (var src in staged)
            {
                var dest = Staging.UniqueName(dir, Path.GetFileName(src));
                try
                {
                    if (Directory.Exists(src)) { CopyDir(src, dest); Directory.Delete(src, true); moved++; }
                    else { File.Move(src, dest); moved++; }
                }
                catch (IOException)
                {
                    // 跨卷：复制后删除
                    if (Directory.Exists(src)) { CopyDir(src, dest); Directory.Delete(src, true); moved++; }
                    else { File.Copy(src, dest, true); File.Delete(src); moved++; }
                }
                catch (Exception ex) { Log.Warn($"落盘失败 {src}: {ex.Message}"); }
            }
            Log.Info($"投递完成：{moved} 项 → {dir}");
            UiUtil.Run(() =>
            {
                _toast?.Show("投递完成", $"{moved} 项已保存到 {dir}", null, 3500);
                if (Cfg.OpenFolderAfterDrop)
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")); } catch { }
            });
        }
        catch (Exception ex)
        {
            Log.Error("移动投递文件失败", ex);
            UiUtil.Run(() => _toast?.Show("投递失败", ex.Message, null, 4000));
        }
    }

    static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    void OnXferFailed(long id, string reason)
    {
        _xferBusy = false;
        lock (_pendingGate)
        {
            if (_pending?.Id == id) { _pending = null; Badge = BadgeState.None; }
        }
        _dropIn = null;
        bool canceled = reason.Contains("取消");
        TransferJournal.Finish(id, canceled ? XferState.Canceled : XferState.Failed, null, reason);
        UiUtil.Run(() => _toast?.Show(canceled ? "已取消" : "传输失败", reason, null, 4000));
    }

    /// <summary>传输完成后延迟清理暂存目录（剪贴板还引用着这些文件，稍后删）。</summary>
    void TryCleanupStaging(List<string> staged)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(10));
            try
            {
                var dirs = staged.Select(Path.GetDirectoryName).Where(x => x != null).Distinct();
                foreach (var d in dirs)
                    if (d != null && d.StartsWith(AppPaths.StagingRoot, StringComparison.OrdinalIgnoreCase))
                        Directory.Delete(d, true);
            }
            catch { }
        });
    }

    void ToastThrottled(string title, string msg)
    {
        UiUtil.Run(() => _toast?.Show(title, msg, null, 3000));
    }

    // ================= UI 状态泵 =================

    void PumpUiState()
    {
        if (DebugHaloMode) return;
        var v = BuildView();
        if (v.State != _lastIndState)
        {
            _lastIndState = v.State;
            _labelUntil = Monotonic.Ms + 2600;   // 状态切换时短暂亮一下文字
        }
        bool transient = Monotonic.Ms < _labelUntil;
        bool persistent = v.State is IndState.Dragging or IndState.DropReady or IndState.DropBlocked or IndState.Transferring;

        string? text = v.Text;
        string? detail = v.Detail;
        if (!persistent && !transient) { text = null; detail = null; }

        _indicator?.SetView(v.State, v.Badge, v.Progress, text, detail);
    }

    /// <summary>算出光环这一帧该显示什么。</summary>
    IndView BuildView()
    {
        double prog = _xferBusy ? MathX.Clamp(_xferSent / (double)Math.Max(_xferTotal, 1), 0, 1) : 0;

        if (!Connected)
            return new IndView(IndState.Disconnected, BadgeState.None, 0, "未连接", "等待对端");

        if (_xferBusy)
        {
            var lbl = string.IsNullOrEmpty(_xferLabel) ? (_xferSending ? "发送" : "接收") : _xferLabel;
            return new IndView(IndState.Transferring, Badge, prog, "传输中", $"{lbl} {prog * 100:F0}%");
        }

        if (_dropHandover)
            return new IndView(IndState.Dragging, Badge, 0, $"拖拽中 · {_dropOutCount} 项",
                $"{FileScanner.Human(_dropOutTotal)} → 松开左键投给 {PeerName}");

        var di = _dropIn;
        if (di != null && !di.Committed)
        {
            var hover = _dropHoverDir;
            string label;
            if (string.IsNullOrEmpty(hover)) label = "…";
            else
            {
                label = Path.GetFileName(hover!.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(label)) label = hover;   // 盘符根目录
            }
            if (_dropHoverReal)
                return new IndView(IndState.DropReady, Badge, 0, $"松手保存 → {label}", $"{di.Count} 个文件 · {FileScanner.Human(di.Total)}");
            if (Cfg.DropFallbackToDefaultDir)
                return new IndView(IndState.DropReady, Badge, 0, $"松手存到默认：{label}", $"{di.Count} 个文件 · 当前位置不是文件夹");
            return new IndView(IndState.DropBlocked, Badge, 0, "此处不能投递", "请移到某个文件夹窗口上方再松手");
        }

        var state = IsServerRole
            ? (RemoteActive ? IndState.RemoteCtl : IndState.Local)
            : (BeingControlled ? IndState.Controlled : IndState.Local);

        string? txt = state switch
        {
            IndState.Local => $"{MyName}",
            IndState.RemoteCtl => $"控制 {PeerName}",
            IndState.Controlled => $"{MyName} · 被控中",
            _ => null,
        };
        string? det = state switch
        {
            IndState.Local => Connected ? "鼠标在本机" : null,
            IndState.RemoteCtl => "移到对端边缘可返回",
            IndState.Controlled => $"来自 {PeerName}",
            _ => null,
        };
        if (LockToScreen) det = $"已锁定到当前屏幕（{HotkeyHint("L")} 解除）";
        return new IndView(state, Badge, 0, txt, det);
    }

    /// <summary>正在连接 / 等对方确认：界面据此显示动态文案与进度条。</summary>
    public bool Busy => !Connected && (CurrentTarget.Length > 0 || WaitConfirmPeer.Length > 0);

    public (string statusText, IndState state, BadgeState badge) UiSnapshot()
    {
        var v = BuildView();
        string txt;
        if (!Connected)
        {
            var wait = WaitConfirmPeer;
            var t = CurrentTarget;
            if (wait.Length > 0) txt = $"等待对方确认（{wait}）";
            else if (t.Length > 0) txt = "正在连接 " + t + (LastConnectError.Length > 0 ? "　上次失败：" + LastConnectError : "");
            else if (LastConnectError.Length > 0) txt = "未连接　（" + LastConnectError + "）";
            else txt = "未连接";
        }
        else
        {
            txt = $"已连接 {PeerName}" + (RemoteActive ? "（正在控制对端）" : BeingControlled ? "（被对端控制）" : "");
        }
        return (txt, v.State, Badge);
    }

    /// <summary>当前文件传输的实时快照，供“传输记录与日志”窗口显示进度。</summary>
    public (bool busy, bool sending, long sent, long total, string label) XferSnapshot()
        => (_xferBusy, _xferSending, _xferSent, Math.Max(_xferTotal, 0), _xferLabel);

    public void Dispose()
    {
        try { PostMsg(new Msg { T = "bye" }); } catch { }
        try { _link?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _netCts.Cancel(); } catch { }
        _disc.Dispose();
        _drag.Stop();
        try { _hook.Mode = HookMode.Off; } catch { }
        _uiPump?.Stop();
        _hoverPump?.Stop();
        ExplorerHelper.ResetShell();
    }
}
