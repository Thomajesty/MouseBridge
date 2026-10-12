using System.IO;

namespace MouseBridge.Core;

/// <summary>产品信息与版本号（握手、关于页、日志统一取这里）。</summary>
public static class AppInfo
{
    public const string Name = "MouseBridge";
    public const string Version = "1.6.7";
    public const string Title = "鼠下快传 MouseBridge";

    /// <summary>关于页展示的作者信息。</summary>
    public const string Author = "Thomajesty";

    /// <summary>关于页展示的联系信息（可点击发邮件）。</summary>
    public const string Contact = "Thomajesty@163.com";

    public static string BuildStamp
    {
        get
        {
            try
            {
                var p = AppExe.Path;
                if (p != null && File.Exists(p))
                    return File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm");
            }
            catch { }
            return "-";
        }
    }
}
