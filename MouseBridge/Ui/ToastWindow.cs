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

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEA, 0x1E, 0x24, 0x2B)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0xC2, 0xFF)),
            BorderThickness = new Thickness(1),
            // StackPanel 靠上对齐，别让子项被拉高
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { _title, _msg, _bar } },
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
