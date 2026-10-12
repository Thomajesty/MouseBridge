using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MouseBridge.Files;

namespace MouseBridge.Core;

public enum XferState { Running, Done, Failed, Canceled, Interrupted }

/// <summary>一次文件传输（收或发）的完整档案。</summary>
public class XferRecord
{
    public long Id { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>true=本机发出；false=本机接收。</summary>
    public bool Send { get; set; }
    public string Peer { get; set; } = "";
    /// <summary>形如“发送 3 个文件”/“接收 3 个文件”。</summary>
    public string Label { get; set; } = "";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    public XferState State { get; set; } = XferState.Running;
    /// <summary>落盘目录（接收）或来源说明（发送）。</summary>
    public string Target { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>本次传输是拖拽投递还是剪贴板同步。</summary>
    public string Via { get; set; } = "";

    [JsonIgnore] public double Progress => TotalBytes > 0 ? MathX.Clamp(DoneBytes / (double)TotalBytes, 0, 1) : 0;
    [JsonIgnore] public bool IsRunning => State == XferState.Running;

    [JsonIgnore]
    public TimeSpan Elapsed => (End == default ? DateTime.Now : End) - Start;

    // —— 供界面直接绑定 ——
    [JsonIgnore] public string DirText => Send ? "发送" : "接收";
    [JsonIgnore] public string StateText => TransferJournal.StateText(State);
    [JsonIgnore] public string WhenText => Start.ToString("MM-dd HH:mm:ss");
    [JsonIgnore] public string SizeText => FileScanner.Human(TotalBytes);
    [JsonIgnore] public string CountText => $"{FileCount} 个";
    [JsonIgnore] public string PeerText => string.IsNullOrEmpty(Peer) ? "—" : Peer;
    [JsonIgnore] public string TargetText =>
        !string.IsNullOrEmpty(Target) ? Target : (!string.IsNullOrEmpty(Note) ? Note : "—");

    [JsonIgnore]
    public string ElapsedText
    {
        get
        {
            var e = Elapsed;
            if (State == XferState.Running) return "进行中";
            if (e.TotalSeconds < 1) return "<1 秒";
            if (e.TotalSeconds > 86400) return ">1 天";
            return e.TotalHours >= 1 ? e.ToString(@"hh\:mm\:ss") : e.ToString(@"mm\:ss");
        }
    }

    [JsonIgnore]
    public string SpeedText
    {
        get
        {
            double sec = Elapsed.TotalSeconds;
            if (sec < 0.4 || DoneBytes <= 0) return "—";
            return $"{FileScanner.Human((long)(DoneBytes / sec))}/s";
        }
    }

    [JsonIgnore]
    public string EtaText
    {
        get
        {
            if (State != XferState.Running) return "—";
            double sec = Elapsed.TotalSeconds;
            if (sec < 0.4 || DoneBytes <= 0 || TotalBytes <= DoneBytes) return "—";
            double rate = DoneBytes / sec;
            double left = (TotalBytes - DoneBytes) / rate;
            if (left > 86400) return ">1 天";
            return TimeSpan.FromSeconds(left).ToString(left >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
        }
    }
}

/// <summary>
/// 传输台账：内存里保存最近若干次收/发记录，同时以 JSONL 追加落盘
/// （%APPDATA%\MouseBridge\transfers.jsonl），重启后仍可回看。
///
/// 落盘策略：开始写一条 Running 行、结束再写一条同 Id 的行；
/// 读回时后写的覆盖先写的，仍处于 Running 的说明上次是被强杀，标成“已中断”。
/// </summary>
public static class TransferJournal
{
    static readonly object Gate = new();
    static readonly List<XferRecord> _records = new();
    static readonly Dictionary<long, XferRecord> _live = new();
    static string? _file;

    public const int KeepInMemory = 300;
    const int CompactAtLines = 1200;
    const int CompactKeepLines = 600;

    public static event Action? Changed;

    static TransferJournal()
    {
        _file = Path.Combine(AppPaths.Root, "transfers.jsonl");
        Load();
    }

    public static string FilePath => _file ?? "";

    // ================= 写入 =================

    public static XferRecord Begin(long id, bool send, double total, int fileCount, string label, string peer, string via)
    {
        var rec = new XferRecord
        {
            Id = id,
            Start = DateTime.Now,
            Send = send,
            Peer = peer,
            Label = label,
            FileCount = fileCount,
            TotalBytes = (long)total,
            Via = via,
            State = XferState.Running,
        };
        lock (Gate)
        {
            _live[id] = rec;
            _records.Insert(0, rec);
            Trim();
        }
        Persist(rec);
        Raise();
        return rec;
    }

    public static void Progress(long id, long done, long total)
    {
        bool changed = false;
        lock (Gate)
        {
            if (_live.TryGetValue(id, out var r))
            {
                r.DoneBytes = done;
                if (total > 0) r.TotalBytes = total;
                changed = true;
            }
        }
        if (changed) Raise();
    }

    public static void Finish(long id, XferState state, string? target = null, string? note = null)
    {
        XferRecord? rec;
        lock (Gate)
        {
            _live.TryGetValue(id, out rec);
            _live.Remove(id);
            if (rec == null) return;
            rec.End = DateTime.Now;
            rec.State = state;
            if (target != null) rec.Target = target;
            if (note != null) rec.Note = note;
            if (state == XferState.Done && rec.DoneBytes == 0) rec.DoneBytes = rec.TotalBytes;
            else if (rec.DoneBytes > rec.TotalBytes) rec.DoneBytes = rec.TotalBytes;
        }
        Persist(rec);
        Raise();
    }

    /// <summary>目标目录稍后才知道（先把文件落到默认目录、再移动）时的补写。</summary>
    public static void SetTarget(long id, string target)
    {
        lock (Gate)
        {
            var r = _records.FirstOrDefault(x => x.Id == id);
            if (r == null) return;
            r.Target = target;
        }
        Raise();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _records.Clear();
            _live.Clear();
        }
        try { if (_file != null && File.Exists(_file)) File.Delete(_file); } catch { }
        Raise();
    }

    /// <summary>删掉单条记录（自检清场、误记的记录都能用）。</summary>
    public static void Remove(long id)
    {
        lock (Gate)
        {
            _live.Remove(id);
            _records.RemoveAll(r => r.Id == id);
        }
        RewriteFile();
        Raise();
    }

    static void RewriteFile()
    {
        try
        {
            if (_file == null) return;
            List<XferRecord> snap;
            lock (Gate) snap = new List<XferRecord>(_records);
            var sb = new StringBuilder();
            foreach (var r in snap) sb.AppendLine(JsonSerializer.Serialize(r, Opt));
            File.WriteAllText(_file, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Log.Warn($"重写传输台账失败：{ex.Message}"); }
    }

    public static List<XferRecord> Snapshot()
    {
        lock (Gate) return new List<XferRecord>(_records);
    }

    public static XferRecord? Active
    {
        get { lock (Gate) return _records.FirstOrDefault(r => r.IsRunning); }
    }

    /// <summary>把当前台账导出为 CSV（默认放日志目录）。</summary>
    public static string Export()
    {
        var list = Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine("开始时间,方向,对端,文件数,总大小(字节),已传(字节),耗时(秒),结果,方式,目标,备注");
        foreach (var r in list.OrderByDescending(x => x.Start))
        {
            sb.Append(r.Start.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
              .Append(r.Send ? "发送" : "接收").Append(',')
              .Append(Quote(r.Peer)).Append(',')
              .Append(r.FileCount).Append(',')
              .Append(r.TotalBytes).Append(',')
              .Append(r.DoneBytes).Append(',')
              .Append(r.Elapsed.TotalSeconds.ToString("F1")).Append(',')
              .Append(StateText(r.State)).Append(',')
              .Append(Quote(r.Via)).Append(',')
              .Append(Quote(r.Target)).Append(',')
              .Append(Quote(r.Note))
              .AppendLine();
        }
        Directory.CreateDirectory(AppPaths.LogDir);
        var path = Path.Combine(AppPaths.LogDir, $"transfers-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // 带 BOM，Excel 不乱码
        return path;
    }

    static string Quote(string s) => Str.Has(s, ',') || Str.Has(s, '"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string StateText(XferState s) => s switch
    {
        XferState.Running => "进行中",
        XferState.Done => "成功",
        XferState.Failed => "失败",
        XferState.Canceled => "已取消",
        _ => "已中断",
    };

    // ================= 落盘 =================

    static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static void Persist(XferRecord rec)
    {
        try
        {
            if (_file == null) return;
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(_file, JsonSerializer.Serialize(rec, Opt) + Environment.NewLine, Encoding.UTF8);
            Compact();
        }
        catch (Exception ex) { Log.Warn($"写传输台账失败：{ex.Message}"); }
    }

    static void Compact()
    {
        try
        {
            if (_file == null || !File.Exists(_file)) return;
            var lines = File.ReadAllLines(_file);
            if (lines.Length <= CompactAtLines) return;
            var keep = lines.Skip(lines.Length - CompactKeepLines);
            File.WriteAllLines(_file, keep, Encoding.UTF8);
        }
        catch { }
    }

    static void Load()
    {
        try
        {
            if (_file == null || !File.Exists(_file)) return;
            var byId = new Dictionary<long, XferRecord>();
            foreach (var line in File.ReadLines(_file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                XferRecord? r = null;
                try { r = JsonSerializer.Deserialize<XferRecord>(line, Opt); } catch { }
                if (r == null) continue;
                byId[r.Id] = r;   // 后写的同 Id 行覆盖
            }
            foreach (var r in byId.Values.OrderByDescending(x => x.Start))
            {
                if (r.State == XferState.Running)
                {
                    // 上次进程没跑完就退出了
                    r.State = XferState.Interrupted;
                    r.End = r.End == default ? r.Start : r.End;
                }
                _records.Add(r);
            }
            Trim();
        }
        catch (Exception ex) { Log.Warn($"读传输台账失败：{ex.Message}"); }
    }

    static void Trim()
    {
        if (_records.Count > KeepInMemory) _records.RemoveRange(KeepInMemory, _records.Count - KeepInMemory);
    }

    static void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
