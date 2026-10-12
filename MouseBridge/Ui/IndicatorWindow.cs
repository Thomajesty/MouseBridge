using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseBridge.Control;
using MouseBridge.Input;

namespace MouseBridge.Ui;

/// <summary>
/// 光标光环：挂在光标箭头右侧的状态圈，一眼看出"现在控制的是哪台电脑、文件在哪一步"，
/// 同时不遮挡箭头正指向的内容。
///
/// 颜色语义
///   灰 = 未连接　　绿 = 控制本机　　橙 = 正在控制对端　　红 = 正被对端控制
///   紫 = 拖拽中（可跨屏投递）　　青绿 = 松手即可投递到高亮文件夹　　红 = 此处无法投递
///   青虚线旋转 = 正在传输文件（外圈进度弧显示百分比）
/// 徽标（右上角小圆点/数字）
///   黄 = 对端文件待接收　绿 = 文件已就绪可粘贴　蓝 = 剪贴板已同步　数字 = 待处理条目数
/// </summary>
public class IndicatorWindow : Window
{
    // —— 视觉元素 ——
    readonly Canvas _canvas = new();
    readonly Ellipse _ring = new();
    readonly Path _arc = new();
    readonly Border _badge = new();
    readonly TextBlock _badgeText = new();
    readonly Border _pill = new();
    readonly TextBlock _title = new();
    readonly TextBlock _detail;
    readonly Grid _grid = new();

    readonly DispatcherTimer _timer;

    /// <summary>光圈与文字气泡之间的间距。</summary>
    const double PillGap = 10;

    /// <summary>
    /// 光圈左缘与光标尖端的水平间距。光圈不再罩在光标上——压在箭头上会挡住
    /// 正要点击的目标；挂在箭头右侧（箭头本体约 12px 宽，再留 4px 空隙）。
    /// </summary>
    const double CursorGapX = 16;

    /// <summary>光圈中心相对光标热点的垂直下移：箭头向下延伸约 19px，取其形心看起来是"跟手"的。</summary>
    const double CursorDropY = 8;

    // —— 状态 ——
    bool _enabled;                 // 初始 false：首次 Configure(true) 才会 Show()
    bool _showBadge = true;
    bool _showText = true;
    bool _showArc = true;
    bool _pillOnLeft;              // 文字气泡是否翻到了光圈左侧（贴近屏幕右缘时）
    int _ringSize = 44;
    int _box;                       // 光环方框边长
    IndState _state = IndState.Disconnected;
    BadgeState _badgeState = BadgeState.None;
    double _progress;
    string? _text;
    double _lastLeft = double.NaN, _lastTop = double.NaN;
    bool _animating;

    static readonly Color CGray = Color.FromRgb(0x9A, 0xA3, 0xAF);
    static readonly Color CGreen = Color.FromRgb(0x2E, 0xCC, 0x71);
    static readonly Color COrange = Color.FromRgb(0xFF, 0x9F, 0x1A);
    static readonly Color CRed = Color.FromRgb(0xFF, 0x45, 0x3A);
    static readonly Color CCyan = Color.FromRgb(0x22, 0xD3, 0xEE);
    static readonly Color CPurple = Color.FromRgb(0xA8, 0x55, 0xF7);
    static readonly Color CTeal = Color.FromRgb(0x14, 0xB8, 0xA6);
    static readonly Brush PanelBrush = new SolidColorBrush(Color.FromArgb(0xE8, 0x1B, 0x21, 0x29));

    public IndicatorWindow()
    {
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Width;   // 宽度随文字气泡自然伸缩，高度随内容
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _ring.Stroke = new SolidColorBrush(CGray);
        _ring.Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        // 先加光圈、后加进度弧：弧画在光圈之上，否则会被同圆的光圈整条盖住
        _canvas.Children.Add(_ring);
        _canvas.Children.Add(_arc);

        _arc.Stroke = new SolidColorBrush(CCyan);
        _arc.StrokeThickness = 5;
        _arc.StrokeStartLineCap = PenLineCap.Round;
        _arc.StrokeEndLineCap = PenLineCap.Round;
        _arc.Visibility = Visibility.Collapsed;

        _badgeText.FontSize = 9.5;
        _badgeText.FontWeight = FontWeights.SemiBold;
        _badgeText.Foreground = Brushes.White;
        _badgeText.HorizontalAlignment = HorizontalAlignment.Center;
        _badgeText.VerticalAlignment = VerticalAlignment.Center;
        _badge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x00));
        _badge.CornerRadius = new CornerRadius(7);
        _badge.BorderThickness = new Thickness(1.5);
        _badge.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x1C, 0x22));
        _badge.MinWidth = 14;
        _badge.Height = 14;
        _badge.Padding = new Thickness(2, 0, 2, 0);
        _badge.Child = _badgeText;
        _badge.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(_badge);

        _title.FontSize = 12;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Brushes.White;
        _title.TextAlignment = TextAlignment.Center;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _detail = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB6, 0xC2, 0xD0)),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_detail);
        _pill.Background = PanelBrush;
        _pill.CornerRadius = new CornerRadius(7);
        _pill.Padding = new Thickness(8, 4, 8, 4);
        _pill.BorderThickness = new Thickness(1);
        _pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        _pill.Child = stack;
        _pill.MaxWidth = 320;
        _pill.Visibility = Visibility.Collapsed;

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_canvas, 1);
        _canvas.VerticalAlignment = VerticalAlignment.Center;
        _grid.Children.Add(_canvas);
        // 文字气泡默认挂在光圈右边（列 2）；靠近屏幕右边缘时整体翻到左边（列 0）
        Grid.SetColumn(_pill, 2);
        _pill.VerticalAlignment = VerticalAlignment.Center;
        _pill.Margin = new Thickness(PillGap, 0, 0, 0);
        _grid.Children.Add(_pill);
        Content = _grid;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => FollowCursor();

        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeOverlay(hwnd);
            ApplyGeometry();
            _timer.Start();
        };
    }

    // ================= 对外接口 =================

    public void Configure(bool enabled, int size, bool badge, bool text, bool arc)
    {
        _showBadge = badge;
        _showText = text;
        _showArc = arc;
        _ringSize = MathX.Clamp(size, 20, 72);

        bool was = _enabled;
        _enabled = enabled;

        if (!enabled)
        {
            _timer.Stop();
            Hide();
            return;
        }
        ApplyGeometry();
        Render();
        if (!was || !IsVisible) Show();
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void SetView(IndState state, BadgeState badge, double progress, string? text, string? detail)
    {
        _state = state;
        _badgeState = badge;
        _progress = progress;
        _text = string.IsNullOrWhiteSpace(text) ? null : text;
        _detail.Text = detail ?? "";
        _title.Text = _text ?? "";
        Render();
    }

    // ================= 几何 =================

    void ApplyGeometry()
    {
        double stroke = Math.Max(3, _ringSize * 0.085);
        _box = (int)Math.Ceiling(_ringSize + stroke * 2 + 4);
        _ring.Width = _ringSize;
        _ring.Height = _ringSize;
        _ring.StrokeThickness = stroke;
        _ring.RenderTransform = null;
        Canvas.SetLeft(_ring, (_box - _ringSize) / 2.0);
        Canvas.SetTop(_ring, (_box - _ringSize) / 2.0);

        _canvas.Width = _box;
        _canvas.Height = _box;

        _badge.RenderTransform = null;
        Canvas.SetLeft(_badge, _box - 15);
        Canvas.SetTop(_badge, 3);

        _arc.Width = _box;
        _arc.Height = _box;
        // 弧比光圈略粗一点，压在压暗的光圈上，"已传多少"才看得清
        _arc.StrokeThickness = stroke * 1.12;
        Canvas.SetLeft(_arc, 0);
        Canvas.SetTop(_arc, 0);

        UpdateArcPath();
        Probe();
    }

    /// <summary>
    /// 出图/自测用：设置 MB_HALO_PROBE=1 时把当前几何写进 %TEMP%\mb-halo-geom.txt，
    /// 便于自动化确认"设置页滑块 → 运行中的光环"这条链路真的生效。
    /// </summary>
    void Probe()
    {
        if (Environment.GetEnvironmentVariable("MB_HALO_PROBE") != "1") return;
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mb-halo-geom.txt");
            System.IO.File.AppendAllText(path,
                $"ring={_ringSize} box={_box} stroke={_ring.StrokeThickness:F2} vis={IsVisible}{Environment.NewLine}");
        }
        catch { }
    }

    void UpdateArcPath()
    {
        if (!_showArc || _progress <= 0.001 || _progress >= 0.999)
        {
            if (_progress >= 0.999 && _showArc && _progress > 0)
            {
                // 收尾：整圈
                var rr = _ringSize / 2.0;
                var g = new EllipseGeometry(new Point(_box / 2.0, _box / 2.0), rr, rr);
                _arc.Data = g;
                _arc.Visibility = Visibility.Visible;
                return;
            }
            _arc.Visibility = Visibility.Collapsed;
            return;
        }
        double r = _ringSize / 2.0;
        var c = new Point(_box / 2.0, _box / 2.0);
        double p = MathX.Clamp(_progress, 0.001, 0.999);
        Point Pt(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = Pt(-90), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(Pt(-90 + 360 * p), new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        _arc.Data = pg;
        _arc.Visibility = Visibility.Visible;
    }

    void FollowCursor()
    {
        try
        {
            if (!IsVisible) return;
            Native.GetCursorPos(out var p);

            // 物理像素 → WPF 逻辑像素（PerMonitorV2，跨不同 DPI 显示器也对）
            double sx = 1, sy = 1, ox = 0, oy = 0;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                var m = src.CompositionTarget.TransformFromDevice;
                sx = m.M11; sy = m.M22; ox = m.OffsetX; oy = m.OffsetY;
            }
            double x = p.X * sx + ox;
            double y = p.Y * sy + oy;

            double ringHalf = _box / 2.0;
            var wa = SystemParameters.WorkArea;

            // 文字默认在光圈右侧；右侧放不下就翻到左侧（带滞回，避免边缘抖动）
            if (_pill.Visibility == Visibility.Visible && _pill.ActualWidth > 0)
            {
                double need = ringHalf + PillGap + _pill.ActualWidth;
                bool wantLeft = _pillOnLeft;
                if (!_pillOnLeft)
                {
                    if (x + need > wa.Right - 6) wantLeft = true;
                }
                else
                {
                    // 只有右侧真的腾出空间才翻回去；否则留在左侧，避免在两个条件间反复横跳
                    if (x + need < wa.Right - 36) wantLeft = false;
                }
                if (wantLeft != _pillOnLeft)
                {
                    _pillOnLeft = wantLeft;
                    ApplyPillSide();
                    return;   // ApplyPillSide 里会再调一次 FollowCursor
                }
            }

            // 光圈挂在光标箭头右侧（不再罩在光标上）：窗口左缘 = 光标 + 间距 - 光圈左缘在窗口内的偏移
            double w = ActualWidth > 0 ? ActualWidth : _box;
            double h = ActualHeight > 0 ? ActualHeight : _box;
            double ringCx = _pillOnLeft && _pill.Visibility == Visibility.Visible
                ? _pill.ActualWidth + PillGap + _box / 2.0
                : _box / 2.0;
            double ringLeftInWin = ringCx - _box / 2.0;

            double left = x + CursorGapX - ringLeftInWin;
            double top = y + CursorDropY - h / 2.0;

            if (double.IsNaN(_lastLeft) || double.IsNaN(_lastTop)
                || Math.Abs(left - _lastLeft) >= 0.5 || Math.Abs(top - _lastTop) >= 0.5)
            {
                _lastLeft = left; _lastTop = top;
                Left = left; Top = top;
            }
        }
        catch { }
    }

    // ================= 渲染 =================

    static Color ColorOf(IndState s) => s switch
    {
        IndState.Disconnected => CGray,
        IndState.Local => CGreen,
        IndState.RemoteCtl => COrange,
        IndState.Controlled => CRed,
        IndState.Dragging => CPurple,
        IndState.DropReady => CTeal,
        IndState.DropBlocked => CRed,
        IndState.Transferring => CCyan,
        _ => CGray,
    };

    public static string StateText(IndState s) => s switch
    {
        IndState.Disconnected => "未连接",
        IndState.Local => "本机",
        IndState.RemoteCtl => "控制对端",
        IndState.Controlled => "被对端控制",
        IndState.Dragging => "拖拽中",
        IndState.DropReady => "可投递",
        IndState.DropBlocked => "不可投递",
        IndState.Transferring => "传输中",
        _ => ""
    };

    void Render()
    {
        if (!_enabled) return;

        var color = ColorOf(_state);

        // 传输中：外圈当作"轨道"压暗，进度弧用满色叠在上面，这样百分比一眼看得出；
        // 旋转虚线只留给拖拽态（表示"还攥在手里"），避免与进度弧同色同圈互相掩盖。
        bool transferring = _state == IndState.Transferring;
        var ringColor = transferring ? Color.FromArgb(0x59, color.R, color.G, color.B) : color;
        _ring.Stroke = new SolidColorBrush(ringColor);
        _arc.Stroke = new SolidColorBrush(color);
        _ring.Fill = _state switch
        {
            IndState.DropReady => new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)),
            IndState.Dragging => new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
            _ => new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
        };

        bool spin = _state is IndState.Dragging;
        if (spin && !_animating)
        {
            _animating = true;
            _ring.StrokeDashArray = new DoubleCollection { 2.6, 2.0 };
            var anim = new DoubleAnimation(0, 9.2, new Duration(TimeSpan.FromSeconds(1.1)))
            { RepeatBehavior = RepeatBehavior.Forever };
            _ring.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
        }
        else if (!spin && _animating)
        {
            _animating = false;
            _ring.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            _ring.StrokeDashArray = null;
            _ring.StrokeDashOffset = 0;
        }

        if (_state == IndState.Controlled && !spin)
        {
            _ring.Opacity = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(Monotonic.Ms / 320.0));
        }
        else _ring.Opacity = 1;

        UpdateArcPath();
        RenderBadge();
        RenderText();
    }

    void RenderBadge()
    {
        if (!_showBadge || _badgeState == BadgeState.None)
        {
            _badge.Visibility = Visibility.Collapsed;
            return;
        }
        _badge.Visibility = Visibility.Visible;
        _badge.Background = new SolidColorBrush(_badgeState switch
        {
            BadgeState.PendingFile => Color.FromRgb(0xFF, 0xD4, 0x00),
            BadgeState.ReadyFile => Color.FromRgb(0x2E, 0xCC, 0x71),
            BadgeState.Synced => Color.FromRgb(0x2E, 0x8B, 0xFF),
            _ => Colors.Transparent,
        });
        _badgeText.Text = "";
        _badge.Width = double.NaN;
        _badge.MinWidth = 14;
    }

    void RenderText()
    {
        bool show = _showText && (!string.IsNullOrEmpty(_text) || !string.IsNullOrEmpty(_detail.Text));
        if (!show)
        {
            if (_pill.Visibility == Visibility.Visible)
            {
                _pill.Visibility = Visibility.Collapsed;
                ResizeToContent();
            }
            return;
        }
        _title.Visibility = string.IsNullOrEmpty(_text) ? Visibility.Collapsed : Visibility.Visible;
        _detail.Visibility = string.IsNullOrEmpty(_detail.Text) ? Visibility.Collapsed : Visibility.Visible;
        _pill.Visibility = Visibility.Visible;
        ResizeToContent();
    }

    /// <summary>把气泡换到光圈的另一侧。</summary>
    void ApplyPillSide()
    {
        if (_pillOnLeft)
        {
            Grid.SetColumn(_pill, 0);
            _pill.Margin = new Thickness(0, 0, PillGap, 0);
        }
        else
        {
            Grid.SetColumn(_pill, 2);
            _pill.Margin = new Thickness(PillGap, 0, 0, 0);
        }
        ResizeToContent();
        FollowCursor();
    }

    /// <summary>窗口宽度跟随文字气泡（光环始终水平居中，因此仍精确套在光标上）。</summary>
    void ResizeToContent()
    {
        // 不要手动 Measure 再赋 Width——WPF 会因可用尺寸相同而命中测量缓存，
        // 读到上一帧的陈旧 DesiredSize，导致文字被省略号截断。
        // 交给 SizeToContent.Width，由布局系统按内容自然宽度定宽（光环仍水平居中＝仍套在光标上）。
        // 这里立刻跑一遍布局，让 FollowCursor 读到的 ActualWidth 是这一帧的。
        if (IsLoaded) UpdateLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
