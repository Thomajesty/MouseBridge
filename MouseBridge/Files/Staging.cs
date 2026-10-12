using System.IO;

namespace MouseBridge.Files;

/// <summary>接收文件先落在暂存区，粘贴/投递时再就位。启动时清空上次残留。</summary>
public static class Staging
{
    static readonly object Gate = new();

    public static string NewDir(long id)
    {
        lock (Gate)
        {
            var dir = Path.Combine(AppPaths.StagingRoot, id.ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void CleanAll()
    {
        lock (Gate)
        {
            try
            {
                if (!Directory.Exists(AppPaths.StagingRoot)) return;
                foreach (var d in Directory.GetDirectories(AppPaths.StagingRoot))
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            catch (Exception ex) { Log.Warn("清理暂存区失败: " + ex.Message); }
        }
    }

    /// <summary>保证目标文件名不与现有文件冲突（追加 (2)、(3)…）。</summary>
    public static string UniqueName(string dir, string name)
    {
        name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        name = FileScanner.EscapeReserved(name);   // 躲开 CON/NUL/COM1 这类保留设备名
        var cand = Path.Combine(dir, name);
        if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (int i = 2; i < 10000; i++)
        {
            cand = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        }
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }
}
