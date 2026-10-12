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
