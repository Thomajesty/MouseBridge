using System.IO;
using System.Text;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MouseBridge.Clipboard;

public enum ClipKind { Files, Text, Image }

public class ClipContent
{
    public ClipKind Kind;
    public string Text = "";
    public byte[] Png = Array.Empty<byte>();
    public string[] Files = Array.Empty<string>();

    public static ClipContent OfText(string s) => new() { Kind = ClipKind.Text, Text = s };
    public static ClipContent OfImage(byte[] png) => new() { Kind = ClipKind.Image, Png = png };
    public static ClipContent OfFiles(string[] f) => new() { Kind = ClipKind.Files, Files = f };
}

/// <summary>
/// 本机剪贴板监听与读写（在 WPF UI 线程上操作，STA）。
/// 自己写入的内容通过签名比对抑制回声，避免两台机器互相转发死循环。
/// </summary>
public class ClipboardService : IDisposable
{
    HwndSource? _hs;
    string _lastSig = "";
    long _lastWriteTick;

    /// <summary>本程序刚写完剪贴板的静默截止时刻：这之前忽略一切剪贴板更新事件。
    /// 回声抑制依赖"内容签名一致"，但图片一旦走 Bitmap→重新编码的路，PNG 字节就会变、
    /// 签名对不上 —— 静默窗是断掉乒乓回环的第二道保险（第一道见 Read() 的 PNG 原始字节）。</summary>
    long _writeSilentUntil;

    public event Action<ClipContent>? Changed;

    public void Start()
    {
        var p = new HwndSourceParameters("MouseBridge.Clipboard") { Width = 0, Height = 0, WindowStyle = 0, PositionX = -32000, PositionY = -32000 };
        _hs = new HwndSource(p);
        _hs.AddHook(WndProc);
        Native.AddClipboardFormatListener(_hs.Handle);
        Log.Info("剪贴板监听已启动");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == Native.WM_CLIPBOARDUPDATE)
        {
            handled = true;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(ProcessUpdate);
        }
        else if ((uint)msg == Native.WM_DISPLAYCHANGE)
        {
            DisplayChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    public event Action? DisplayChanged;

    void ProcessUpdate()
    {
        if (Monotonic.Ms < _writeSilentUntil)
        {
            Log.Debug("剪贴板更新处于写后静默窗内（忽略）");
            return;
        }
        var c = Read();
        if (c == null) return;
        var sig = Signature(c);
        if (sig == _lastSig && Monotonic.Ms - _lastWriteTick < 2500)
        {
            Log.Debug("剪贴板更新为自身写入的内容（回声抑制）");
            return;
        }
        Log.Debug($"剪贴板变化: {c.Kind}" + (c.Kind == ClipKind.Files ? $" x{c.Files.Length}" : ""));
        Changed?.Invoke(c);
    }

    public static string Signature(ClipContent c) => c.Kind switch
    {
        ClipKind.Text => "T:" + Sha1(Encoding.UTF8.GetBytes(c.Text)),
        ClipKind.Image => "I:" + Sha1(c.Png),
        _ => "F:" + Sha1(Encoding.UTF8.GetBytes(string.Join(";", c.Files))),
    };

    /// <summary>
    /// 发送侧登记：这份内容刚推给对端，把它当"自己写入的"做回声抑制。
    ///
    /// 为什么必须：本机推给对端后，对端会把它写进对端剪贴板 —— 那次写入的系统广播
    /// 不会回到本机，但**本机随后把对端内容写进来**时（回声，见 Write），或对端再写回时，
    /// 本机的剪贴板监听都会看到"内容又变了"。若发送侧不登记签名，本机会把这份回声
    /// 当成"用户新复制"再推回去 → 两台机器互相重发同一份内容，无限乒乓
    /// （实测 6 毫秒一轮，UI 线程全耗在写剪贴板上，整个程序像卡死）。
    /// </summary>
    public void MarkSent(ClipContent c)
    {
        _lastSig = Signature(c);
        _lastWriteTick = Monotonic.Ms;
    }

    static string Sha1(byte[] b)
    {
        var h = Hashing.Sha1(b);
        return Hex.Of(h);
    }

    /// <summary>读取当前剪贴板内容（优先级：文件 > 文本 > 图片）。失败重试。</summary>
    public static ClipContent? Read()
    {
        IDataObject? d = null;
        for (int i = 0; i < 10; i++)
        {
            try { d = System.Windows.Clipboard.GetDataObject(); break; }
            catch (COMException) { Thread.Sleep(30); }
            catch (InvalidOperationException) { Thread.Sleep(30); }
        }
        if (d == null) return null;
        try
        {
            if (d.GetDataPresent(DataFormats.FileDrop, true) && d.GetData(DataFormats.FileDrop, true) is string[] files && files.Length > 0)
                return ClipContent.OfFiles(files.Where(File.Exists).ToArray());
            if (d.GetDataPresent(DataFormats.UnicodeText, true) && d.GetData(DataFormats.UnicodeText, true) is string s && s.Length > 0)
                return ClipContent.OfText(s);
            // 图片优先取写入方放进来的 PNG 原始字节（Write 时会带一份 "PNG" 自定义格式）。
            // 必须这么做：走 Bitmap→PngBitmapEncoder 重新编码的话，字节和发来的那份**不一样**，
            // 回声抑制的签名对不上 → 两台机器互相"收到→写入→当成新复制→再发回去"无限乒乓
            // （实测 6 毫秒一轮，UI 线程全耗在写剪贴板上，整个程序像卡死）。
            // 其他程序（截图工具/浏览器）放进来的 "PNG" 也是原始字节，取它没有副作用。
            if (ExtractRawPng(d) is { Length: > 0 } png)
                return ClipContent.OfImage(png);
            if (d.GetDataPresent(DataFormats.Bitmap, true) && d.GetData(DataFormats.Bitmap, true) is BitmapSource bmp)
            {
                using var ms = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                enc.Save(ms);
                return ClipContent.OfImage(ms.ToArray());
            }
        }
        catch (COMException) { return null; }
        catch (Exception ex) { Log.Debug("读取剪贴板失败: " + ex.Message); return null; }
        return null;
    }

    /// <summary>从剪贴板数据对象里取 PNG 原始字节（写入方用 "PNG" 自定义格式附带）；没有则 null。</summary>
    static byte[]? ExtractRawPng(IDataObject d)
    {
        try
        {
            if (!d.GetDataPresent("PNG", false)) return null;
            return d.GetData("PNG", false) switch
            {
                byte[] b => b,
                MemoryStream m => m.ToArray(),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>写入剪贴板并记录签名（用于回声抑制）。</summary>
    public void Write(ClipContent c)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                DataObject d = new();
                switch (c.Kind)
                {
                    case ClipKind.Text:
                        d.SetData(DataFormats.UnicodeText, c.Text);
                        break;
                    case ClipKind.Image:
                        using (var ms = new MemoryStream(c.Png))
                        {
                            var dec = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                            d.SetImage(dec.Frames[0]);
                        }
                        // 部分应用优先识别 PNG 原始数据
                        d.SetData("PNG", new MemoryStream(c.Png));
                        break;
                    case ClipKind.Files:
                        var sc = new StringCollection();
                        sc.AddRange(c.Files);
                        d.SetFileDropList(sc);
                        break;
                }
                System.Windows.Clipboard.SetDataObject(d, true);
                _lastSig = Signature(c);
                _lastWriteTick = Monotonic.Ms;
                _writeSilentUntil = Monotonic.Ms + 600;   // 写后静默窗（断乒乓第二道保险）
                return;
            }
            catch (COMException) { Thread.Sleep(30); }
            catch (InvalidOperationException) { Thread.Sleep(30); }
            catch (Exception ex) { Log.Warn("写剪贴板失败: " + ex.Message); return; }
        }
        Log.Warn("写剪贴板重试失败（被其他程序占用）");
    }

    public void Dispose()
    {
        try
        {
            if (_hs != null) Native.RemoveClipboardFormatListener(_hs.Handle);
        }
        catch { }
    }
}
