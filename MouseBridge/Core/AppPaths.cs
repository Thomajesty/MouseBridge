using System.IO;

namespace MouseBridge.Core;

public static class AppPaths
{
    /// <summary>
    /// 数据目录。设了 MB_DATA_DIR 就用它 —— 同一台机器上起两个实例做互连自测时，
    /// 两边必须各用一份配置，否则会互相覆盖。
    /// </summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("MB_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseBridge");

    public static string LogDir => Path.Combine(Root, "logs");
    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string StagingRoot => Path.Combine(Root, "staging");
    public static string CertFile => Path.Combine(Root, "cert.pfx");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(StagingRoot);
    }
}
