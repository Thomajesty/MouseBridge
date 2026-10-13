using System.IO;
using System.Net.Http;
using System.Text.Json;

#if NETFRAMEWORK
using System.Net;
#endif

namespace MouseBridge.Core;

/// <summary>一次检查得到的线上最新版本信息（不含下载内容）。</summary>
public sealed class UpdateInfo
{
    /// <summary>最新版本号（去掉 v 前缀，如 "1.7.0"）。</summary>
    public string LatestVersion = "";

    /// <summary>Release 的 tag（如 "v1.7.0"）。</summary>
    public string Tag = "";

    /// <summary>选中的更新包资产名（如 "MouseBridge-1.7.0-win-x64.exe"）。</summary>
    public string AssetName = "";

    /// <summary>资产 id（下载走 API octet-stream，绕开被墙的网页域）。</summary>
    public long AssetId;

    /// <summary>资产字节数。</summary>
    public long AssetSize;

    /// <summary>Release 说明页（html_url，仅展示用）。</summary>
    public string ReleaseUrl = "";

    /// <summary>线上版本比本程序新？</summary>
    public bool HasUpdate => CompareVersions(LatestVersion, AppInfo.Version) > 0;

    /// <summary>按点分段逐段比较，缺段按 0。返回 &gt;0 表示 a 更新。</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < 4; i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    static int[] Parse(string v)
    {
        // 去掉 v 前缀与 "-xxx" 后缀（如 "1.7.0-beta"）
        v = (v ?? "").Trim().TrimStart('v', 'V');
        int dash = v.IndexOf('-');
        if (dash >= 0) v = v.Substring(0, dash); // net48 没有 System.Index，不用 [..n] 切片
        var parts = v.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries); // net48 没有 Split(char, opts)
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            int.TryParse(parts[i].Trim(), out nums[i]);
        return nums;
    }
}

/// <summary>
/// 应用内检查更新：查询 GitHub Releases 最新版，按系统选包，走 API 下载资产。
/// 网络现实：api.github.com 与 objects.githubusercontent.com 可直连，
/// github.com 网页域在部分网络不可达，所以下载不用 browser_download_url。
/// </summary>
public static class UpdateChecker
{
    public const string RepoOwner = "Thomajesty";
    public const string RepoName = "MouseBridge";

    static HttpClient? _http;

    static HttpClient Http
    {
        get
        {
            if (_http != null) return _http;
#if NETFRAMEWORK
            // Win7 上默认协议可能不含 TLS 1.2（装了 KB3140245 才系统级开启），显式追加
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
#endif
            var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            h.Timeout = TimeSpan.FromSeconds(30);
            h.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            _http = h;
            return h;
        }
    }

    /// <summary>本机该用哪个包：Win7/8 或 32 位系统 → win7 zip（手动解压）；其余 → 自包含单文件 exe（可自动替换）。</summary>
    public static bool PrefersWin7Zip =>
        Environment.OSVersion.Version.Major < 10 || IntPtr.Size == 4;

    /// <summary>
    /// 查询最新 Release。线上没有任何 Release 时返回 null；网络/解析异常向上抛。
    /// 全链路 ConfigureAwait(false)：允许被 UI 线程同步等待而不死锁。
    /// </summary>
    public static async Task<UpdateInfo?> QueryLatestAsync()
    {
        string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new UpdateInfo
        {
            Tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "",
            ReleaseUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "",
        };
        info.LatestVersion = info.Tag.TrimStart('v', 'V');

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return info;

        string want = PrefersWin7Zip ? "-win7-32bit.zip" : "-win-x64.exe";
        // 只挑"版本号与 tag 一致且匹配本机形态"的资产，避免误抓旧的 net8/win7 包
        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.StartsWith($"MouseBridge-{info.LatestVersion}", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
            info.AssetName = name;
            info.AssetId = a.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id) ? id : 0;
            info.AssetSize = a.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out var sz) ? sz : 0;
            break;
        }
        return info;
    }

    /// <summary>
    /// 下载资产到 destFile（覆盖）。progress 取值 0~100；总长未知时按已下字节缓慢推进不阻塞。
    /// </summary>
    public static async Task DownloadAssetAsync(long assetId, string destFile, IProgress<double>? progress)
    {
        string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/assets/{assetId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/octet-stream");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        var tmp = destFile + ".part";
        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))       // net48 无 IAsyncDisposable，用同步 using
        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            var buf = new byte[64 * 1024];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                done += n;
                if (progress != null)
                    progress.Report(total is > 0 ? Math.Min(100.0, done * 100.0 / total.Value) : 0);
            }
        }
        if (File.Exists(destFile)) File.Delete(destFile); // net48 的 Move 没有 overwrite 重载
        File.Move(tmp, destFile);
    }

    /// <summary>
    /// 命令行 --check-update 用：检查并把结果写一行到 resultFile（UTF-8），
    /// 便于自动化测试读取。返回 0 正常 / 1 出错。
    /// </summary>
    public static async Task<int> CheckToFileAsync(string resultFile)
    {
        string line;
        try
        {
            var info = await QueryLatestAsync();
            if (info == null) line = "ok latest=none hasupdate=false";
            else
            {
                line = $"ok latest={info.LatestVersion} hasupdate={(info.HasUpdate ? "true" : "false")}" +
                       $" asset={info.AssetName} size={info.AssetSize}";
            }
        }
        catch (Exception ex)
        {
            line = "error " + ex.Message.Replace('\n', ' ').Replace('\r', ' ');
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
        File.WriteAllText(resultFile, line, new System.Text.UTF8Encoding(false));
        return line.StartsWith("ok") ? 0 : 1;
    }
}
