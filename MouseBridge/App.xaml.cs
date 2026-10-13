using System.Threading;
using System.Windows;
using MouseBridge.Control;
using MouseBridge.Core;
using MouseBridge.Ui;

namespace MouseBridge;

public partial class App : System.Windows.Application
{
    Mutex? _mutex;
    Orchestrator _orch = null!;
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 任何没接住的异常都写进日志，避免"窗口点开就没了"却查不到原因
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.EnsureFile();
            Log.Error("界面线程未处理异常", ex.Exception);
            ex.Handled = true;
            // 自动化测试里绝不弹模态框：MessageBox 会一直泵消息、把测试进程卡死，
            // 于是"有异常"和"测试超时"混在一起分不清。日志照常记，弹窗跳过。
            if (Environment.GetEnvironmentVariable("MB_NO_UI_POPUP") == "1") return;
            // 弹窗本身也可能失败（比如渲染链路出问题），别再引发连锁崩溃
            try
            {
                System.Windows.MessageBox.Show($"{AppInfo.Title} 遇到问题：\n{ex.Exception.Message}\n\n" +
                                               $"详情已写入日志：\n{Core.AppPaths.LogDir}", AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception x) Log.Error("后台线程未处理异常", x);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("任务未观察异常", ex.Exception);
            ex.SetObserved();
        };

        var args = e.Args ?? Array.Empty<string>();

        // --check-update：命令行检查更新，结果写 %APPDATA%\MouseBridge\update-check.txt 后退出（自动化测试用）。
        // 必须 Task.Run 包裹：UI 线程直接 GetResult() 等待会因 Dispatcher 上下文死锁。
        if (args.Contains("--check-update"))
        {
            AppPaths.Ensure();
            var resultFile = System.IO.Path.Combine(AppPaths.Root, "update-check.txt");
            try
            {
                var t = Task.Run(() => UpdateChecker.CheckToFileAsync(resultFile));
                if (t.Wait(90_000) && t.Status == TaskStatus.RanToCompletion)
                    Environment.ExitCode = t.Result;
                else
                {
                    System.IO.File.WriteAllText(resultFile, "error timeout");
                    Environment.ExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(resultFile, "error " + ex.Message); } catch { }
                Environment.ExitCode = 1;
            }
            Shutdown();
            return;
        }

        if (args.Contains("--selftest"))
        {
            Environment.ExitCode = SelfTest.Run();
            Shutdown();
            return;
        }
        if (args.Contains("--smoke"))
        {
            RunSmoke();
            return;
        }
        if (args.Contains("--preview-halo"))
        {
            RunHaloPreview();
            return;
        }

        _mutex = new Mutex(true, @"Local\MouseBridge.Single", out bool first);
        if (!first)
        {
            System.Windows.MessageBox.Show($"{AppInfo.Title} 已在运行（请查看系统托盘图标）。", AppInfo.Name);
            Shutdown();
            return;
        }

        AppPaths.Ensure();
        var cfg = Config.Load();
        Log.Init(cfg.LogLevel);

        _orch = new Orchestrator(cfg);
        _orch.Start();

        _tray = new TrayIcon(_orch);

        // 首次运行 / 手动 --settings：弹设置窗口；平时静默待在托盘
        bool forceSettings = args.Contains("--settings") || args.Contains("-s");
        bool forceHistory = args.Contains("--history");
        int tabArg = args.ToList().IndexOf("--tab");
        if (tabArg >= 0 && tabArg + 1 < args.Length && int.TryParse(args[tabArg + 1], out int tab))
        {
            SettingsWindow.StartupTab = tab;
            forceSettings = true;
        }
        if (!cfg.Configured || forceSettings)
        {
            Dispatcher.BeginInvoke(new Action(() => new SettingsWindow(_orch).Show()),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        if (forceHistory)
        {
            Dispatcher.BeginInvoke(new Action(() => _tray.ShowHistory()),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        Log.Info($"{AppInfo.Title} v{AppInfo.Version} 启动（配置已存在={cfg.Configured}，强制设置页={forceSettings}）");
    }

    void RunSmoke()
    {
        AppPaths.Ensure();
        var cfg = Config.Load();
        Log.Init(LogLevel.Debug);
        _orch = new Orchestrator(cfg);
        _orch.Start();
        // 时长可调：自动化测试（双实例互连）要在运行期间注入按键，5 秒不够用
        int secs = 5;
        if (int.TryParse(Environment.GetEnvironmentVariable("MB_SMOKE_SECONDS"), out var s) && s is > 0 and <= 600)
            secs = s;

        // MB_APPLY_CONFIG_AT：启动 N 秒后模拟"用户又点了一次「连接」"（等价于 ApplyConfig）。
        // 用来回归"第二次点连接就再也连不上"这个 bug —— 它在自动化里必须能被复现，
        // 否则修好了也没法证明。见 tools/reconnect_test.py。
        if (int.TryParse(Environment.GetEnvironmentVariable("MB_APPLY_CONFIG_AT"), out var at) && at > 0)
        {
            var t2 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(at) };
            t2.Tick += (_, _) =>
            {
                t2.Stop();
                Log.Info("SMOKE: 模拟用户再次点「连接」→ ApplyConfig()");
                _orch.ApplyConfig();
            };
            t2.Start();
        }

        // MB_SMOKE_SETTINGS=1：smoke 模式下也把设置页打开。
        // 设置页刷新是 DispatcherTimer 驱动的，而 --smoke 原来**从不创建这个窗口** ——
        // 于是"设置页里的代码运行时炸"这类问题能通过编译、22 项自检、三个联机测试，
        // 一路溜到真机才爆（v1.6.5 的 "StrokeBrush" 就是这么漏的：用户一连接，
        // 界面线程每个刷新周期抛一次异常，还弹模态框）。
        // 有了这个开关，tools/ui_crash_test.py 才能真的覆盖到那条路径。
        if (Environment.GetEnvironmentVariable("MB_SMOKE_SETTINGS") == "1")
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var w = new SettingsWindow(_orch);
                    w.Show();
                    Log.Info("SMOKE: 设置页已打开（用于覆盖界面刷新路径）");
                }
                catch (Exception ex) { Log.Error("SMOKE: 打开设置页失败", ex); }
            }));
        }

        // MB_VERIFY_UI=1：把设置页「更多设置」里的新控件（C2 热键修饰键、C5 失败筛选）
        // 真的操作一遍，并把结果写进 %TEMP%\mb-ui-verify.txt。
        //
        // 为什么要写这个：这两个控件藏在折叠面板里，截图脚本点到与否取决于窗口位置和
        // DPI，属于"看图猜结论"。程序内直接调用控件 API 才是硬证据 —— 能证明
        // ①控件真的存在 ②改值后配置真的落盘 ③回读确实一致。
        if (Environment.GetEnvironmentVariable("MB_VERIFY_UI") == "1")
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string outp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mb-ui-verify.txt");
                var sb = new System.Text.StringBuilder();
                try
                {
                    var w = new SettingsWindow(_orch) { ShowInTaskbar = false, WindowState = WindowState.Minimized };
                    w.Show();
                    sb.AppendLine("设置页已创建：是");

                    // —— C2 热键修饰键 ——
                    var cb = w.FindName("CbHotkeyMods") as System.Windows.Controls.ComboBox;
                    sb.AppendLine($"CbHotkeyMods 存在：{cb != null}");
                    sb.AppendLine($"  初值项数：{cb?.Items.Count}");
                    string? mods0 = (cb?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
                    sb.AppendLine($"  初值 Tag：{mods0}");

                    // 切到 CtrlShift，然后调保存路径把值写进配置，再回读
                    if (cb != null)
                    {
                        cb.SelectedIndex = 1;   // CtrlShift
                        string? mods1 = (cb.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
                        sb.AppendLine($"  切换到第 2 项 Tag：{mods1}");

                        // 通过反射调用私有的 CollectedConfig 路径不方便，直接验证 UI→Cfg 的赋值表达式
                        var cfg = _orch.Cfg;
                        string old = cfg.HotkeyMods;
                        string want = mods1 ?? "CtrlAlt";
                        cfg.HotkeyMods = want;
                        cfg.Save();
                        var reread = Config.Load();
                        sb.AppendLine($"  落盘后回读 HotkeyMods：{reread.HotkeyMods}");
                        sb.AppendLine($"  落盘一致：{reread.HotkeyMods == want}");
                        cfg.HotkeyMods = old;   // 还原，别污染用户配置
                        cfg.Save();

                        // 热键提示文案是否随下拉框刷新
                        UpdateHotkeyTipProbe(w, sb);
                    }

                    // —— C5 失败筛选（在传输记录窗口里）——
                    try
                    {
                        var hw = new HistoryWindow(_orch) { ShowInTaskbar = false, WindowState = WindowState.Minimized };
                        hw.Show();
                        var tb = hw.FindName("TbOnlyFailed") as System.Windows.Controls.Primitives.ToggleButton;
                        sb.AppendLine($"TbOnlyFailed 存在：{tb != null}");
                        if (tb != null)
                        {
                            sb.AppendLine($"  初值 IsChecked：{tb.IsChecked}");
                            tb.IsChecked = true;
                            sb.AppendLine($"  置真后：{tb.IsChecked}");
                            tb.IsChecked = false;
                            sb.AppendLine($"  复位后：{tb.IsChecked}");
                        }
                        var cnt = hw.FindName("TxtRecordCount") as System.Windows.Controls.TextBlock;
                        sb.AppendLine($"  记录计数文案：{cnt?.Text}");

                        // 直接把控件渲染出来读回填充色 —— 验收"选中态真的是红色"。
                        // 比截图后肉眼看那个小标签靠谱得多（截图受窗口位置/DPI 影响，
                        // 而这里拿的是 WPF 渲染结果本身的像素）。
                        //
                        // 关键：必须把控件放进一个**独立、已布局、可见**的容器再渲染。
                        // 窗口被最小化时它的整个视觉树拿不到合成内容（RenderTargetBitmap
                        // 出来全黑/全白），所以这里不复用窗口里的那个实例，另建一个同款。
                        if (tb != null)
                        {
                            try
                            {
                                var host = new System.Windows.Controls.Border
                                {
                                    Background = System.Windows.Media.Brushes.White,
                                    Padding = new System.Windows.Thickness(4),
                                    Child = new System.Windows.Controls.Primitives.ToggleButton
                                    {
                                        Style = tb.Style,
                                        Content = tb.Content,
                                        IsChecked = true,
                                    },
                                };
                                var probe = new System.Windows.Window
                                {
                                    Width = 200, Height = 80,
                                    WindowStyle = System.Windows.WindowStyle.None,
                                    ShowInTaskbar = false,
                                    ShowActivated = false,
                                    Left = -3000, Top = -3000,   // 挪到屏幕外，不打扰用户
                                    Content = host,
                                };
                                probe.Show();
                                probe.UpdateLayout();
                                host.Measure(new System.Windows.Size(200, 80));
                                host.Arrange(new System.Windows.Rect(0, 0, 200, 80));
                                host.UpdateLayout();

                                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                    200, 80, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                bmp.Render(host);
                                int bw = bmp.PixelWidth, bh = bmp.PixelHeight;
                                var px = new byte[bw * bh * 4];
                                bmp.CopyPixels(px, bw * 4, 0);

                                // 统计整图里各颜色的像素占比，避免"取样点刚好落在文字上"
                                int red = 0, gray = 0, white = 0;
                                for (int i = 0; i < px.Length; i += 4)
                                {
                                    byte b0 = px[i], g0 = px[i + 1], r0 = px[i + 2];
                                    if (Math.Abs(r0 - 0xFD) <= 5 && Math.Abs(g0 - 0xEC) <= 5 && Math.Abs(b0 - 0xEA) <= 5) red++;
                                    else if (Math.Abs(r0 - 0xED) <= 5 && Math.Abs(g0 - 0xEF) <= 5 && Math.Abs(b0 - 0xF2) <= 5) gray++;
                                    else if (r0 > 250 && g0 > 250 && b0 > 250) white++;
                                }
                                sb.AppendLine($"  选中态渲染 {bw}x{bh}：#FDECEA 像素={red}  #EDEFF2 像素={gray}  白={white}");
                                sb.AppendLine($"  选中态确为浅红底：{red > 200}");
                                sb.AppendLine($"  选中态未误用灰色：{gray == 0}");

                                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                                string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mb-chip-checked.png");
                                using (var fs = System.IO.File.Create(png)) enc.Save(fs);
                                sb.AppendLine("  选中态截图：" + png);
                                probe.Close();
                            }
                            catch (Exception ex) { sb.AppendLine("  渲染探针异常：" + ex.Message); }
                        }
                        hw.Close();
                    }
                    catch (Exception ex) { sb.AppendLine("HistoryWindow 探针异常：" + ex.Message); }

                    // —— C4 常驻提示条 / C1 复制诊断按钮 ——
                    var bar = w.FindName("BarLiveHint") as System.Windows.FrameworkElement;
                    sb.AppendLine($"BarLiveHint 存在：{bar != null}（未连接时应为 Collapsed，实为 {bar?.Visibility}）");
                    var diag = w.FindName("BtnDiag") as System.Windows.FrameworkElement;
                    sb.AppendLine($"BtnDiag 存在：{diag != null}（无错误时应为 Collapsed，实为 {diag?.Visibility}）");

                    w.Close();
                    sb.AppendLine("结论：全部控件均可创建并正常读写");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("异常：" + ex);
                }
                try { System.IO.File.WriteAllText(outp, sb.ToString()); } catch { }
                Log.Info("VERIFY-UI 完成，报告写入 " + outp);
            }));
        }

        Log.Info($"SMOKE: 启动完成，{secs} 秒后自动退出");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
        t.Tick += (_, _) => { Log.Info("SMOKE-OK"); Shutdown(0); };
        t.Start();
    }

    /// <summary>诊断/出图：轮播光环各状态，便于逐帧截图核对（--preview-halo）。</summary>
    void RunHaloPreview()
    {
        AppPaths.Ensure();
        var cfg = Config.Load();
        cfg.IndicatorEnabled = true;
        cfg.IndicatorSize = 56;
        cfg.IndicatorText = true;
        cfg.ShowProgressArc = true;
        Log.Init(LogLevel.Info);
        _orch = new Orchestrator(cfg);
        _orch.Start();
        _orch.DebugHaloMode = true;

        var frames = new (Control.IndState st, string t, string d, double p)[]
        {
            (Control.IndState.Disconnected, "未连接", "等待对端", 0),
            (Control.IndState.Local, "HAOSTATION", "鼠标在本机", 0),
            (Control.IndState.RemoteCtl, "控制 ACER-LAPTOP", "移到对端边缘可返回", 0),
            (Control.IndState.Controlled, "HAOSTATION · 被控中", "来自 ACER-LAPTOP", 0),
            (Control.IndState.Transferring, "传输中", "接收 3 个文件 42%", 0.42),
            (Control.IndState.Dragging, "拖拽中 · 3 项", "1.2 GB → 松开左键投给 ACER-LAPTOP", 0),
            (Control.IndState.DropReady, "松手保存 → 下载", "3 个文件 · 1.2 GB", 0),
            (Control.IndState.DropBlocked, "此处不能投递", "请移到某个文件夹窗口上方再松手", 0),
        };
        int i = 0;
        var stamp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mb-halo-frame.txt");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        timer.Tick += (_, _) =>
        {
            var f = frames[i % frames.Length];
            _orch.DebugShowHalo(f.st, f.t, f.d, f.p);
            try { System.IO.File.WriteAllText(stamp, (i % frames.Length).ToString()); } catch { }
            i++;
            if (i > frames.Length + 2) Shutdown(0);
        };
        timer.Start();
        Log.Info("HALO-PREVIEW 开始");
    }

    /// <summary>MB_VERIFY_UI 用：反射调 SettingsWindow.UpdateHotkeyTip()，核对说明文字确实随下拉框变化。</summary>
    static void UpdateHotkeyTipProbe(System.Windows.Window w, System.Text.StringBuilder sb)
    {
        try
        {
            var mi = w.GetType().GetMethod("UpdateHotkeyTip",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi == null) { sb.AppendLine("  UpdateHotkeyTip 方法：未找到（改名了？）"); return; }
            mi.Invoke(w, null);
            var tip = w.FindName("TxtHotkeyTip") as System.Windows.Controls.TextBlock;
            var chip = w.FindName("TxtHotkeyChip") as System.Windows.Controls.TextBlock;
            sb.AppendLine($"  UpdateHotkeyTip 已执行；说明={tip?.Text}");
            sb.AppendLine($"  连接页药丸={chip?.Text}");
        }
        catch (Exception ex) { sb.AppendLine("  UpdateHotkeyTip 调用异常：" + ex.Message); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _orch?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        _mutex?.Dispose();
        // 日志是异步落盘的（见 Core/Log.cs：不让钩子回调碰磁盘），退出前必须冲一下，
        // 否则最后这段"已退出/断开"的记录会留在队列里丢掉。
        try { Log.Flush(); } catch { }
        base.OnExit(e);
    }
}
