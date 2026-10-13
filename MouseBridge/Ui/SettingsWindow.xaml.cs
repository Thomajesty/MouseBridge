using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseBridge.Control;
using MouseBridge.Core;
using MouseBridge.Input;
using MouseBridge.Net;

namespace MouseBridge.Ui;

public partial class SettingsWindow : Window
{
    /// <summary>启动时定位到哪个标签页（--tab N，用于出图/直达某一页）。</summary>
    public static int StartupTab;

    readonly Orchestrator _orch;
    readonly DispatcherTimer _refresh;
    readonly DispatcherTimer? _scan;
    readonly DispatcherTimer _previewAnim;
    readonly Action _hintHandler;
    string _side = "right";
    bool _ready;
    double _previewProg;          // 预览进度弧的循环相位
    LegendItem? _legendHover;     // 鼠标悬停的颜色图例项（非空时预览演示该状态）
    bool _scanning;
    int _dots;                    // 状态文案里滚动省略号的相位
    bool _syncingHost;            // 两个「对端地址」输入框互相同步时防递归
    bool _syncingPwd;             // 连接密码的明文/遮字两控件互相同步时防递归
    bool _plainVisible;           // 密码是否处于明文显示状态

    // 光环状态色（与 IndicatorWindow 保持一致）
    static readonly Color CGray = Color.FromRgb(0x9A, 0xA3, 0xAF);
    static readonly Color CGreen = Color.FromRgb(0x2E, 0xCC, 0x71);
    static readonly Color COrange = Color.FromRgb(0xFF, 0x9F, 0x1A);
    static readonly Color CRed = Color.FromRgb(0xFF, 0x45, 0x3A);
    static readonly Color CCyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    static readonly Color CPurple = Color.FromRgb(0xA8, 0x55, 0xF7);
    static readonly Color CTeal = Color.FromRgb(0x14, 0xB8, 0xA6);
    static readonly Color CYellow = Color.FromRgb(0xFF, 0xD4, 0x00);
    static readonly Color CBadgeBlue = Color.FromRgb(0x2E, 0x8B, 0xFF);

    /// <summary>颜色图例的一项：悬停时左侧预览按这份"剧本"演示。</summary>
    sealed record LegendItem(string Label, Color Color, string Title, string Sub,
                             bool Arc = false, bool Dash = false, Color? Badge = null);

    List<LegendItem> _legend = new();

    // —— 预览几何：和真实光环（IndicatorWindow）同一套数字，所见即所得 ——
    const double HotX = 14, HotY = 36;      // 光标热点在预览画布上的位置
    const double PreviewGapX = 16;          // 光圈左缘与热点的水平间距
    const double PreviewDropY = 8;          // 光圈中心相对热点的垂直下移
    const double PreviewPillGap = 8;        // 气泡与光圈之间的间距
    const double PreviewPillH = 42;         // 气泡固定高度，避免悬停换文案时上下抖

    const string CopyIconData =
        "M 4.5,1.3 H 9.8 A 1.6,1.6 0 0 1 11.4,2.9 V 9.4 " +
        "M 1.6,3.9 H 7 A 1.6,1.6 0 0 1 8.6,5.5 V 12.4 A 1.6,1.6 0 0 1 7,14 H 1.6 A 1.6,1.6 0 0 1 0,12.4 V 5.5 " +
        "A 1.6,1.6 0 0 1 1.6,3.9 Z";
    const string CheckIconData = "M 0.8,8.2 L 4.6,12 L 12.6,2.6";

    /// <summary>生成连接密码用的字符集：去掉 0/O/1/l/I 这些看着会认错的。</summary>
    const string PwdAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public SettingsWindow(Orchestrator orch)
    {
        InitializeComponent();
        _orch = orch;
        Native.ClampMaximizeToWorkArea(this);   // 无边框窗口最大化别盖住任务栏
        LoadFromConfig();
        BuildLegend();
        UpdateHotkeyTip();   // 热键说明依赖下拉框初值，必须在 LoadFromConfig 之后
        _ready = true;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.RoundCorners(hwnd);
            try
            {
                var img = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
                Icon = img;
                ImgAppIcon.Source = img;
            }
            catch { }
        };

        // 最大化/还原时切换标题栏按钮图形（方框 / 双框）
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            IcoMax.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
            IcoRestore.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        };

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        // 发现列表每 2 秒合并一次（自动发现的候选 + 上次手动扫描的结果），
        // 按端点去重、最多留 8 条，避免列表无限变长。
        _scan = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _scan.Tick += (_, _) => RefreshDiscovered();
        _scan.Start();

        // 预览动画：进度弧循环 + 拖拽态虚线旋转，和真实光环一致
        _previewAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _previewAnim.Tick += (_, _) => PreviewAnimTick();
        _previewAnim.Start();

        // 连接阶段变化（开始连、等对方确认、连上、被拒绝）时立刻刷一次状态栏，
        // 不用等 700ms 的定时器 —— 点「连接」后马上就能看到反馈。
        _hintHandler = () => UiUtil.Run(Refresh);
        _orch.OnUiHint += _hintHandler;

        Closed += (_, _) =>
        {
            _refresh.Stop();
            _scan?.Stop();
            _previewAnim.Stop();
            _orch.OnUiHint -= _hintHandler;
        };
        Loaded += (_, _) =>
        {
            if (StartupTab > 0 && StartupTab < MainTabs.Items.Count) MainTabs.SelectedIndex = StartupTab;
            UpdateSideHighlight();
            Refresh();
        };
    }

    static (int X, int Y, int W, int H) Vs() => Native.VirtualScreen();

    // ================= 载入 / 保存 =================

    void LoadFromConfig()
    {
        var c = _orch.Cfg;
        TxtVersion.Text = "v" + AppInfo.Version;
        TxtAboutVersion.Text = "v" + AppInfo.Version;
        TxtAboutVerRow.Text = "v" + AppInfo.Version;
        TxtAboutAuthor.Text = AppInfo.Author;
        RunContact.Text = AppInfo.Contact;
        LnkContact.NavigateUri = new Uri("mailto:" + AppInfo.Contact);
        // 本机名称默认就是设备名（用户可以直接点进去改）
        TbMachineName.Text = _orch.MyName;
        if (c.Role == "server") RbServer.IsChecked = true; else RbClient.IsChecked = true;
        PbPassword.Password = c.Password;
        TbPort.Text = c.Port.ToString();
        CbTls.IsChecked = c.TlsEnabled;
        CbAutoDiscover.IsChecked = c.AutoDiscover;
        CbConfirm.IsChecked = c.ConfirmOnConnect;
        TbPeerHost.Text = c.PeerHost;
        TbPeerHostAlt.Text = c.PeerHost;
        CbHotkeys.IsChecked = c.HotkeysEnabled;
        SelectComboByTag(CbHotkeyMods, c.HotkeyMods);
        CbTakeover.IsChecked = c.TakeoverEnabled;

        TbSwitchDelay.Text = c.SwitchDelayMs.ToString();
        CbLockKey.IsChecked = c.LockKeyEnabled;

        CbSyncText.IsChecked = c.SyncText;
        CbSyncImage.IsChecked = c.SyncImage;
        CbSyncFiles.IsChecked = c.SyncFiles;
        CbEagerReceive.IsChecked = c.EagerReceive;
        TbEagerCap.Text = c.EagerCapMB.ToString();
        TbClipCap.Text = c.ClipCapMB.ToString();
        CbDragDrop.IsChecked = c.DragDropAcrossMachines;
        CbFallback.IsChecked = c.DropFallbackToDefaultDir;
        CbOpenFolder.IsChecked = c.OpenFolderAfterDrop;

        CbIndicator.IsChecked = c.IndicatorEnabled;
        SlIndicatorSize.Value = c.IndicatorSize;
        TxtSizeVal.Text = ((int)c.IndicatorSize).ToString();
        CbIndicatorText.IsChecked = c.IndicatorText;
        CbShowArc.IsChecked = c.ShowProgressArc;
        CbShowBadge.IsChecked = c.ShowBadge;

        CbAutoStart.IsChecked = c.AutoStart;
        CbAutoStartMinimized.IsChecked = c.AutoStartMinimized;

        _side = c.PeerSide;
        UpdateSideHighlight();
        UpdateSideHint();

        if (c.DropDir is "Desktop" or "Downloads") SelectTag(CbDropDir, c.DropDir);
        else { SelectTag(CbDropDir, "custom"); TbDropDirCustom.Text = c.DropDir; }
        UpdateDropDirVisibility();
        SelectTag(CbLogLevel, c.LogLevel.ToString());
        UpdateLocalIpHint();
        ApplyRoleView(animate: false);
        UpdateNameWatermark();
        UpdatePreview();
    }

    /// <summary>「（点击修改）」贴在本机名称框右端；点进去编辑时让位给文字。</summary>
    void UpdateNameWatermark()
        => TxtNameWatermark.Visibility = TbMachineName.IsKeyboardFocusWithin
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>被控端那个"可选对端地址"和主控端的主地址框其实是同一个配置项，两边互相同步。</summary>
    void TbPeerHostAlt_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingHost) return;
        _syncingHost = true;
        try { TbPeerHost.Text = TbPeerHostAlt.Text; }
        finally { _syncingHost = false; }
    }

    void TbMachineName_GotFocus(object sender, RoutedEventArgs e) => UpdateNameWatermark();
    void TbMachineName_LostFocus(object sender, RoutedEventArgs e) => UpdateNameWatermark();

    /// <summary>本机地址提示：第一排右端精简一行；「更多设置」里给出完整列表。</summary>
    void UpdateLocalIpHint()
    {
        var ips = Orchestrator.LocalIps();
        TxtLocalIpsInline.Text = ips.Count > 0
            ? $"本机 {ips[0]}" + (ips.Count > 1 ? $" (+{ips.Count - 1})" : "")
            : "未获取到本机地址";

        int port = int.TryParse(TbPort.Text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : _orch.Cfg.Port;
        TxtLocalIps.Text = ips.Count > 0
            ? "全部本机地址：" + string.Join("、", ips) + $"　端口 {port}"
            : "没获取到本机地址，请检查网络连接。";
    }

    /// <summary>按角色原位替换第三排：主控端=地址/扫描/连接，被控端=服务开关。两个视图同高，切换不跳动。</summary>
    void ApplyRoleView(bool animate = true)
    {
        bool server = RbServer.IsChecked != false;
        var show = server ? ViewServer : ViewClient;
        var hide = server ? ViewClient : ViewServer;
        bool changed = show.Visibility != Visibility.Visible;
        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        if (changed && animate) FadeIn(show);
        TxtFindLabel.Text = server ? "扫描发现" : "提示";
        // 被控端不需要扫描主控端 IP：整块发现列表收起来，换成一句操作提示，页面更干净
        DiscoverPanel.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        ClientTipPanel.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        if (!server) TbPeerHostAlt.Text = TbPeerHost.Text;   // 切过去时把已有地址带过来
        // 只在被控端显示（它在「更多设置」折叠区里，所以不会影响连接页的紧凑度）
        RowClientDial.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        UpdateServiceUi();
    }

    static void FadeIn(UIElement el)
    {
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>被控端「本机地址」那一行：地址、状态点、提示文案、按钮显隐。</summary>
    void UpdateServiceUi()
    {
        if (SvcDot == null) return;
        bool on = _orch.Cfg.ServiceOn;

        var ips = Orchestrator.LocalIps();
        int port = int.TryParse(TbPort.Text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : _orch.Cfg.Port;
        string first = ips.Count > 0 ? ips[0] : "（未获取到地址）";
        // 一律走 Res() 兜底：FindResource 在键缺失时会抛异常，而本函数由 DispatcherTimer
        // 周期调用，一次抛出就会变成"界面反复弹错误框"。取不到键只少上一处颜色，不崩。
        if (Res(on ? "OkBrush" : "Text3Brush") is Brush dot) SvcDot.Fill = dot;
        TxtSvcAddr.Text = ips.Count > 0 ? $"{first}:{port}" : first;
        if (Res(on ? "TextBrush" : "Text2Brush") is Brush addrFg) TxtSvcAddr.Foreground = addrFg;
        TxtSvcState.Text = on ? "服务已启动 · 等待主控端连接" : "服务未启动";

        // 未启动时只给「启动服务」；启动后换成「关闭」，两个按钮原位替换、不跳版
        BtnServiceStart.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        BtnServiceStop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
            TxtSvcHint.Text = $"服务已启动。请把地址「{TxtSvcAddr.Text}」填到主控端的「对端地址」里，" +
                              "由主控端点「连接」；这台会弹窗让你确认。";
        else
            TxtSvcHint.Text = ips.Count > 0
                ? "点「启动服务」后，把上面的地址告诉主控端（多网卡时哪个能通用哪个）。"
                : "没获取到本机地址，请检查网络连接。";
    }

    /// <summary>
    /// 反向控制相关的界面回填。
    ///
    /// 主控端：把配置里的许可状态同步到勾选框（别的入口改了配置也不会让界面说谎）。
    /// 被控端：用一枚小徽标说明「对端是否允许本机反过来控制」——被控端自己没有这个开关，
    ///         能不能反向申请完全由主控端那台决定，所以这里只做只读展示。
    /// </summary>
    void UpdateReverseUi()
    {
        if (CbAllowReverse != null)
        {
            bool want = _orch.Cfg.AllowReverseControl;
            if (CbAllowReverse.IsChecked != want)
            {
                _ready = false;
                CbAllowReverse.IsChecked = want;
                _ready = true;
            }
        }

        if (TxtRevBadge == null) return;
        bool peerAllows = _orch.ReverseAllowedByPeer;
        TxtRevBadge.Visibility = _orch.Connected ? Visibility.Visible : Visibility.Collapsed;
        if (!_orch.Connected) return;
        TxtRevBadge.Text = peerAllows ? "对端已允许反向控制" : "对端未允许反向控制";
        // 用 Res() 而不是 (Brush)FindResource()：资源键是运行时按字符串查的，写错键编译器
        // 完全看不见 —— v1.6.5 这里写成不存在的 "StrokeBrush"，界面线程每个刷新周期抛一次
        // ResourceReferenceKeyNotFoundException。取不到就保持原样，最多颜色不对，绝不崩。
        if (Res(peerAllows ? "OkBrush" : "Text3Brush") is Brush fg) TxtRevBadge.Foreground = fg;
        // 描边改用 RoleClientBrush（深青 #0F7389）而不是 RoleClientFill：
        // 方案 A′ 之后 Fill 是浅底，当描边用会淡到几乎看不见。
        if (Res(peerAllows ? "RoleClientBrush" : "LineStrongBrush") is Brush bd) RevBadge.BorderBrush = bd;
    }

    /// <summary>
    /// 安全取资源：找不到键、或键指向的不是 Brush 时返回 null（而不是抛异常）。
    /// 设置页由定时器周期性刷新，这里任何一次抛异常都会变成"界面一直弹错误框"，
    /// 所以宁可少上一处颜色也不能让它炸。键名是否正确由 tools/resource_key_check.py 静态兜住。
    /// </summary>
    Brush? Res(string key) => TryFindResource(key) as Brush;

    /// <summary>被控端：启动 / 关闭服务。密码由这台自己输入，不自动生成。</summary>
    void BtnService_Click(object sender, RoutedEventArgs e)
    {
        var c = _orch.Cfg;
        if (!c.ServiceOn)
        {
            // 启动前必须已有密码：直接弹窗让用户输入，不在页面上堆说明文字
            var pw = CurrentPassword();
            if (pw.Length == 0 && c.Password.Length == 0)
            {
                MessageBox.Show("请先输入连接密码。\n\n主控端要填一模一样的密码才能连进来。",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                FocusPassword();
                return;
            }
            if (pw.Length > 0) c.Password = pw;
            c.ServiceOn = true;
            ApplyConnectionFields();
            _orch.ApplyConfig();
            TxtScanStatus.Text = $"服务已启动，正在等待主控端连接…（地址 {TxtSvcAddr.Text}）";
        }
        else
        {
            c.ServiceOn = false;
            ApplyConnectionFields();
            _orch.ApplyConfig();
            TxtScanStatus.Text = "服务已停止，新的连接会被拒绝（当前已连上的不受影响）。";
        }
        UpdateServiceUi();
        Refresh();
    }

    void Role_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        string want = RbServer.IsChecked == true ? "server" : "client";

        // —— 运行中切换角色会立刻改变"谁控制谁"，必须让用户确认一次 ——
        // 以前点一下就直接换了：两台机器上谁点了角色按钮谁就改行为，对端毫无察觉，
        // 表现就是"用着用着逻辑乱了"。这里加了确认，并且断开现有连接（角色变了，
        // 原来的会话语义已经不成立，继续用它只会让两端状态对不上）。
        if (want != _orch.Cfg.Role && _orch.Connected)
        {
            var ans = MessageBox.Show(
                "本机当前已连接对端。\n\n" +
                $"把角色改成「{(want == "server" ? "主控端" : "被控端")}」会断开这次连接，" +
                "然后按新角色重新连。\n\n确定要切换吗？",
                AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes)
            {
                // 用户取消：把单选按钮拨回原来的角色，界面与配置保持一致
                _ready = false;
                if (_orch.Cfg.Role == "server") RbServer.IsChecked = true; else RbClient.IsChecked = true;
                _ready = true;
                return;
            }
        }

        ApplyRoleView();
        _orch.Cfg.Role = want;
        _orch.Cfg.Save();
        if (_orch.Connected) _orch.ApplyConfig();   // 角色变了 → 重建会话（ApplyConfig 会归零 Connected）
        TxtScanStatus.Text = want == "server"
            ? "已切到主控端：填对方地址或点「扫描」后按「连接」。"
            : "已切到被控端：在这台点「启动服务」，把本机地址告诉主控端。";
        Refresh();
    }

    /// <summary>
    /// 主控端：「允许被控端控制」许可开关。
    ///
    /// 打开后被控端可以申请反过来控制本机键鼠；控制权归属始终由"哪个鼠标在动"决定
    /// —— 本机鼠标一动就立刻收回。开关一变就地同步给对端，不必重连。
    /// </summary>
    void CbAllowReverse_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _orch.Cfg.AllowReverseControl = CbAllowReverse.IsChecked == true;
        _orch.Cfg.Save();
        _orch.NotifyReverseAllowedChanged();
        Refresh();
    }

    static void SelectTag(ComboBox cb, string? tag)
    {
        foreach (var it in cb.Items)
            if (it is ComboBoxItem ci && (string?)ci.Tag == tag) { cb.SelectedItem = ci; return; }
    }

    static string? SelectedTag(ComboBox cb) => (cb.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>上次手动扫描的结果（和自动发现的候选合并显示）。</summary>
    readonly List<PeerHit> _hits = new();

    /// <summary>
    /// 刷新发现列表：自动发现的候选 + 上次扫描结果，最多 8 条。
    /// 本机地址已在 Discovery 里被过滤，不会出现在这里。
    ///
    /// 去重分两层：
    ///   ①同端点（host:port）合并，避免"扫描结果"与"常驻发现"里同一条出现两遍；
    ///   ②同一台机器（按机器名）只留一条 —— 被控端同时挂在两个网段时会长出两个地址，
    ///     两个地址都探得通会让用户看到重复条目、误以为出了问题。地址更"近"的胜出。
    /// </summary>
    void RefreshDiscovered()
    {
        var all = new List<PeerHit>(_hits);
        foreach (var p in _orch.Disc.Peers()) all.Add(p);

        var byEndpoint = new Dictionary<string, PeerHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in all)
            if (!byEndpoint.ContainsKey(p.Where)) byEndpoint[p.Where] = p;

        var byMachine = new Dictionary<string, PeerHit>(StringComparer.OrdinalIgnoreCase);
        var unnamed = new List<PeerHit>();
        foreach (var p in byEndpoint.Values)
        {
            if (p.Name.Length == 0) { unnamed.Add(p); continue; }
            if (byMachine.TryGetValue(p.MachineKey, out var old))
            {
                if (p.Rank < old.Rank) byMachine[p.MachineKey] = p;
            }
            else byMachine[p.MachineKey] = p;
        }

        var merged = new List<PeerHit>(byMachine.Values);
        merged.AddRange(unnamed);
        merged.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Name.Length > 0 ? a.Name : "\uffff", b.Name.Length > 0 ? b.Name : "\uffff");
            if (c != 0) return c;
            c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Host, b.Host);
        });
        if (merged.Count > 8) merged = merged.GetRange(0, 8);

        var want = merged.ConvertAll(h => h.Title);
        var have = LbDiscovered.Items.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
        if (want.SequenceEqual(have)) return;          // 没变就别动，免得把用户的选中项/滚动位置抖掉

        var sel = LbDiscovered.SelectedItem as string;
        LbDiscovered.Items.Clear();
        foreach (var t in want) LbDiscovered.Items.Add(t);
        if (sel != null && want.Contains(sel)) LbDiscovered.SelectedItem = sel;
    }

    /// <summary>从列表项里抠出 host:port。</summary>
    static string? AddressOf(string item)
    {
        int a = item.IndexOf('('), b = item.IndexOf(')');
        return a >= 0 && b > a ? item.Substring(a + 1, b - a - 1) : null;
    }

    // ================= 状态刷新 =================

    void Refresh()
    {
        var (txt, state, _) = _orch.UiSnapshot();

        // 连接中 / 等对端确认时给文案加上滚动的省略号，并显示不确定进度条：
        // 只有一句静态"未连接"的话，用户很容易以为点了按钮没反应。
        bool busy = _orch.Busy;
        _dots++;
        TxtStatus.Text = "状态：" + txt + (busy ? new string('.', _dots % 3 + 1) : "");
        PbBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // 没连上、且已经积累过一次失败原因时才露出「复制诊断」按钮 ——
        // 平时不占地方，出问题时用户一眼就能看到入口（C1）。
        if (BtnDiag != null)
            BtnDiag.Visibility = (!_orch.Connected && _orch.LastConnectError.Length > 0)
                ? Visibility.Visible : Visibility.Collapsed;

        var vs = Vs();
        TxtMeCard.Text = _orch.MyName;
        TxtLocalRes.Text = $"{vs.W}×{vs.H}";
        TxtMeChip.Text = "本机 · " + _orch.MyName;
        var peer = _orch.PeerName;
        if (string.IsNullOrEmpty(peer)) TxtPeerChip.Text = "对端未连接";
        else
        {
            // 连上后顺带显示链路时延（C9）：跨网段/无线时用户能直观看到链路质量
            long rtt = _orch.PeerRttMs;
            TxtPeerChip.Text = rtt >= 0 ? $"对端 · {peer}　{rtt} ms" : "对端 · " + peer;
        }

        UpdateServiceUi();
        UpdateReverseUi();
        if (_dots % 15 == 0) UpdateLocalIpHint();
        if (_dots % 30 == 0) UpdateHotkeyTip();

        var (dot, detail) = state switch
        {
            IndState.Disconnected => (Color.FromRgb(0x9A, 0xA3, 0xAF), "等待对端连接"),
            IndState.Local => (Color.FromRgb(0x2E, 0xCC, 0x71), "鼠标在这台电脑上"),
            IndState.RemoteCtl => (Color.FromRgb(0xFF, 0x9F, 0x1A), "鼠标正在对面电脑上"),
            IndState.Controlled => (Color.FromRgb(0xFF, 0x45, 0x3A), "对面正在操作这台电脑"),
            IndState.Transferring => (Color.FromRgb(0x22, 0xD3, 0xEE), "正在传输文件"),
            IndState.Dragging => (Color.FromRgb(0xA8, 0x55, 0xF7), "正在拖拽文件"),
            IndState.DropReady => (Color.FromRgb(0x14, 0xB8, 0xA6), "松手即可投递"),
            IndState.DropBlocked => (Color.FromRgb(0xFF, 0x45, 0x3A), "当前位置不能投递"),
            _ => (Color.FromRgb(0x9A, 0xA3, 0xAF), ""),
        };
        StatusDot.Fill = new SolidColorBrush(dot);
        // 连接/等确认期间不显示光环状态那句话，免得和"正在连接 xxx"挤在一起看不懂
        TxtDetail.Text = busy ? "" : detail + (_orch.Cfg.TakeoverEnabled ? "" : "　· 接管已暂停");

        UpdateLiveHintBar();
        UpdatePreview(dot);
    }

    /// <summary>
    /// 常驻提示条（C4）：已连接时，用一句话说清"现在鼠标归谁、怎么操作"，
    /// 并在"鼠标在对端"时给出一个「回到本机」的动作按钮。
    ///
    /// 为什么值得做：这套软件的心智模型是"控制权跟着鼠标走"，但普通用户不会凭空知道
    /// 这一点。连上后一句随状态变化的提示，比在帮助文档里写十行都管用。
    /// </summary>
    void UpdateLiveHintBar()
    {
        if (BarLiveHint == null || TxtLiveHint == null) return;
        if (!_orch.Connected)
        {
            BarLiveHint.Visibility = Visibility.Collapsed;
            return;
        }
        BarLiveHint.Visibility = Visibility.Visible;

        bool rev = _orch.ReverseControlled;
        string backKey = _orch.HotkeyHint("1");
        string pauseKey = _orch.HotkeyHint("D");
        if (_orch.BeingControlled)
        {
            TxtLiveHint.Text = rev
                ? $"对面正在反向控制本机。你一动本机鼠标或键盘，控制权立刻回到你这台。"
                : $"本机正被「{_orch.PeerName}」控制。按 {pauseKey} 可暂停接管，或点「关闭服务」断开。";
            if (BtnLiveHintAction != null) BtnLiveHintAction.Visibility = Visibility.Collapsed;
        }
        else if (_orch.RemoteActive)
        {
            TxtLiveHint.Text = _orch.IsServerRole
                ? $"鼠标已推到「{_orch.PeerName}」，键盘跟着走。把鼠标推到屏幕另一端边缘即可回来。"
                : $"正在反向控制「{_orch.PeerName}」。把鼠标推回边缘或按 {backKey} 回到本机。";
            if (BtnLiveHintAction != null) BtnLiveHintAction.Visibility = Visibility.Visible;
        }
        else
        {
            TxtLiveHint.Text = $"已连接「{_orch.PeerName}」。把鼠标推到屏幕边缘即可切换控制对方；" +
                               "复制的内容在两边都能粘贴。";
            if (BtnLiveHintAction != null) BtnLiveHintAction.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>提示条上的「回到本机」：只在鼠标在对端时可见。</summary>
    void BtnLiveHintAction_Click(object sender, RoutedEventArgs e)
    {
        try { _orch.ReleaseControl("提示条：回到本机"); } catch { }
    }

    // ================= 热键修饰键（C2） =================

    /// <summary>按 Tag 选中 ComboBox 里的项；找不到就选第一项（保持有值）。</summary>
    static void SelectComboByTag(ComboBox cb, string? tag)
    {
        if (cb == null) return;
        int idx = 0;
        for (int i = 0; i < cb.Items.Count; i++)
        {
            if (cb.Items[i] is ComboBoxItem it && (it.Tag as string) == tag) { idx = i; break; }
        }
        cb.SelectedIndex = idx;
    }

    /// <summary>取 ComboBox 当前项的 Tag（没选中返回 null）。</summary>
    static string? SelectedComboTag(ComboBox cb) =>
        cb?.SelectedItem is ComboBoxItem it ? it.Tag as string : null;

    /// <summary>切换修饰键组合：立刻更新下方说明，让用户看到实际生效的按键。</summary>
    void CbHotkeyMods_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateHotkeyTip();
    }

    /// <summary>
    /// 刷新热键说明文字。文案直接读当前下拉框的值（而不是落盘后的 Cfg），
    /// 这样用户一改就能看到效果，不必先保存。
    /// </summary>
    void UpdateHotkeyTip()
    {
        if (TxtHotkeyTip == null) return;
        string mods = SelectedComboTag(CbHotkeyMods) ?? "CtrlAlt";
        string p = mods switch
        {
            "CtrlShift" => "Ctrl+Shift+",
            "AltShift" => "Alt+Shift+",
            _ => "Ctrl+Alt+",
        };
        TxtHotkeyTip.Text = $"当前热键：{p}2 跳到对端 · {p}1 拉回本机 · {p}L 锁定屏幕 · {p}D 暂停接管。" +
                            "（Ctrl+Alt+1 拉回本机永远有效，不受此处设置影响。）";
        // 连接页右上角那颗小药丸也同步（它是最先被看到的热键提示）
        if (TxtHotkeyChip != null)
            TxtHotkeyChip.Text = $"{p}2 过去 · 1 回来 · D 暂停";
    }

    /// <summary>
    /// 预览框一帧渲染。
    /// 左半边是固定 330×92 的画布，箭头/光圈/进度弧/徽标/气泡全在里面按**真实光环的几何**
    /// 摆位：气泡贴着光圈右缘、与光圈中心同高——上一版把气泡放在 Canvas 外面，
    /// 文案一变宽度就变，整个预览区跟着左右跳；而且尺寸小时气泡离光圈很远。
    /// </summary>
    void UpdatePreview(Color? overrideColor = null)
    {
        if (PreviewRing == null || PreviewCanvas == null) return;

        double size = SlIndicatorSize?.Value ?? 44;
        double stroke = Math.Max(3, size * 0.085);
        double ringCx = HotX + PreviewGapX + size / 2.0;
        double ringCy = HotY + PreviewDropY;
        double ringLeft = ringCx - size / 2.0, ringTop = ringCy - size / 2.0;

        Canvas.SetLeft(PreviewArrow, HotX);
        Canvas.SetTop(PreviewArrow, HotY);
        PreviewRing.Width = size;
        PreviewRing.Height = size;
        PreviewRing.StrokeThickness = stroke;
        Canvas.SetLeft(PreviewRing, ringLeft);
        Canvas.SetTop(PreviewRing, ringTop);
        Canvas.SetLeft(PreviewBadge, ringCx + size / 2.0 - 8);
        Canvas.SetTop(PreviewBadge, ringTop - 2);

        var spec = _legendHover;
        Color ringColor; string title, sub;
        bool showArc, dash = false; Color? badge = null;

        if (spec != null)
        {
            ringColor = spec.Color;
            title = spec.Title;
            sub = spec.Sub;
            showArc = spec.Arc;
            dash = spec.Dash;
            badge = spec.Badge;
        }
        else
        {
            ringColor = overrideColor ?? (_orch.IsServerRole
                ? (_orch.RemoteActive ? COrange : CGreen)
                : (_orch.BeingControlled ? CRed : CGreen));
            title = _orch.IsServerRole
                ? (_orch.RemoteActive ? "控制 " + (_orch.PeerName.Length > 0 ? _orch.PeerName : "对端") : "本机 · " + _orch.MyName)
                : (_orch.BeingControlled ? "被控中 · " + _orch.MyName : "本机 · " + _orch.MyName);
            sub = _orch.IsServerRole && _orch.RemoteActive ? "移到对端边缘可返回"
                : (_orch.Connected ? "鼠标在本机" : "等待对端连接");
            showArc = CbShowArc?.IsChecked == true;
            badge = CbShowBadge?.IsChecked == true ? BadgeColor(_orch.Badge) : null;
        }

        PreviewRing.Stroke = new SolidColorBrush(ringColor);
        if (dash)
        {
            PreviewRing.StrokeDashArray = new DoubleCollection { 2.6, 2.0 };
        }
        else
        {
            PreviewRing.StrokeDashArray = null;
            PreviewRing.StrokeDashOffset = 0;
        }

        bool enabled = CbIndicator?.IsChecked != false;
        bool textOn = CbIndicatorText?.IsChecked == true;
        PreviewPill.Visibility = textOn ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            PreviewTitle.Text = "光环已关闭";
            PreviewSub.Text = "勾选上方开关恢复";
        }
        else
        {
            PreviewTitle.Text = title;
            PreviewSub.Text = sub;
        }

        // 气泡紧贴光圈右缘、与光圈中心对齐：不论光环调到多少，间距都是 PreviewPillGap
        Canvas.SetLeft(PreviewPill, ringCx + size / 2.0 + PreviewPillGap);
        Canvas.SetTop(PreviewPill, ringCy - PreviewPillH / 2.0);

        if (showArc && enabled)
        {
            UpdatePreviewArc(MathX.Clamp(_previewProg, 0.02, 0.999), size, stroke, ringCx, ringCy);
            PreviewArc.Visibility = Visibility.Visible;
        }
        else PreviewArc.Visibility = Visibility.Collapsed;

        if (badge.HasValue && enabled)
        {
            PreviewBadge.Visibility = Visibility.Visible;
            PreviewBadge.Background = new SolidColorBrush(badge.Value);
        }
        else PreviewBadge.Visibility = Visibility.Collapsed;

        PreviewCanvas.Opacity = enabled ? 1.0 : 0.35;
    }

    void UpdatePreviewArc(double p, double size, double stroke, double cx, double cy)
    {
        double r = size / 2.0;
        Point Pt(double deg) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = Pt(-90), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(Pt(-90 + 360 * p), new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        PreviewArc.Data = pg;
        PreviewArc.StrokeThickness = stroke * 1.12;
    }

    static Color? BadgeColor(BadgeState b) => b switch
    {
        BadgeState.PendingFile => CYellow,
        BadgeState.ReadyFile => CGreen,
        BadgeState.Synced => CBadgeBlue,
        _ => null,
    };

    void PreviewAnimTick()
    {
        bool arcRunning = CbShowArc?.IsChecked == true || _legendHover?.Arc == true;
        if (arcRunning && PreviewArc.Visibility == Visibility.Visible)
        {
            _previewProg = (_previewProg + 0.022) % 1.0;
            UpdatePreview();
        }
        if (_legendHover?.Dash == true && PreviewRing.StrokeDashArray != null)
        {
            PreviewRing.StrokeDashOffset = (PreviewRing.StrokeDashOffset + 0.45) % 9.2;
        }
    }

    void BuildLegend()
    {
        // 标签写短一点（11 项要挤进 3 列，长了会被裁）；完整含义看悬停时左侧的演示和提示气泡
        _legend = new List<LegendItem>
        {
            new("灰：未连接", CGray, "未连接", "等待对端"),
            new("绿：控制本机", CGreen, "本机 · " + _orch.MyName, "鼠标在本机"),
            new("橙：控制对端", COrange, "控制 对端", "移到对端边缘可返回"),
            new("红：被对端控制", CRed, "被控中 · " + _orch.MyName, "来自 对端"),
            new("紫：拖拽中", CPurple, "拖拽中 · 3 项", "松开左键投给 对端", Dash: true),
            new("青绿：可投递", CTeal, "松手保存 → 资料", "3 个文件 · 12.8 MB"),
            new("青：传输中", CCyan, "传输中", "发送 3.2 / 5.0 MB", Arc: true),
            new("红：不能投递", CRed, "此处不能投递", "请移到文件夹窗口上方"),
            new("黄点：待接收", CYellow, "本机 · " + _orch.MyName, "对面发来文件，待接收", Badge: CYellow),
            new("绿点：可粘贴", CGreen, "本机 · " + _orch.MyName, "文件已就绪，Ctrl+V 粘贴", Badge: CGreen),
            new("蓝点：剪贴板同步", CBadgeBlue, "本机 · " + _orch.MyName, "剪贴板已同步", Badge: CBadgeBlue),
        };

        // 3 列 11 项，按 4 / 4 / 3 分布（用户指定）
        int[] dist = { 4, 4, 3 };
        int idx = 0;
        for (int col = 0; col < 3; col++)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (col > 0) sp.Margin = new Thickness(14, 0, 0, 0);
            for (int r = 0; r < dist[col] && idx < _legend.Count; r++)
            {
                sp.Children.Add(BuildLegendRow(_legend[idx++]));
            }
            Grid.SetColumn(sp, col);
            LegendHost.Children.Add(sp);
        }
    }

    FrameworkElement BuildLegendRow(LegendItem it)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(it.Color),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = it.Label,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xDE)),
        });
        // 整行可悬停：进入=左侧预览演示该状态，离开=恢复实时预览；
        // 悬停还带提示气泡，把完整含义（标签放不下的部分）补上。
        var hit = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(4, 3, 6, 3),
            CornerRadius = new CornerRadius(4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
            ToolTip = it.Title + "　·　" + it.Sub,
        };
        hit.MouseEnter += (_, _) =>
        {
            _legendHover = it;
            UpdatePreview();
        };
        hit.MouseLeave += (_, _) =>
        {
            if (_legendHover == it) { _legendHover = null; UpdatePreview(); }
        };
        return hit;
    }

    // ================= 布局方位 =================

    void Slot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            _side = tag;
            UpdateSideHighlight();
            UpdateSideHint();
        }
    }

    void UpdateSideHighlight()
    {
        var normal = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        var line = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
        // 同样走 Res() 兜底：这两处原本是 (Brush)FindResource(...)，键若被改名就会在
        // 刷新周期里抛异常。取不到时退回近似色，界面不至于失去高亮，但绝不崩。
        var accent = Res("AccentBrush") ?? new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0xD0));
        var soft = Res("AccentSoftBrush") ?? new SolidColorBrush(Color.FromRgb(0xEA, 0xF0, 0xFC));

        foreach (var (border, tag) in new[] { (SlotLeft, "left"), (SlotRight, "right"), (SlotTop, "top"), (SlotBottom, "bottom") })
        {
            if (border == null) continue;
            bool sel = tag == _side;
            border.Background = sel ? soft : normal;
            border.BorderBrush = sel ? accent : line;
            border.BorderThickness = new Thickness(sel ? 1.6 : 1);
        }
    }

    void UpdateSideHint()
    {
        TxtSideHint.Text = $"当前：对端在{Config.SideText(_side)}。鼠标推到本机屏幕的{Config.SideText(_side)}边缘就会切到对端；在对端的相对边缘推回来即返回本机。"
            + "（两台电脑的摆放与实际桌面一致，操作才顺手；被控端的这个设置会自动镜像，不用手动改。）";
    }

    // ================= 事件 =================

    void LbDiscovered_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LbDiscovered.SelectedItem is string s && AddressOf(s) is string addr) TbPeerHost.Text = addr;
        BtnConnect_Click(sender, e);   // 双击 = 直接连
    }

    /// <summary>
    /// 「扫描」：先广播找同网段，再做 TCP 端口探测兜底跨路由器。
    /// 分两阶段、边扫边把结果推上来，所以状态下能实时看到"已找到几台 / 扫到第几台"。
    /// </summary>
    async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        if (!int.TryParse(TbPort.Text.Trim(), out int port) || port is < 1 or > 65535)
            port = _orch.Cfg.Port;

        _scanning = true;
        BtnScan.IsEnabled = false;
        BtnConnect.IsEnabled = false;
        TxtScanStatus.Text = "正在扫描局域网…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var list = await _orch.Disc.ScanNowAsync(port, rep =>
            {
                // 回到 UI 线程更新（扫描在后台跑）
                UiUtil.Run(() =>
                {
                    _hits.Clear();
                    _hits.AddRange(rep.Hits);
                    RefreshDiscovered();
                    TxtScanStatus.Text = rep.Total > 0
                        ? $"{rep.Phase}（已找到 {rep.Hits.Count} 台，用时 {sw.Elapsed.TotalSeconds:F0} 秒）"
                        : rep.Phase;
                });
            });

            _hits.Clear();
            _hits.AddRange(list);
            RefreshDiscovered();
            if (LbDiscovered.Items.Count > 0) LbDiscovered.SelectedIndex = 0;

            // 只扫到一台就顺手把地址填好，省一次点击
            if (list.Count == 1 && TbPeerHost.Text.Trim().Length == 0)
                TbPeerHost.Text = list[0].Where;

            TxtScanStatus.Text = list.Count switch
            {
                0 => $"扫描完成：没找到（用时 {sw.Elapsed.TotalSeconds:F0} 秒）。" +
                     "确认对方软件在运行、密码一致；还找不到就把对方界面上的「本机地址」直接填到上面点「连接」。",
                1 => $"扫描完成：找到 1 台 —— {list[0].Title}，地址已自动填好，点「连接」即可。",
                _ => $"扫描完成：找到 {list.Count} 台（本机不会出现在列表里）。" +
                     "点一下选中要连的那台再按「连接」，双击可直接连。",
            };
        }
        catch (Exception ex)
        {
            TxtScanStatus.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _scanning = false;
            BtnScan.IsEnabled = true;
            BtnConnect.IsEnabled = true;
        }
    }

    /// <summary>
    /// 「连接」：优先用列表里选中的那台，否则用填写的对端地址。
    /// 两端角色都会主动拨号，所以这个按钮在主控端才显示（被控端用「启动服务」）。
    /// </summary>
    void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (LbDiscovered.SelectedItem is string s && AddressOf(s) is string addr) TbPeerHost.Text = addr;

        var host = TbPeerHost.Text.Trim();
        if (host.Length == 0)
        {
            TxtScanStatus.Text = "请先填对端 IP 地址，或点「扫描」自动找对端。";
            TbPeerHost.Focus();
            return;
        }
        if (!EnsurePassword()) return;

        ApplyConnectionFields();

        if (_orch.TargetIsSelf)
        {
            TxtScanStatus.Text = host + " 是本机自己的地址，连自己没意义。" +
                                 "请填对方那台电脑的 IP —— 在对方界面上能看到它的「本机地址」。";
            return;
        }

        _orch.ApplyConfig();     // 按最新设置重建监听与发现
        _orch.ReconnectNow();    // 立刻踢一下连接循环，不用等下一轮定时器
        Refresh();
        TxtScanStatus.Text = "正在连接 " + host + " …　连不上先点「防火墙放行」；对方要是设了确认，" +
                             "还得在对方那边点一下「允许连接」。";
    }

    /// <summary>
    /// 连接密码是必需的（HMAC 挑战应答用它验身份）。没设过就在这里弹窗提示，
    /// 而不是在页面上堆一大段说明文字 —— 只在用户真的要用的时候才打扰。
    /// </summary>
    bool EnsurePassword()
    {
        var pw = CurrentPassword();
        if (pw.Length > 0 || _orch.Cfg.Password.Length > 0) return true;
        // 被控端：密码由使用者自己输入（主控端要填同样的密码才连得上）
        if (RbClient.IsChecked == true)
        {
            MessageBox.Show("请先输入连接密码。\n\n这个密码要和主控端填的完全一样，主控端才能连进来。",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            FocusPassword();
            return false;
        }
        var r = MessageBox.Show(
            "还没有设置连接密码。\n\n两台电脑必须用一模一样的密码才能连上，" +
            "现在帮你随机生成一个吗？\n（生成后请照着填到另一台电脑上）",
            AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (r == MessageBoxResult.OK)
        {
            DoGeneratePassword();
            return true;
        }
        FocusPassword();
        return false;
    }

    /// <summary>当前密码：明/密两个控件取那个有内容的（两者始终同步）。</summary>
    string CurrentPassword()
        => _plainVisible ? TbPasswordPlain.Text : PbPassword.Password;

    void FocusPassword()
    {
        if (_plainVisible) TbPasswordPlain.Focus(); else PbPassword.Focus();
    }

    /// <summary>「显示 / 隐藏」：在明文与遮字之间切换，同时把另一侧同步过去。</summary>
    void BtnPwdEye_Click(object sender, RoutedEventArgs e)
    {
        _plainVisible = !_plainVisible;
        _syncingPwd = true;
        try
        {
            if (_plainVisible)
            {
                TbPasswordPlain.Text = PbPassword.Password;
                TbPasswordPlain.Visibility = Visibility.Visible;
                PbPassword.Visibility = Visibility.Collapsed;
                BtnPwdEye.Content = "隐藏";
                TbPasswordPlain.CaretIndex = TbPasswordPlain.Text.Length;
                TbPasswordPlain.Focus();
            }
            else
            {
                PbPassword.Password = TbPasswordPlain.Text;
                PbPassword.Visibility = Visibility.Visible;
                TbPasswordPlain.Visibility = Visibility.Collapsed;
                BtnPwdEye.Content = "显示";
                PbPassword.Focus();
            }
        }
        finally { _syncingPwd = false; }
    }

    void PbPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingPwd) return;
        _syncingPwd = true;
        try { TbPasswordPlain.Text = PbPassword.Password; }
        finally { _syncingPwd = false; }
    }

    void TbPasswordPlain_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingPwd) return;
        _syncingPwd = true;
        try { PbPassword.Password = TbPasswordPlain.Text; }
        finally { _syncingPwd = false; }
    }

    /// <summary>「生成」：随机 8 位数字，纯数字最好念、最好输。</summary>
    void BtnGenPwd_Click(object sender, RoutedEventArgs e)
    {
        DoGeneratePassword();
        ApplyConnectionFields();
        TxtScanStatus.Text = "已生成密码并保存。把「" + PbPassword.Password + "」填到另一台电脑上，再点「连接」。";
    }

    void DoGeneratePassword()
    {
        var pwd = MakePassword(8);
        _syncingPwd = true;
        try
        {
            PbPassword.Password = pwd;
            TbPasswordPlain.Text = pwd;
        }
        finally { _syncingPwd = false; }
        _orch.Cfg.Password = pwd;
        _orch.Cfg.Save();
        Log.Info("已生成新的连接密码");
    }

    static string MakePassword(int len)
    {
        var bytes = Rand.Bytes(len);
        var sb = new System.Text.StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)('0' + bytes[i] % 10));
        return sb.ToString();
    }

    /// <summary>把连接相关的表单项写进配置并保存（不动其它页的未保存改动）。</summary>
    void ApplyConnectionFields()
    {
        var c = _orch.Cfg;
        c.MachineLabel = TbMachineName.Text.Trim();
        c.Role = RbServer.IsChecked == true ? "server" : "client";
        var pw = CurrentPassword();
        if (pw.Length > 0) c.Password = pw;
        if (int.TryParse(TbPort.Text.Trim(), out int port) && port is >= 1 and <= 65535) c.Port = port;
        c.TlsEnabled = CbTls.IsChecked == true;
        c.AutoDiscover = CbAutoDiscover.IsChecked == true;
        c.ConfirmOnConnect = CbConfirm.IsChecked == true;
        c.PeerHost = TbPeerHost.Text.Trim();
        c.HotkeysEnabled = CbHotkeys.IsChecked == true;
        c.Save();
    }

    /// <summary>按当前设置立刻尝试一次连接（客户端立即触发；主控端本就在监听，仅刷新状态）。</summary>
    void BtnReconnect_Click(object sender, RoutedEventArgs e)
    {
        _orch.ReconnectNow();
        Refresh();
    }

    void BtnFirewall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UiUtil.SetFirewallRule();
            MessageBox.Show("已请求添加防火墙规则，请在弹出的授权窗口中确认。", AppInfo.Name);
        }
        catch
        {
            MessageBox.Show("未能添加规则（可能被取消）。也可手动放行本程序或 TCP 端口。", AppInfo.Name);
        }
    }

    /// <summary>点击邮箱 → 打开系统邮件客户端发信（mailto:）。</summary>
    void LnkContact_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:" + AppInfo.Contact)
            { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show("没能打开邮件客户端。邮箱：" + AppInfo.Contact, AppInfo.Name);
        }
    }

    /// <summary>复制邮箱到剪贴板：图标短暂变成对勾（不写文字，保持轻量）。</summary>
    void BtnCopyContact_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(AppInfo.Contact); } catch { }
        IcoCopy.Data = Geometry.Parse(CheckIconData);
        // Res() 兜底：取不到 OkBrush 就保持原描边色，不抛异常（本方法在按钮回调里，
        // 抛出会弹未处理异常框）。
        if (Res("OkBrush") is Brush ok) IcoCopy.Stroke = ok;
        BtnCopyContact.ToolTip = "已复制";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            IcoCopy.Data = Geometry.Parse(CopyIconData);
            if (Res("Text2Brush") is Brush t2) IcoCopy.Stroke = t2;
            BtnCopyContact.ToolTip = "复制邮箱地址";
        };
        t.Start();
    }

    void BtnResetTrust_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("将清除已记住的对端证书指纹，下次连接会重新信任对方。\n仅在对端确实重装过本软件时才需要。\n\n继续？",
                AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _orch.Cfg.PinnedServerThumb = "";
        _orch.Cfg.Save();
        MessageBox.Show("已重置，下次连接会重新记录指纹。", AppInfo.Name);
    }

    // ==================== 软件更新 ====================

    UpdateInfo? _update;           // 最近一次检查的结果
    volatile bool _updatingBusy;   // 防重复点击

    /// <summary>联网查 GitHub 最新 Release，把结果写进状态行。</summary>
    async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingBusy) return;
        _updatingBusy = true;
        BtnCheckUpdate.IsEnabled = false;
        BtnDoUpdate.Visibility = Visibility.Collapsed;
        TxtUpdateStatus.Text = "正在检查更新…";
        try
        {
            var info = await UpdateChecker.QueryLatestAsync();
            if (info == null)
            {
                TxtUpdateStatus.Text = "线上还没有发布过版本。";
            }
            else if (!info.HasUpdate)
            {
                _update = null;
                TxtUpdateStatus.Text = $"已是最新版本（v{AppInfo.Version}）。";
            }
            else
            {
                _update = info;
                TxtUpdateStatus.Text = $"发现新版本 v{info.LatestVersion}（当前 v{AppInfo.Version}），点击「下载并安装」升级。";
                BtnDoUpdate.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            _updatingBusy = false;
        }
    }

    /// <summary>下载新版：Win10+64 位自动替换重启；Win7/32 位下载 zip 后打开所在文件夹手动解压。</summary>
    async void BtnDoUpdate_Click(object sender, RoutedEventArgs e)
    {
        var info = _update;
        if (info == null || !info.HasUpdate || info.AssetId == 0 || _updatingBusy) return;
        _updatingBusy = true;
        BtnDoUpdate.IsEnabled = false;
        BtnCheckUpdate.IsEnabled = false;
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string dest = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(string.IsNullOrEmpty(exePath) ? "." : exePath) ?? ".",
                info.AssetName);

            PbUpdate.Visibility = Visibility.Visible;
            PbUpdate.Value = 0;
            TxtUpdateStatus.Text = $"正在下载 {info.AssetName}（{info.AssetSize / 1048576.0:0.0} MB）…";
            var progress = new Progress<double>(p =>
            {
                PbUpdate.Value = p;
                TxtUpdateStatus.Text = $"正在下载… {p:0}%";
            });
            await UpdateChecker.DownloadAssetAsync(info.AssetId, dest, progress);
            PbUpdate.Value = 100;

            if (dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Win7 / 32 位：没有自包含单文件，自动替换只认 exe；给出 zip 让用户手动覆盖
                TxtUpdateStatus.Text = $"新版本已下载：{dest}\n请解压后覆盖旧文件。";
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dest}\"");
                }
                catch { }
                BtnDoUpdate.Visibility = Visibility.Collapsed;
                return;
            }

            if (MessageBox.Show($"新版本已下载完成。\n\n现在重启并替换为新版本吗？（旧程序会保留为 .old 备份）",
                    AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                TxtUpdateStatus.Text = $"新版本已下载到：{dest}\n可稍后手动改名替换，或重新点击「下载并安装」。";
                return;
            }

            // 自替换脚本：等本进程退出 → 旧 exe 改 .old → 新 exe 就位 → 拉起新程序。
            // 必须经 cmd 中转，因为运行中的 exe 不能覆盖自己。
            string newFile = dest;
            string log = System.IO.Path.Combine(Core.AppPaths.LogDir, "update.log");
            string script =
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                $"move /y \"{exePath}\" \"{exePath}.old\" >> \"{log}\" 2>&1\r\n" +
                $"move /y \"{newFile}\" \"{exePath}\" >> \"{log}\" 2>&1\r\n" +
                $"start \"\" \"{exePath}\"\r\n";
            string bat = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mb-update.cmd");
            System.IO.File.WriteAllText(bat, script, System.Text.Encoding.GetEncoding(936));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"")
            { CreateNoWindow = true, UseShellExecute = false });

            TxtUpdateStatus.Text = "正在重启升级…";
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = "更新失败：" + ex.Message;
            PbUpdate.Visibility = Visibility.Collapsed;
        }
        finally
        {
            BtnDoUpdate.IsEnabled = true;
            BtnCheckUpdate.IsEnabled = true;
            _updatingBusy = false;
        }
    }

    void CbDropDir_Changed(object sender, SelectionChangedEventArgs e) => UpdateDropDirVisibility();

    void UpdateDropDirVisibility()
    {
        bool custom = SelectedTag(CbDropDir) == "custom";
        TbDropDirCustom.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        BtnPickDir.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    void BtnPickDir_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择默认投递接收目录" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TbDropDirCustom.Text = dlg.SelectedPath;
    }

    void SlIndicatorSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtSizeVal == null) return;
        int v = (int)e.NewValue;
        TxtSizeVal.Text = v.ToString();
        UpdatePreview();
        if (!_ready) return;
        // 拖滑块即时生效：直接改运行中的光环，不必保存
        _orch.SetIndicatorLive(v, CbIndicatorText.IsChecked == true, CbShowArc.IsChecked == true);
    }

    // 指示器相关开关：勾一下立刻在真实光环上生效（不用先保存），预览同步跟着变
    void CbIndicator_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (!_ready) return;
        _orch.SetIndicatorEnabledLive(CbIndicator.IsChecked == true);
    }

    void CbIndicatorText_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (!_ready) return;
        _orch.SetIndicatorLive((int)SlIndicatorSize.Value, CbIndicatorText.IsChecked == true,
            CbShowArc.IsChecked == true);
    }

    void CbShowArc_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (!_ready) return;
        _orch.SetIndicatorLive((int)SlIndicatorSize.Value, CbIndicatorText.IsChecked == true,
            CbShowArc.IsChecked == true);
    }

    void CbShowBadge_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (!_ready) return;
        _orch.SetBadgeLive(CbShowBadge.IsChecked == true);
    }

    /// <summary>打开"传输记录与运行日志"窗口。</summary>
    void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        var w = new HistoryWindow(_orch);
        w.Owner = this;
        w.Show();
    }

    void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{AppPaths.LogDir}\""); } catch { }
    }

    void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 复制连接诊断信息到剪贴板。
    ///
    /// 目的（C1）：用户遇到"连不上"时，最需要的是**现场信息**——版本、角色、两端地址、
    /// 密码到底设没设、失败原因、日志尾部。以前这些散落在日志文件里，用户要么找不到、
    /// 要么不会表述。这里一键打包成纯文本，粘贴出来就能直接定位问题。
    /// 注意**绝不包含密码明文**，只报"是否已设置"。
    /// </summary>
    void BtnDiag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var c = _orch.Cfg;
            var ips = Orchestrator.LocalIps();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("==== 鼠下快传 连接诊断 ====");
            sb.AppendLine($"版本: {AppInfo.Version}");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"本机名: {_orch.MyName}");
            sb.AppendLine($"本机地址: {(ips.Count > 0 ? string.Join(" / ", ips) : "（未获取到）")}:{c.Port}");
            sb.AppendLine($"当前角色: {(c.Role == "server" ? "主控端" : "被控端")}");
            sb.AppendLine($"连接密码: {(c.Password.Length > 0 ? "已设置" : "未设置")}");
            sb.AppendLine($"对端地址(手填): {(string.IsNullOrWhiteSpace(c.PeerHost) ? "（未填，靠扫描）" : c.PeerHost)}");
            sb.AppendLine($"自动发现: {(c.AutoDiscover ? "开" : "关")}   TLS: {(c.TlsEnabled ? "开" : "关")}");
            sb.AppendLine($"当前目标: {(_orch.CurrentTarget.Length > 0 ? _orch.CurrentTarget : "（无）")}");
            sb.AppendLine($"已连接: {(_orch.Connected ? "是" : "否")}");
            sb.AppendLine($"最近错误: {(_orch.LastConnectError.Length > 0 ? _orch.LastConnectError : "（无）")}");
            sb.AppendLine();
            sb.AppendLine("---- 日志尾部 ----");
            try { sb.AppendLine(string.Join(Environment.NewLine, new LogTail().Tail(40))); }
            catch { sb.AppendLine("（日志读取失败）"); }

            System.Windows.Clipboard.SetText(sb.ToString());
            BtnDiag.Content = "已复制";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { t.Stop(); BtnDiag.Content = "复制诊断"; };
            t.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制诊断信息失败：" + ex.Message, AppInfo.Name);
        }
    }

    void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TbPort.Text.Trim(), out int port) || port is < 1 or > 65535)
        { MessageBox.Show("端口无效（1-65535）", AppInfo.Name); return; }
        if (!int.TryParse(TbSwitchDelay.Text.Trim(), out int delay) || delay < 0) delay = 0;
        if (!long.TryParse(TbEagerCap.Text.Trim(), out long eagerCap) || eagerCap < 1) eagerCap = 256;
        if (!long.TryParse(TbClipCap.Text.Trim(), out long clipCap) || clipCap < 1) clipCap = 2048;

        var pw = PbPassword.Password;
        if (pw.Length == 0 && string.IsNullOrEmpty(_orch.Cfg.Password))
        { MessageBox.Show("请设置连接密码（两台电脑保持一致）", AppInfo.Name); return; }

        var c = _orch.Cfg;
        c.MachineLabel = TbMachineName.Text.Trim();
        c.Role = RbServer.IsChecked == true ? "server" : "client";
        if (pw.Length > 0) c.Password = pw;
        c.Port = port;
        c.TlsEnabled = CbTls.IsChecked == true;
        c.AutoDiscover = CbAutoDiscover.IsChecked == true;
        c.PeerHost = TbPeerHost.Text.Trim();
        c.HotkeysEnabled = CbHotkeys.IsChecked == true;
        c.HotkeyMods = SelectedComboTag(CbHotkeyMods) ?? "CtrlAlt";
        c.TakeoverEnabled = CbTakeover.IsChecked == true;
        c.PeerSide = _side;
        c.SwitchDelayMs = delay;
        c.LockKeyEnabled = CbLockKey.IsChecked == true;
        c.SyncText = CbSyncText.IsChecked == true;
        c.SyncImage = CbSyncImage.IsChecked == true;
        c.SyncFiles = CbSyncFiles.IsChecked == true;
        c.EagerReceive = CbEagerReceive.IsChecked == true;
        c.EagerCapMB = eagerCap;
        c.ClipCapMB = clipCap;
        c.DragDropAcrossMachines = CbDragDrop.IsChecked == true;
        c.DropFallbackToDefaultDir = CbFallback.IsChecked == true;
        c.OpenFolderAfterDrop = CbOpenFolder.IsChecked == true;
        c.IndicatorEnabled = CbIndicator.IsChecked == true;
        c.IndicatorSize = (int)SlIndicatorSize.Value;
        c.IndicatorText = CbIndicatorText.IsChecked == true;
        c.ShowProgressArc = CbShowArc.IsChecked == true;
        c.ShowBadge = CbShowBadge.IsChecked == true;
        c.AutoStartMinimized = CbAutoStartMinimized.IsChecked == true;

        var dropTag = SelectedTag(CbDropDir) ?? "Desktop";
        c.DropDir = dropTag switch
        {
            "Desktop" => "Desktop",
            "Downloads" => "Downloads",
            _ => string.IsNullOrWhiteSpace(TbDropDirCustom.Text) ? "Desktop" : TbDropDirCustom.Text.Trim(),
        };
        if (Enum.TryParse<LogLevel>((SelectedTag(CbLogLevel) ?? "Info"), out var lv)) c.LogLevel = lv;

        bool autoStart = CbAutoStart.IsChecked == true;
        if (autoStart != c.AutoStart) { c.AutoStart = autoStart; UiUtil.SetAutoStart(autoStart); }

        c.Configured = true;
        // 记下保存前是否已连接：ApplyConfig 一定会把链路断掉重来，
        // 所以文案要分两种情形说清楚 —— 用户选了"连接中保存"，结果看到"已保存"却不知
        // 掉线了，会以为软件坏了。
        bool wasConnected = _orch.Connected;
        try
        {
            c.Save();
            _orch.ApplyConfig();
            if (wasConnected)
            {
                // 已连接状态下的保存 = 会断开并按新配置重连。给一次明确的二次确认。
                var r = MessageBox.Show(
                    "配置已保存。\n\n由于改动可能影响连接（角色 / 密码 / 端口 / 地址），" +
                    "程序需要断开当前连接并按新配置重新连接。\n\n现在重连？",
                    AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.No)
                {
                    // 用户想稍后自己连：保留配置，只是不主动重连（ApplyConfig 已经把旧链路断了）
                    MessageBox.Show("配置已保存，暂未重连。可稍后在主界面点「连接」。", AppInfo.Name);
                    Close();
                    return;
                }
                MessageBox.Show("已保存并按新配置重连。\n请确保另一台电脑设置为相反角色、相同密码。", AppInfo.Name);
            }
            else
            {
                MessageBox.Show("已保存并应用。\n请确保另一台电脑设置为相反角色、相同密码。", AppInfo.Name);
            }
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, AppInfo.Name);
        }
    }
}
