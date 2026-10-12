using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MouseBridge.Ui;

/// <summary>
/// 连接确认弹窗（被控端）。
///
/// 主控端密码验证通过后，被控端会弹出这个窗口问一句"是否允许"：
/// 密码对了不代表就该放行 —— 同网段知道密码的人也可能随时接管你的键鼠。
/// 带倒计时，超时按拒绝处理（默认更安全），窗口不写进任务栏。
/// </summary>
public class AskWindow : Window
{
    readonly TaskCompletionSource<bool> _tcs = new();
    readonly TextBlock _count = new();
    readonly DispatcherTimer _timer;
    int _left;

    public Task<bool> Answer => _tcs.Task;

    public AskWindow(string peerName, string peerWhere, int seconds = 60, bool reverse = false)
    {
        _left = seconds;

        Title = (reverse ? "反向控制请求 · " : "连接请求 · ") + AppInfo.Name;   // 无边框不显示标题，但自动化截图要靠它找窗口
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = TryFont();

        var title = new TextBlock
        {
            Text = reverse ? "反向控制请求" : "连接请求",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x24)),
        };
        var who = new TextBlock
        {
            Text = reverse ? "「" + peerName + "」请求控制本机" : "「" + peerName + "」请求连接本机",
            FontSize = 13,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x24)),
            TextWrapping = TextWrapping.Wrap,
        };
        var addr = new TextBlock
        {
            Text = peerWhere,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x71, 0x7D)),
        };
        var warn = new TextBlock
        {
            Text = reverse
                ? "允许后，对方可以操作本机的鼠标和键盘。你一动自己的鼠标就能立刻收回控制权。"
                : "允许后，对方可以操作本机的鼠标和键盘。",
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00)),
            TextWrapping = TextWrapping.Wrap,
        };

        _count.FontSize = 11.5;
        _count.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x71, 0x7D));
        _count.Margin = new Thickness(0, 10, 0, 0);

        var deny = MakeButton("拒绝", false);
        deny.Click += (_, _) => Finish(false);
        var allow = MakeButton("允许连接", true);
        allow.Click += (_, _) => Finish(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(deny);
        buttons.Children.Add(allow);

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(who);
        stack.Children.Add(addr);
        stack.Children.Add(warn);
        stack.Children.Add(_count);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0xD8, 0xDE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16, 18, 16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.28,
                Color = Colors.Black,
            },
            Child = stack,
        };

        UpdateCount();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _left--;
            UpdateCount();
            if (_left <= 0) Finish(false);
        };
        _timer.Start();

        // 用户直接关窗口（Alt+F4）也算拒绝
        Closed += (_, _) => _tcs.TrySetResult(false);
        SourceInitialized += (_, _) => Native.RoundCorners(new WindowInteropHelper(this).Handle);
    }

    static FontFamily TryFont()
    {
        try
        {
            if (Application.Current?.TryFindResource("UiFont") is FontFamily f) return f;
        }
        catch { }
        return new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, sans-serif");
    }

    static Brush Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }

    static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 96,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12.5,
            Padding = new Thickness(10, 0, 10, 0),
        };
        if (primary)
        {
            b.Background = Res("AccentBrush", Color.FromRgb(0x1F, 0x5F, 0xD0));
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
        }
        return b;
    }

    void UpdateCount()
        => _count.Text = _left > 0 ? $"{_left} 秒内没有回应就自动拒绝。" : "已超时，按拒绝处理。";

    void Finish(bool allow)
    {
        _timer.Stop();
        _tcs.TrySetResult(allow);
        try { Close(); } catch { }
    }
}
