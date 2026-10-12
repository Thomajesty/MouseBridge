using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace MouseBridge.Core;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// 全局日志。
///
/// **关键约束**：本类会被"全局低级钩子回调"直接调用（例如 Orchestrator.OnHookMouseMove
/// 里那句"本机鼠标移动 → 收回控制权"）。Windows 对 WH_MOUSE_LL / WH_KEYBOARD_LL 回调
/// 有硬性时间预算（超过约 300ms 就会被系统摘掉钩子，而且**不会自动重装**，表现是
/// 键鼠突然全失灵）。所以写入路径绝对不能"同步磁盘 IO + 全局锁"——磁盘偶发卡顿
/// （杀软扫描、机械盘寻道）就能把回调拖爆。
///
/// 因此改成：调用方只管把已格式化的行塞进无界队列（纳秒级、无锁等待），
/// 由一条后台线程负责真正落盘。进程退出前调 <see cref="Flush"/> 把尾巴写完。
/// 队列无界是刻意的——丢日志比丢钩子好；内存占用由日志本身长度天然限制。
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    static LogLevel _level = LogLevel.Info;
    static string? _file;

    // 无界队列 + 单写线程：写入侧永不阻塞、永不抛异常（钩子回调里必须如此）。
    static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    static readonly Thread Writer;
    static volatile bool _stopping;

    static Log()
    {
        Writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "mb-log",
            Priority = ThreadPriority.BelowNormal,   // 别跟键鼠处理抢 CPU
        };
        Writer.Start();
    }

    public static void Init(LogLevel level)
    {
        _level = level;
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            _file = Path.Combine(AppPaths.LogDir, $"mb-{DateTime.Now:yyyyMMdd}.log");
            Info("==== MouseBridge 启动 ====");
        }
        catch { _file = null; }
    }

    /// <summary>保证日志文件可用（启动早期崩了也要能记下来，但不重复打启动横幅）。</summary>
    public static void EnsureFile()
    {
        if (_file != null) return;
        try
        {
            _level = LogLevel.Debug;
            Directory.CreateDirectory(AppPaths.LogDir);
            _file = Path.Combine(AppPaths.LogDir, $"mb-{DateTime.Now:yyyyMMdd}.log");
        }
        catch { _file = null; }
    }

    public static void Debug(string m) => W(LogLevel.Debug, m);
    public static void Info(string m) => W(LogLevel.Info, m);
    public static void Warn(string m) => W(LogLevel.Warn, m);
    public static void Error(string m) => W(LogLevel.Error, m);
    public static void Error(string m, Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(m).Append(": ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            sb.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
        if (!string.IsNullOrEmpty(ex.StackTrace))
            sb.Append(Environment.NewLine).Append(ex.StackTrace);
        W(LogLevel.Error, sb.ToString());
    }

    static void W(LogLevel lv, string m)
    {
        if (lv < _level) return;
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{lv,-5}] {m}";
        // 注意：这里**只有入队**，没有 lock、没有 File.*、没有格式化到磁盘的路径。
        // 全部 try/catch 兜住，保证在钩子回调里调用绝对不会往外抛。
        try
        {
            System.Diagnostics.Debug.WriteLine(line);
            if (_file == null) return;
            if (_stopping) return;
            Queue.Add(line);
        }
        catch { }
    }

    static void WriteLoop()
    {
        string? currentFile = null;
        StreamWriter? w = null;
        try
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    // 跨天时换文件（原实现是每次 AppendAllText 自己算路径，这里也保留换天逻辑）
                    var want = _file;
                    if (want == null) continue;
                    if (!string.Equals(want, currentFile, StringComparison.Ordinal))
                    {
                        w?.Flush();
                        w?.Dispose();
                        w = new StreamWriter(new FileStream(want, FileMode.Append, FileAccess.Write,
                                                            FileShare.ReadWrite))
                        { AutoFlush = true };
                        currentFile = want;
                    }
                    w!.WriteLine(line);
                }
                catch
                {
                    // 落盘失败（磁盘满、文件被独占）不能把写线程带走，丢弃这一条继续。
                    try { w?.Dispose(); } catch { }
                    w = null;
                    currentFile = null;
                    Thread.Sleep(200);   // 稍作退避，避免失败时疯狂重试
                }
            }
        }
        catch { }
        finally
        {
            try { w?.Flush(); w?.Dispose(); } catch { }
        }
    }

    /// <summary>把队列里剩下的日志写盘。进程退出前调用，最多等 timeoutMs 毫秒。</summary>
    public static void Flush(int timeoutMs = 1500)
    {
        try
        {
            _stopping = true;
            Queue.CompleteAdding();
            Writer.Join(timeoutMs);
        }
        catch { }
    }
}
