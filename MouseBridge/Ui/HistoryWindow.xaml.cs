using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseBridge.Control;
using MouseBridge.Core;

namespace MouseBridge.Ui;

/// <summary>
/// 传输记录与运行日志：把"传了哪些文件、传到哪、成没成功"和"程序后台都干了什么"
/// 集中到一个独立窗口，随时打开查看，不必去翻 AppData 目录。
/// </summary>
public partial class HistoryWindow : Window
{
    readonly Orchestrator _orch;
    readonly LogTail _tail = new(3000);
    readonly DispatcherTimer _timer;

    string _logStamp = "";
    long _listStamp = -1;
    long _seenId = -1;
    bool _ready;                 // XAML 解析期（如 ComboBoxItem.IsSelected）会触发事件，此时控件还没建好

    public HistoryWindow(Orchestrator orch)
    {
        InitializeComponent();
        _orch = orch;
        Native.ClampMaximizeToWorkArea(this);   // 无边框窗口最大化别盖住任务栏
        TxtVersion.Text = "v" + AppInfo.Version;
        TxtMeChip.Text = _orch.MyName;
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

        // 打开时先把已有日志读进来
        _tail.Poll();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) => Refresh();
    }

    // ================= 刷新 =================

    void Refresh()
    {
        if (!_ready) return;
        RefreshStatus();
        RefreshCurrentTask();
        RefreshRecords();
        RefreshLog();
    }

    void RefreshStatus()
    {
        var (txt, state, _) = _orch.UiSnapshot();
        TxtStatus.Text = txt;

        var (color, label) = state switch
        {
            IndState.Disconnected => (Color.FromRgb(0x9A, 0xA3, 0xAF), "离线"),
            IndState.Local => (Color.FromRgb(0x2E, 0xCC, 0x71), "在线"),
            IndState.RemoteCtl => (Color.FromRgb(0xFF, 0x9F, 0x1A), "在控对端"),
            IndState.Controlled => (Color.FromRgb(0xFF, 0x45, 0x3A), "被对端控制"),
            IndState.Dragging => (Color.FromRgb(0xA8, 0x55, 0xF7), "拖拽中"),
            IndState.DropReady => (Color.FromRgb(0x14, 0xB8, 0xA6), "可投递"),
            IndState.DropBlocked => (Color.FromRgb(0xFF, 0x45, 0x3A), "不可投递"),
            IndState.Transferring => (Color.FromRgb(0x22, 0xD3, 0xEE), "传输中"),
            _ => (Color.FromRgb(0x9A, 0xA3, 0xAF), ""),
        };
        StatusDot.Fill = new SolidColorBrush(color);

        string peer = _orch.PeerName;
        TxtPeerChip.Text = string.IsNullOrEmpty(peer) ? "对端未连接" : $"对端 {peer}";
        TxtModeChip.Text = $"角色 {(_orch.IsServerRole ? "主控端" : "被控端")}";
        TxtDetail.Text = $"光环状态 {label} · 监听端口 {_orch.Cfg.Port}";
    }

    void RefreshCurrentTask()
    {
        var (busy, sending, sent, total, label) = _orch.XferSnapshot();
        if (!busy)
        {
            TxtCurTitle.Text = "当前没有文件传输";
            TxtCurPct.Text = "";
            PbCur.Value = 0;
            TxtCurSub.Text = $"上次传输：{LastSummary()}";
            return;
        }

        double p = total > 0 ? MathX.Clamp(sent / (double)total, 0, 1) : 0;
        TxtCurTitle.Text = string.IsNullOrEmpty(label) ? (sending ? "正在发送文件" : "正在接收文件") : label;
        TxtCurPct.Text = $"{p * 100:F0}%";
        PbCur.Value = p;
        var active = TransferJournal.Active;
        string speed = active?.SpeedText ?? "—";
        string eta = active?.EtaText ?? "—";
        TxtCurSub.Text = $"{Fmt(sent)} / {Fmt(total)} · 速度 {speed} · 剩余 {eta}";
    }

    string LastSummary()
    {
        var list = TransferJournal.Snapshot();
        if (list.Count == 0) return "暂无记录";
        var r = list[0];
        return $"{r.WhenText} {r.DirText} {r.CountText} · {r.SizeText} · {r.StateText}";
    }

    static string Fmt(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / 1048576.0:F1} MB";
        return $"{b / 1073741824.0:F2} GB";
    }

    void RefreshRecords()
    {
        var all = TransferJournal.Snapshot();
        var active = TransferJournal.Active;

        // 失败筛选（C5）：失败/中断的记录最需要被看见，用一次性过滤复制出显示列表，
        // 避免在原集合上反复 Where 造成每次刷新都产生新序列（也会打断选中项保持）。
        bool onlyFailed = TbOnlyFailed?.IsChecked == true;
        List<XferRecord> list;
        if (onlyFailed)
        {
            list = new List<XferRecord>(all.Count);
            foreach (var r in all)
                if (r.State is XferState.Failed or XferState.Interrupted) list.Add(r);
        }
        else list = all;

        long stamp = list.Count * 1_000_003L
                     + (active?.Id ?? -1) * 7919L
                     + (active?.DoneBytes ?? 0)
                     + (onlyFailed ? 7L : 0L);
        if (stamp == _listStamp) return;
        _listStamp = stamp;

        long keep = _seenId;
        if (LvRecords.SelectedItem is XferRecord sel) keep = sel.Id;

        LvRecords.ItemsSource = null;
        LvRecords.ItemsSource = list;

        int failCount = 0;
        foreach (var r in all)
            if (r.State is XferState.Failed or XferState.Interrupted) failCount++;

        if (all.Count == 0) TxtRecordCount.Text = "共 0 条";
        else if (onlyFailed) TxtRecordCount.Text = $"只看失败 · {list.Count} / {all.Count} 条";
        else
            TxtRecordCount.Text = $"共 {all.Count} 条 · 成功 {all.Count(x => x.State == XferState.Done)} · " +
                                  $"失败 {failCount}";

        if (keep > 0)
        {
            foreach (var r in list)
                if (r.Id == keep) { LvRecords.SelectedItem = r; break; }
        }
    }

    void RefreshLog()
    {
        if (TxtLog == null || CbAutoScroll == null) return;
        _tail.Poll();
        var tail = _tail.Tail(600, SelectedLevel());

        string stamp = tail.Count == 0 ? "0" : $"{tail.Count}|{tail[tail.Count - 1]}";
        if (stamp != _logStamp)
        {
            _logStamp = stamp;
            TxtLog.Text = string.Join(Environment.NewLine, tail);
            string f = _tail.CurrentFile is null ? "（暂无日志）" : Path.GetFileName(_tail.CurrentFile);
            TxtLogFile.Text = $"· {f} · 显示 {tail.Count} 行";
        }
        // 每次都滚到底：刚设置完文本时布局还没走完，只在那时滚会停在开头
        if (CbAutoScroll.IsChecked == true) TxtLog.ScrollToEnd();
    }

    LogLevel SelectedLevel()
    {
        if (CbLogLevel.SelectedItem is ComboBoxItem it && it.Tag is string s && int.TryParse(s, out int v))
            return (LogLevel)v;
        return LogLevel.Info;
    }

    // ================= 交互 =================

    void CbLogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _logStamp = "";
        RefreshLog();
    }

    /// <summary>「只看失败」筛选开关。切换时强制重建列表（清掉 stamp 缓存）。</summary>
    void TbOnlyFailed_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _listStamp = -1;
        RefreshRecords();
    }

    void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _listStamp = -1; _logStamp = "";
        Refresh();
    }

    void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = TransferJournal.Export();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")); } catch { }
            MessageBox.Show(this, $"已导出到：\n{path}", "导出传输记录", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出传输记录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void BtnClearRecords_Click(object sender, RoutedEventArgs e)
    {
        if (TransferJournal.Snapshot().Count == 0) return;
        var r = MessageBox.Show(this, "确定清空全部传输记录吗？（日志文件不受影响）", "清空记录",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        TransferJournal.Clear();
        _listStamp = -1;
        RefreshRecords();
    }

    void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{AppPaths.LogDir}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开日志目录失败：" + ex.Message, "打开日志目录", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>双击记录：能定位到本地路径就打开它。</summary>
    void LvRecords_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LvRecords.SelectedItem is not XferRecord r) return;
        try
        {
            var t = r.Target;
            if (string.IsNullOrEmpty(t) || t.StartsWith("剪贴板") || t.StartsWith("→")) return;
            if (Directory.Exists(t))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{t}\"");
            else if (File.Exists(t))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{t}\""));
            else if (Directory.Exists(Path.GetDirectoryName(t)))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(t)}\"");
        }
        catch { }
    }

    void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
