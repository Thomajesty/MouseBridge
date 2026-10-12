using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Threading;
using MouseBridge.Control;
using MouseBridge.Core;

namespace MouseBridge.Ui;

/// <summary>
/// 系统托盘图标与菜单。
/// 图标 = 应用图标 + 右下角状态色点（灰/绿/橙/红/青/紫），一眼看出当前处于哪种状态。
/// 每种颜色只创建一次并缓存句柄，退出时统一释放，不泄漏 GDI 资源。
/// </summary>
public class TrayIcon : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _ni;
    readonly Orchestrator _orch;
    readonly DispatcherTimer _pump;
    readonly System.Windows.Forms.ToolStripMenuItem _miState = new("状态：未知") { Enabled = false };
    readonly System.Windows.Forms.ToolStripMenuItem _miPause = new("暂停接管");
    readonly System.Windows.Forms.ToolStripMenuItem _miJump = new("跳到对端");
    readonly System.Windows.Forms.ToolStripMenuItem _miBack = new("拉回本机");
    SettingsWindow? _settings;
    HistoryWindow? _history;
    string _lastColor = "";

    static readonly Dictionary<string, (Color color, Icon icon, IntPtr handle)> _cache = new();
    static Bitmap? _baseIcon;

    public TrayIcon(Orchestrator orch)
    {
        _orch = orch;
        _ni = new System.Windows.Forms.NotifyIcon
        {
            Icon = IconFor(Color.Gray),
            Text = AppInfo.Title,
            Visible = true,
        };
        _ni.DoubleClick += (_, _) => ShowSettings();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        _miJump.Click += (_, _) => _orch.HotkeyJumpToPeer();
        _miBack.Click += (_, _) => _orch.HotkeyBackToLocal();
        _miPause.Click += (_, _) =>
        {
            _orch.SetTakeover(!_orch.Cfg.TakeoverEnabled);
            _miPause.Text = _orch.Cfg.TakeoverEnabled ? "暂停接管" : "恢复接管";
        };
        menu.Items.Add("打开设置", null, (_, _) => ShowSettings());
        menu.Items.Add("传输记录与日志…", null, (_, _) => ShowHistory());
        menu.Items.Add(_miState);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_miJump);
        menu.Items.Add(_miBack);
        menu.Items.Add(_miPause);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("关于 " + AppInfo.Version, null, (_, _) => ShowAbout());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _ni.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });
        _ni.ContextMenuStrip = menu;
        _miPause.Text = _orch.Cfg.TakeoverEnabled ? "暂停接管" : "恢复接管";
        menu.Opening += (_, _) => { _miPause.Text = _orch.Cfg.TakeoverEnabled ? "暂停接管" : "恢复接管"; };

        _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _pump.Tick += (_, _) => Pump();
        _pump.Start();
    }

    void ShowSettings()
    {
        if (_settings == null || !_settings.IsVisible)
        {
            _settings = new SettingsWindow(_orch);
            _settings.Show();
        }
        else
        {
            if (_settings.WindowState == System.Windows.WindowState.Minimized)
                _settings.WindowState = System.Windows.WindowState.Normal;
            _settings.Activate();
        }
    }

    /// <summary>打开"传输记录与运行日志"窗口（已开着就前置）。</summary>
    public void ShowHistory()
    {
        if (_history == null || !_history.IsVisible)
        {
            _history = new HistoryWindow(_orch);
            _history.Show();
        }
        else
        {
            if (_history.WindowState == System.Windows.WindowState.Minimized)
                _history.WindowState = System.Windows.WindowState.Normal;
            _history.Activate();
        }
    }

    void ShowAbout()
    {
        System.Windows.MessageBox.Show(
            $"{AppInfo.Title}\n版本 {AppInfo.Version}\n本机名：{_orch.MyName}\n\n" +
            "一套键鼠在两台 Windows 电脑之间自由穿梭；\n" +
            "剪贴板（文字/图片/文件）自动同步；\n" +
            "按住文件推过屏幕边缘，可直接拖到对端任意文件夹里松手。",
            AppInfo.Name);
    }

    void Pump()
    {
        var (txt, state, _) = _orch.UiSnapshot();
        _ni.Text = $"{AppInfo.Title} - {txt}";
        _miState.Text = "状态：" + txt;
        _miJump.Enabled = _orch.IsServerRole && _orch.Connected && !_orch.RemoteActive;
        _miBack.Enabled = _orch.IsServerRole && _orch.RemoteActive;

        var color = ColorOf(state);
        var key = color.ToArgb().ToString();
        if (key != _lastColor)
        {
            _lastColor = key;
            _ni.Icon = IconFor(color);
        }
    }

    static Color ColorOf(IndState s) => s switch
    {
        IndState.Local => Color.FromArgb(46, 204, 113),
        IndState.RemoteCtl => Color.FromArgb(255, 159, 26),
        IndState.Controlled => Color.FromArgb(255, 69, 58),
        IndState.Transferring => Color.FromArgb(34, 211, 238),
        IndState.Dragging => Color.FromArgb(168, 85, 247),
        IndState.DropReady => Color.FromArgb(20, 184, 166),
        IndState.DropBlocked => Color.FromArgb(255, 69, 58),
        _ => Color.FromArgb(150, 160, 172),
    };

    // ---------- 图标绘制 ----------

    static Bitmap? BaseIcon()
    {
        if (_baseIcon != null) return _baseIcon;
        try
        {
            var exe = AppExe.Path;
            if (exe != null)
            {
                using var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null)
                {
                    using var b = ico.ToBitmap();
                    _baseIcon = new Bitmap(b, new Size(64, 64));
                    _baseIcon.SetResolution(96, 96);
                    return _baseIcon;
                }
            }
        }
        catch { }
        return null;
    }

    static Icon FallbackGlyph(Color c, out IntPtr handle)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var b1 = new SolidBrush(Color.FromArgb(70, 70, 80));
        using var b2 = new SolidBrush(c);
        g.FillRectangle(b1, 0, 4, 6, 8);
        g.FillRectangle(b1, 10, 4, 6, 8);
        g.FillEllipse(b2, 6, 6, 4, 4);
        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    static Icon IconFor(Color c)
    {
        var key = c.ToArgb().ToString();
        if (_cache.TryGetValue(key, out var hit)) return hit.icon;

        Icon icon;
        IntPtr handle = IntPtr.Zero;
        try
        {
            var baseBmp = BaseIcon();
            if (baseBmp == null)
            {
                icon = FallbackGlyph(c, out handle);
            }
            else
            {
                using var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(baseBmp, new Rectangle(0, 0, 15, 15));
                    // 右下角状态点
                    using var outline = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                    using var dot = new SolidBrush(c);
                    g.FillEllipse(outline, 7.5f, 7.5f, 8.5f, 8.5f);
                    g.FillEllipse(dot, 8.5f, 8.5f, 6.5f, 6.5f);
                }
                handle = bmp.GetHicon();
                icon = Icon.FromHandle(handle);
            }
        }
        catch
        {
            icon = FallbackGlyph(c, out handle);
        }
        _cache[key] = (c, icon, handle);
        return icon;
    }

    public void Dispose()
    {
        try { _pump.Stop(); _ni.Visible = false; _ni.Dispose(); } catch { }
        foreach (var kv in _cache)
        {
            try { kv.Value.icon.Dispose(); } catch { }
            try { if (kv.Value.handle != IntPtr.Zero) Input.Native.DestroyIcon(kv.Value.handle); } catch { }
        }
        _cache.Clear();
        try { _baseIcon?.Dispose(); } catch { }
        _baseIcon = null;
    }
}
