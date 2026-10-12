using System.IO;

namespace MouseBridge.Files;

/// <summary>文件/文件夹清单扫描：剪贴板清单、投递清单、发送列表共用同一套规则，避免两端算出来的总量不一致。</summary>
public static class FileScanner
{
    /// <summary>清单里的一项。Rel=相对路径（用 / 分隔），Size=字节数，IsDir=是否目录。</summary>
    public class Item
    {
        public string Root = "";          // 磁盘上的绝对路径（发送端用）
        public string Rel = "";           // 相对路径（用 / 分隔，接收端据此还原结构）
        public bool IsDir;
        public long Size;
        public int RootIndex = -1;        // 属于第几个用户选中的根条目
    }

    const int MaxItems = 20000;

    /// <summary>
    /// 展开根路径列表为可发送的条目列表。
    /// 目录会递归展开（保留层级），目录本身也会作为一条 IsDir 条目。
    /// </summary>
    public static (List<Item> items, long total, int fileCount) Expand(IEnumerable<string> roots, int budget = MaxItems)
    {
        var items = new List<Item>();
        long total = 0;
        int files = 0;
        int rootIdx = -1;
        foreach (var root in roots)
        {
            rootIdx++;
            if (items.Count >= budget) break;
            try
            {
                if (File.Exists(root))
                {
                    var fi = new FileInfo(root);
                    items.Add(new Item { Root = root, Rel = fi.Name, Size = fi.Length, RootIndex = rootIdx });
                    total += fi.Length; files++;
                }
                else if (Directory.Exists(root))
                {
                    var di = new DirectoryInfo(root);
                    items.Add(new Item { Root = root, Rel = di.Name, IsDir = true, RootIndex = rootIdx });
                    WalkDir(root, di.Name, items, ref total, ref files, budget, rootIdx);
                }
            }
            catch (Exception ex) { Log.Debug($"扫描 {root} 失败: {ex.Message}"); }
        }
        return (items, total, files);
    }

    static void WalkDir(string dir, string relPrefix, List<Item> items, ref long total, ref int files, int budget, int rootIdx)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (items.Count >= budget) return;
                try
                {
                    var fi = new FileInfo(f);
                    items.Add(new Item { Root = f, Rel = relPrefix + "/" + fi.Name, Size = fi.Length, RootIndex = rootIdx });
                    total += fi.Length; files++;
                }
                catch { }
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (items.Count >= budget) return;
                var name = Path.GetFileName(d);
                items.Add(new Item { Root = d, Rel = relPrefix + "/" + name, IsDir = true, RootIndex = rootIdx });
                WalkDir(d, relPrefix + "/" + name, items, ref total, ref files, budget, rootIdx);
            }
        }
        catch (Exception ex) { Log.Debug($"遍历 {dir} 失败: {ex.Message}"); }
    }

    /// <summary>转成协议用的清单条目（只带名字与大小，便于对端显示）。</summary>
    public static List<Net.FEntry> ToEntries(List<Item> items, int maxTop = 200)
    {
        var list = new List<Net.FEntry>();
        foreach (var it in items)
        {
            if (Str.Has(it.Rel, '/')) continue;      // 只报顶层，避免清单过长
            list.Add(new Net.FEntry { N = it.Rel + (it.IsDir ? "/" : ""), S = it.Size });
            if (list.Count >= maxTop) break;
        }
        if (list.Count == 0 && items.Count > 0)
        {
            // 全部落在子目录里（理论上不会发生，防御性兜底）
            foreach (var it in items.Take(maxTop))
                list.Add(new Net.FEntry { N = it.Rel, S = it.Size });
        }
        return list;
    }

    /// <summary>把相对路径安全地拼到目标目录下，拒绝越权跳转。</summary>
    public static string SafeJoin(string baseDir, string rel)
    {
        var parts = rel.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                       .Where(p => p != "." && p != "..")
                       .Select(Sanitize)
                       .Where(p => p.Length > 0)
                       .ToArray();
        if (parts.Length == 0) parts = new[] { "file" };
        var full = Path.Combine(new[] { baseDir }.Concat(parts).ToArray());
        return full;
    }

    public static string Sanitize(string name)
    {
        var cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        cleaned = cleaned.Trim();
        return EscapeReserved(cleaned);
    }

    /// <summary>
    /// Windows 保留设备名（CON/PRN/AUX/NUL/COM1-9/LPT1-9，含带扩展名的写法如 "NUL.txt"）。
    /// 这类名字在**任何目录**下都被解释成设备，创建文件会失败（或被重定向到设备），
    /// 对端传一个叫 "NUL" 的文件过来，落地时就是一场静默失败。统一在前面加下划线躲开。
    /// </summary>
    public static string EscapeReserved(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        if (!IsReservedStem(stem)) return name;
        // 保留名对大小写不敏感（"aux" 同样触发），前缀下划线即可躲开解释
        return "_" + name;
    }

    static bool IsReservedStem(string stem)
    {
        switch (stem.ToUpperInvariant())
        {
            case "CON": case "PRN": case "AUX": case "NUL": case "CLOCK$":
                return true;
        }
        if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")))
        {
            char d = stem[3];
            return d >= '1' && d <= '9';
        }
        return false;
    }

    public static string Human(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("F1") + " MB";
        return (bytes / 1073741824.0).ToString("F2") + " GB";
    }
}
