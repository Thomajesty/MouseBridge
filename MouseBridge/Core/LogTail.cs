using System.IO;
using System.Text;

namespace MouseBridge.Core;

/// <summary>
/// 运行日志的增量读取器：只读取新增的字节，保留最近若干行，
/// 供“传输记录/日志”窗口实时显示，避免每秒整文件重读。
/// </summary>
public class LogTail
{
    readonly object _gate = new();
    readonly Queue<string> _lines = new();
    readonly int _maxLines;

    long _pos;
    string? _file;

    public LogTail(int maxLines = 2000) => _maxLines = maxLines;

    public event Action? Changed;

    /// <summary>当前正在跟踪的日志文件。</summary>
    public string? CurrentFile { get { lock (_gate) return _file; } }

    public void Poll()
    {
        bool got = false;
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            string? newest = Newest();
            lock (_gate)
            {
                if (newest == null) return;
                if (newest != _file)
                {
                    // 跨天/新建日志：重新跟踪，并把已有内容整段读进来
                    _file = newest;
                    _pos = 0;
                    _lines.Clear();
                }
            }
            if (ReadNew()) got = true;
        }
        catch { }
        if (got) { try { Changed?.Invoke(); } catch { } }
    }

    string? Newest()
    {
        string? best = null;
        var t = DateTime.MinValue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.LogDir, "mb-*.log"))
            {
                var w = File.GetLastWriteTime(f);
                if (w > t) { t = w; best = f; }
            }
        }
        catch { }
        return best;
    }

    bool ReadNew()
    {
        string file;
        long start;
        lock (_gate)
        {
            if (_file == null) return false;
            file = _file; start = _pos;
        }

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length == start) return false;
        if (fs.Length < start) { lock (_gate) _pos = 0; return false; }

        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        int read = fs.Read(buf, 0, buf.Length);
        if (read <= 0) return false;

        // 只消费到最后一个换行为止：末尾可能正写着一半
        int cut = -1;
        for (int i = read - 1; i >= 0; i--) if (buf[i] == (byte)'\n') { cut = i; break; }
        if (cut < 0) return false;

        string text = Encoding.UTF8.GetString(buf, 0, cut);   // 不含那个换行
        lock (_gate)
        {
            _pos = start + cut + 1;
            foreach (var ln in text.Split('\n'))
            {
                var s = ln.TrimEnd('\r');
                if (s.Length == 0) continue;
                _lines.Enqueue(s);
                while (_lines.Count > _maxLines) _lines.Dequeue();
            }
        }
        return true;
    }

    /// <summary>取最近若干行，可按级别过滤（minLevel 及以上）。</summary>
    public List<string> Tail(int count = 400, LogLevel minLevel = LogLevel.Debug)
    {
        lock (_gate)
        {
            var list = new List<string>(_lines.Count);
            foreach (var l in _lines)
                if (LevelOf(l) >= minLevel) list.Add(l);
            if (list.Count > count) list.RemoveRange(0, list.Count - count);
            return list;
        }
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>从 "[12:00:00.000] [Info ] xxx" 里解出级别。</summary>
    static LogLevel LevelOf(string line)
    {
        int i = line.IndexOf('[');
        int j = i >= 0 ? line.IndexOf(']', i + 1) : -1;
        if (j > i)
        {
            var tag = line.Substring(i + 1, j - i - 1).Trim();
            return tag switch
            {
                "Debug" => LogLevel.Debug,
                "Warn" => LogLevel.Warn,
                "Error" => LogLevel.Error,
                "Info" => LogLevel.Info,
                _ => LogLevel.Info,
            };
        }
        return LogLevel.Info;
    }

    public static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };
}
