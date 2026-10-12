using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MouseBridge.Core;

namespace MouseBridge.Ui;

/// <summary>
/// 被控端屏幕右上角的常驻横幅：**只在正被对端控制时出现**。
///
/// 为什么要它：被控端在被远控时，用户看到鼠标自己乱动却不清楚发生了什么、
/// 也不知道怎么中断。这个横幅给出三件事 ——
///   ① 明确告知"正在被 XXX 控制"；
///   ② 一个「暂停接管」按钮，点一下立刻收回控制权（不必去设置页找）；
///   ③ 提示 Ctrl+Alt+D 也能暂停。
///
/// 不抢焦点（ShowActivated=false），点击按钮也不影响前台窗口打字。
/// </summary>
public class ControlBanner : Window
{
    readonly TextBlock _title = new()
    {
        FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Brushes.White,
    };
    readonly TextBlock _sub = new()
    {
        FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xD4, 0xE4)),
    };
    readonly Button _pause;

    /// <summary>点「暂停接管」时触发（由 Orchestrator 接上"收回控制权"）。</summary>
    public event Action? PauseRequested;

    public ControlBanner()
    {
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;          // 绝不抢焦点
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        _pause = new Button
        {
            Content = "暂停接管",
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            ToolTip = "立刻收回鼠标键盘控制权（也可以按 Ctrl+Alt+D）",
        };
        _pause.Click += (_, _) => { Hide(); PauseRequested?.Invoke(); };

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
        };
        // 小圆点的呼吸动画：让"正在被控制"这件事一眼可见
        var breathe = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.25,
            TimeSpan.FromMilliseconds(900))
        { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        dot.BeginAnimation(UIElement.OpacityProperty, breathe);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _sub } };
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dot, texts },
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { left, _pause },
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x8B, 0x1A, 0x1A)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 9, 12, 9),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = row,
        };

        Loaded += (_, _) => Position();
    }

    void Position()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Top + 16;              // 右上角
    }

    /// <summary>
    /// 被控开始时显示，带上是谁在控制。
    /// reverse=true 表示这是"被控端反过来控制本机"（反向控制），文案要说清怎么收回。
    /// <paramref name="pauseKey"/> 由调用方按用户自定义的热键组合传入（C2），
    /// 不在这里写死 Ctrl+Alt+D —— 否则用户换了组合后横幅就在撒谎。
    /// </summary>
    public void ShowControlledBy(string peer, bool reverse = false, string pauseKey = "Ctrl+Alt+D")
    {
        if (_closed) return;
        string who = string.IsNullOrWhiteSpace(peer) ? "对端" : peer;
        _title.Text = reverse ? "正在被「" + who + "」反向控制" : "正在被「" + who + "」控制";
        _sub.Text = reverse
            ? $"你一动本机鼠标就能立刻收回控制权，或按 {pauseKey} 暂停"
            : $"不想被接管可点右侧按钮，或按 {pauseKey} 暂停";
        _pause.ToolTip = $"立刻收回鼠标键盘控制权（也可以按 {pauseKey}）";
        if (!IsVisible) Show();
        Topmost = false; Topmost = true;    // 抬到最前，但不抢焦点
        Position();
    }

    bool _closed;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
}
