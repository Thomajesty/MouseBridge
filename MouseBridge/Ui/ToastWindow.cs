using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MouseBridge.Ui;

/// <summary>右下角轻提示：连接状态、传输进度、拖拽投递结果等。</summary>
public class ToastWindow : Window
{
    readonly TextBlock _title = new() { FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 2) };
    readonly TextBlock _msg = new() { FontSize = 12, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD3, 0xDA)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    readonly System.Windows.Controls.ProgressBar _bar = new() { Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xC2, 0xFF)) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _hideAt = DateTime.MinValue;

    // —— 传输控制按钮（需求：传输过程可暂停/停止）——
    // 只在传输进度显示期间出现；普通提示（3 秒自隐藏那种）一律隐藏。
    readonly StackPanel _btnRow;
    readonly Button _btnPause, _btnStop;

    /// <summary>用户点了「暂停/继续」。</summary>
    public event Action? PauseClicked;
    /// <summary>用户点了「停止」。</summary>
    public event Action? StopClicked;

    public ToastWindow()
    {
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        // 关键：让窗口高度跟着内容走。不设 SizeToContent 时，WindowStyle=None +
        // AllowsTransparency 的窗口会用一个默认高度，内容被拉满 → 截图上那块
        // "上面一行字下面一大片空白"的方框就是这么来的。
        SizeToContent = SizeToContent.Height;

        // 深色底上的扁平小按钮：单色描边、无渐变无阴影（与全局视觉规范一致）
        Button MakeBtn(string text, Action? click)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 72,
                Height = 26,
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x38)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x60)),
                BorderThickness = new Thickness(1),
                // 覆盖全局样式模板可能带来的高亮色，悬停/按下用浅一点的底
                Resources = new ResourceDictionary(),
                Padding = new Thickness(10, 0, 10, 0),
            };
            b.Click += (_, _) => click?.Invoke();
            return b;
        }
        _btnPause = MakeBtn("暂停", () => PauseClicked?.Invoke());
        _btnStop = MakeBtn("停止", () => StopClicked?.Invoke());
        // 停止键给一点危险色描边，与全局"危险"视觉一致（深底上用降饱和的红）
        _btnStop.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0x3A, 0x33));
        _btnStop.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0xB5));

        _btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Children = { _btnPause, new Border { Width = 8 }, _btnStop },
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEA, 0x1E, 0x24, 0x2B)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0xC2, 0xFF)),
            BorderThickness = new Thickness(1),
            // StackPanel 靠上对齐，别让子项被拉高
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { _title, _msg, _bar, _btnRow } },
        };
        Width = 340;
        Content = border;

        _hideTimer.Tick += (_, _) =>
        {
            if (_hideAt == DateTime.MinValue) return;
            if (DateTime.Now >= _hideAt) { Hide(); _hideAt = DateTime.MinValue; }
        };
        _hideTimer.Start();

        Loaded += (_, _) => Position();
        // 高度随内容变化后要重新贴右下角，否则窗口变矮时会浮在原地
        SizeChanged += (_, _) => Position();
    }

    /// <summary>
    /// 传输控制按钮的显隐与暂停态文案。visible=false 时按钮行收起（普通提示态）。
    /// </summary>
    public void SetTransferControls(bool visible, bool paused)
    {
        if (_closed) return;
        _btnRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _btnPause.Content = paused ? "继续" : "暂停";
    }

    // —— 自动化探针（MB_VERIFY_UI）：只读内部状态 / 模拟点击，不进产品逻辑 ——
    public bool SetTransferControlsProbeVisible => _btnRow.Visibility == Visibility.Visible;
    public string SetTransferControlsProbePauseText => _btnPause.Content as string ?? "";
    public void RaiseTransferProbeClicks()
    {
        PauseClicked?.Invoke();
        StopClicked?.Invoke();
    }

    void Position()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Bottom - (ActualHeight > 0 ? ActualHeight : 110) - 16;
    }

    /// <summary>seconds=0 表示保持显示（由下一次调用或进度条决定）。</summary>
    public void Show(string title, string msg, double? progress, int seconds)
    {
        // 退出过程中窗口已经关掉了，这时再 Show 会抛 InvalidOperationException，
        // 把"关机时的正常收尾"污染成一条 Error 日志。
        if (_closed) return;
        _title.Text = title;
        _msg.Text = msg;
        if (progress.HasValue)
        {
            _bar.Visibility = Visibility.Visible;
            _bar.Value = MathX.Clamp(progress.Value, 0, 1);
            _hideAt = DateTime.MinValue;
        }
        else
        {
            _bar.Visibility = Visibility.Collapsed;
            _hideAt = seconds > 0 ? DateTime.Now.AddMilliseconds(seconds) : DateTime.Now.AddSeconds(3);
            // 普通提示不出现传输按钮（按钮行是否显示由 SetTransferControls 决定，
            // 但非传输态调用方不会带按钮；这里双保险：非进度提示若紧接着自隐藏，按钮跟着隐藏）
            if (seconds != 0) _btnRow.Visibility = Visibility.Collapsed;
        }
        if (!IsVisible) Show();
        // 绝不用 Activate()：抢焦点会打断用户正在打字的窗口
        Topmost = false; Topmost = true;
        Position();
    }

    bool _closed;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _hideTimer.Stop();
        base.OnClosed(e);
    }
}
