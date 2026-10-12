using System.Windows;
using System.Windows.Media;

namespace MouseBridge.Ui
{
    /// <summary>
    /// 角色胶囊（主控端 / 被控端单选按钮）的颜色载体。
    ///
    /// 背景：WPF 的 ControlTemplate 是"整体替换"语义 —— 派生样式只要重写 Template，
    /// 基类模板里的所有内容就全部失效。历史版本里 RoleServerRadio / RoleClientRadio
    /// 各自复制了一份几乎相同的模板，结果"改基类不生效"，是个反复踩的坑。
    ///
    /// 现在改成：基类 RoleRadioBase 持有**唯一一份**模板，模板里的颜色不写死，
    /// 而是绑定到这三个附加属性（Fill / Stroke / FillText）；
    /// 两个派生样式只 Setter 颜色值、不再碰 Template。
    ///
    /// 这样改配色时只需改 Theme.xaml 里的画刷定义，或改派生样式的三行 Setter，
    /// 不会出现"两处模板不同步"的问题。
    /// </summary>
    public static class RoleColors
    {
        /// <summary>选中 / 悬停时的胶囊底色（方案 A′：浅底，如主控端 #EDF2FD）。</summary>
        public static readonly DependencyProperty FillProperty =
            DependencyProperty.RegisterAttached(
                "Fill", typeof(Brush), typeof(RoleColors),
                new FrameworkPropertyMetadata(null));

        public static Brush GetFill(DependencyObject d) => (Brush)d.GetValue(FillProperty);
        public static void SetFill(DependencyObject d, Brush v) => d.SetValue(FillProperty, v);

        /// <summary>选中时的描边 / 文字色（原深色，如主控端 #2454C8），保证浅底边界可辨。</summary>
        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.RegisterAttached(
                "Stroke", typeof(Brush), typeof(RoleColors),
                new FrameworkPropertyMetadata(null));

        public static Brush GetStroke(DependencyObject d) => (Brush)d.GetValue(StrokeProperty);
        public static void SetStroke(DependencyObject d, Brush v) => d.SetValue(StrokeProperty, v);

        /// <summary>胶囊上的文字色：与浅底同色系的深字（如主控端 #14418F，8.58:1）。</summary>
        public static readonly DependencyProperty FillTextProperty =
            DependencyProperty.RegisterAttached(
                "FillText", typeof(Brush), typeof(RoleColors),
                new FrameworkPropertyMetadata(null));

        public static Brush GetFillText(DependencyObject d) => (Brush)d.GetValue(FillTextProperty);
        public static void SetFillText(DependencyObject d, Brush v) => d.SetValue(FillTextProperty, v);
    }
}
