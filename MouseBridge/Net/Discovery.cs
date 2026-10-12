using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MouseBridge.Net;

/// <summary>扫描/自动发现到的一台对端。</summary>
public sealed record PeerHit(string Name, string Host, int Port)
{
    public string Where => Host + ":" + Port;
    public string Title => (Name.Length > 0 ? Name : "未知设备") + "　(" + Where + ")";

    /// <summary>
    /// 同一台机器的判定依据。
    /// 优先用机器名（同一台机器从不同网段探到，名字相同）；
    /// 拿不到名字时退化成端点，绝不把两台不同的机器误并成一台。
    /// </summary>
    public string MachineKey => Name.Length > 0 ? "n:" + Name : "e:" + Where;

    /// <summary>
    /// 地址优先级：越"近"越优先保留（同一 /24 直连 &gt; 192.168.x 家用段 &gt; 10.x &gt; 172.16-31.x）。
    /// 「能在同一个段里直接对话」的那条才是用户真正要用的地址。
    /// </summary>
    public int Rank
    {
        get
        {
            var p = Host.Split('.');
            if (p.Length != 4) return 99;
            if (p[0] == "192" && p[1] == "168") return 0;
            if (p[0] == "10") return 1;
            if (p[0] == "172") return 2;
            return 9;
        }
    }
}

/// <summary>扫描进度（供界面实时显示）。</summary>
public sealed class ScanReport
{
    public string Phase = "";        // 当前阶段说明
    public int Done;                 // 本阶段端口探测已完成数
    public int Total;                // 本阶段需要探测的主机数
    public List<PeerHit> Hits = new();
}

/// <summary>
/// 局域网自动发现 + 手动扫描。
///
/// 两个角色完全对称（都能主动连接）：
///   · 周期性发 probe（问"谁在"）与 offer（说"我在"）；
///   · 收到 probe → 单播回一份 offer，并把来源记成候选；
///   · 收到 offer → 直接记成候选；
///   · 自己的网卡 IP 一律过滤（广播会回环、扫描会打到自己，连自己还会"自己控自己"）。
///
/// 跨路由器（两级路由 / 不同网段）时 UDP 广播出不了网关，只能靠 TCP 端口探测兜底。
/// 分两阶段：
///   阶段一（约 2 秒）：本机各 /24 及第三段 ±2 邻段 + 常见家用网段
///                     （192.168.0~3 / 192.168.31 / 10.0.0 / 10.0.1 / 172.16.0）
///   阶段二（约 8 秒，阶段一没命中才跑）：把 192.168.0~31 整片扫一遍 ——
///                     小米/华为/TP-LINK 等家用路由的默认段都在里面，
///                     典型场景「本机 192.168.31.x、对端 192.168.1.x」这种级联组网就能扫到。
/// 全程 384 并发 + 380ms 超时，边扫边把结果推给界面。
/// </summary>
public class Discovery : IDisposable
{
    public const int Port = 39500;
    UdpClient? _udp;
    System.Threading.Timer? _timer;
    System.Threading.Timer? _announce;
    string _role = "server";
    string _myName = "";
    int _tcpPort;
    int _probeCount;
    long _lastSeenTick = -10000;
    readonly object _gate = new();
    /// <summary>host:port → 候选。用端点做键，两台机器改同名也不会互相覆盖。</summary>
    readonly Dictionary<string, PeerHit> _found = new();
    /// <summary>host:port → 最后一次听到它的时刻（Monotonic.Ms）。
    /// 对端关机 / 换网段后，_found 里的旧条目如果不清理，PickBest() 会一直返回一个死地址，
    /// 自动连接就永远在轮试一个连不上的目标。超过 TtlMs 没人应答的条目在读取时一并剔除。</summary>
    readonly Dictionary<string, long> _foundAt = new();
    const long FoundTtlMs = 90000;

    /// <summary>听到对端（probe 或 offer）时触发，连接循环借此立即发起连接。</summary>
    public event Action? OnPeerSeen;

    const int ProbeConcurrency = 384;
    const int ProbeTimeoutMs = 380;

    /// <summary>阶段一优先扫的常见网段（用前三段表示）。</summary>
    static readonly string[] HotSegments =
    {
        "192.168.0", "192.168.1", "192.168.2", "192.168.3", "192.168.31",
        "10.0.0", "10.0.1", "172.16.0",
    };

    public void Start(string role, string myName, int tcpPort)
    {
        Stop();
        _role = role; _myName = myName; _tcpPort = tcpPort; _probeCount = 0;
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            _udp.Client.EnableBroadcast = true;
            _ = Task.Run(RecvLoop);
            _timer = new System.Threading.Timer(_ => Probe(), null, 0, 500);      // 前 12 发每 0.5s，之后 2s
            _announce = new System.Threading.Timer(_ => Announce(), null, 200, 2500);
            Log.Info($"发现服务已启动（角色={role}）");
        }
        catch (Exception ex) { Log.Error("发现服务启动失败（端口可能被占用）", ex); }
    }

    void Probe()
    {
        try
        {
            var b = JsonSerializer.SerializeToUtf8Bytes(new { r = "probe", n = _myName, p = _tcpPort });
            SendAll(b);
            _probeCount++;
            if (_probeCount == 12) _timer?.Change(0, 2000);
        }
        catch (Exception ex) { Log.Debug("广播失败: " + ex.Message); }
    }

    void Announce()
    {
        try
        {
            var b = JsonSerializer.SerializeToUtf8Bytes(new { r = "offer", n = _myName, p = _tcpPort });
            SendAll(b);
        }
        catch (Exception ex) { Log.Debug("宣告失败: " + ex.Message); }
    }

    /// <summary>把探测/宣告发给所有能去的广播地址（含每个网卡的定向广播，多数家用路由允许转发）。</summary>
    void SendAll(byte[] b)
    {
        foreach (var t in BroadcastTargets())
        {
            try { _udp?.Send(b, b.Length, new IPEndPoint(t, Port)); } catch { }
        }
    }

    async Task RecvLoop()
    {
        while (_udp != null)
        {
            try
            {
                var res = await _udp.ReceiveAsync();
                var from = res.RemoteEndPoint.Address;
                if (IsLocal(from)) continue;
                var msg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(res.Buffer);
                if (msg == null || !msg.TryGetValue("r", out var rr)) continue;
                var r = rr.GetString();
                var name = msg.TryGetValue("n", out var nn) ? nn.GetString() ?? "?" : "?";
                int port = msg.TryGetValue("p", out var pp) ? pp.GetInt32() : _tcpPort;
                var host = from.ToString();

                if (r == "probe")
                {
                    try
                    {
                        var b = JsonSerializer.SerializeToUtf8Bytes(new { r = "offer", n = _myName, p = _tcpPort });
                        await _udp.SendAsync(b, b.Length, res.RemoteEndPoint);
                    }
                    catch { }
                    Record(name, host, port);
                }
                else if (r == "offer")
                {
                    Record(name, host, port);
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { Log.Debug("发现接收异常: " + ex.Message); }
        }
    }

    /// <summary>
    /// 广播听到一台机器。同一台机器从多个网段都能听到时，按「哪种地址更近」决定保留谁，
    /// 避免 _found 里堆出一台机器的多个地址、进而让自动连接去轮试一堆死地址。
    /// </summary>
    void Record(string name, string host, int port)
    {
        bool changed;
        var key = host + ":" + port;
        var hit = new PeerHit(name, host, port);
        lock (_gate)
        {
            _foundAt[key] = Monotonic.Ms;
            changed = !_found.TryGetValue(key, out var old) || old.Name != name;
            if (changed && name.Length > 0)
            {
                // 同一台机器已经有别的地址了：谁的分段更近谁留下
                var dup = _found.FirstOrDefault(kv =>
                    kv.Key != key && kv.Value.Name.Length > 0 &&
                    string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase));
                if (dup.Key != null)
                {
                    if (hit.Rank < dup.Value.Rank) { _found.Remove(dup.Key); _foundAt.Remove(dup.Key); }
                    else { _found[key] = hit; goto Done; }
                }
            }
            _found[key] = hit;
        Done:;
        }
        if (changed) Log.Info($"发现对端: {name} ({host}:{port})");
        if (Monotonic.Ms - _lastSeenTick > 1500)
        {
            _lastSeenTick = Monotonic.Ms;
            OnPeerSeen?.Invoke();
        }
    }

    /// <summary>剔除超过 TTL 没人应答的旧条目。<b>调用方必须已持 _gate</b>。</summary>
    void PruneFoundLocked()
    {
        if (_foundAt.Count == 0) return;
        long now = Monotonic.Ms;
        List<string>? dead = null;
        foreach (var kv in _foundAt)
            if (now - kv.Value > FoundTtlMs) (dead ??= new List<string>()).Add(kv.Key);
        if (dead == null) return;
        foreach (var k in dead) { _found.Remove(k); _foundAt.Remove(k); }
    }

    public List<PeerHit> Peers()
    {
        lock (_gate)
        {
            PruneFoundLocked();
            return _found.Values.ToList();
        }
    }

    public (string host, int port)? PickBest()
    {
        lock (_gate)
        {
            PruneFoundLocked();
            foreach (var v in _found.Values) return (v.Host, v.Port);
        }
        return null;
    }

    // ============================ 手动扫描 ============================

    /// <summary>手动「扫描」：UDP 定向广播（同网段）+ TCP 端口探测（跨路由器）。</summary>
    public async Task<List<PeerHit>> ScanNowAsync(int tcpPort, Action<ScanReport>? report = null)
    {
        var hits = new List<PeerHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hitsGate = new object();
        var mine = LocalIps();

        void Push(string phase, int done = 0, int total = 0)
        {
            if (report == null) return;
            List<PeerHit> snap;
            lock (hitsGate) snap = new List<PeerHit>(hits);
            report(new ScanReport { Phase = phase, Done = done, Total = total, Hits = snap });
        }

        bool AddHit(string host, int port, string name)
        {
            lock (hitsGate)
            {
                if (!seen.Add(host + ":" + port)) return false;
                hits.Add(new PeerHit(name, host, port));
                return true;
            }
        }

        // —— 1) UDP 定向广播（临时套接字，不影响常驻发现服务） ——
        Push("正在用广播找同网段的对端…");
        try
        {
            using var sock = new UdpClient();
            sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            sock.Client.EnableBroadcast = true;
            var probe = JsonSerializer.SerializeToUtf8Bytes(new { r = "probe", n = _myName + " (扫描)", p = tcpPort });
            foreach (var t in BroadcastTargets())
            {
                try { sock.Send(probe, probe.Length, new IPEndPoint(t, Port)); } catch { }
            }
            var deadline = Monotonic.Ms + 1400;
            while (Monotonic.Ms < deadline)
            {
                try
                {
                    var res = await sock.ReceiveAsync().WithTimeout(TimeSpan.FromMilliseconds(250));
                    var host = res.RemoteEndPoint.Address.ToString();
                    if (mine.Contains(host)) continue;
                    var msg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(res.Buffer);
                    if (msg == null || !msg.TryGetValue("r", out var rr) || rr.GetString() != "offer") continue;
                    string name = msg.TryGetValue("n", out var nn) ? nn.GetString() ?? "" : "";
                    int port = msg.TryGetValue("p", out var pp) ? pp.GetInt32() : tcpPort;
                    AddHit(host, port, name);
                }
                catch (TimeoutException) { }
                catch { break; }
            }
        }
        catch (Exception ex) { Log.Debug("扫描(UDP)失败: " + ex.Message); }

        // —— 2) TCP 端口探测（跨路由器唯一可行的办法） ——
        var own = OwnSegments();

        async Task ProbeSegments(List<string> segments, string phase)
        {
            var hosts = new List<string>();
            foreach (var seg in segments)
                for (int d = 1; d < 255; d++) hosts.Add(seg + "." + d);
            hosts = hosts.Where(h =>
            {
                if (mine.Contains(h)) return false;
                var p = h.Split('.');
                if (p[0] == "10") return true;
                if (p[0] == "192") return p[1] == "168";
                if (p[0] == "172")
                {
                    int b2 = int.Parse(p[1]);
                    return b2 >= 16 && b2 <= 31;
                }
                return false;
            }).ToList();
            if (hosts.Count == 0) return;

            int total = hosts.Count, done = 0, lastPush = 0;
            var found = new ConcurrentBag<string>();
            using var sem = new SemaphoreSlim(ProbeConcurrency);
            var tasks = hosts.Select(async host =>
            {
                try
                {
                    await sem.WaitAsync();
                    try
                    {
                        using var tcp = new TcpClient();
                        using var cts = new CancellationTokenSource(ProbeTimeoutMs);
                        await tcp.ConnectAsync(host, tcpPort).WithToken(cts.Token);
                        found.Add(host);
                        if (AddHit(host, tcpPort, "")) Log.Info($"端口探测命中 {host}:{tcpPort}");
                    }
                    finally { sem.Release(); }
                }
                catch { }
                finally
                {
                    int d = Interlocked.Increment(ref done);
                    if (d - lastPush >= 200 || d == total)
                    {
                        lastPush = d;
                        Push($"{phase}… {d}/{total}", d, total);
                    }
                }
            });
            await Task.WhenAll(tasks);
            Push($"{phase}完成", total, total);
        }

        await ProbeSegments(Stage1Segments(own), "正在探测常见网段");
        if (hits.Count == 0)
        {
            // 阶段一颗粒无收：多半是"两台机器不在同一个 /24 里"（级联路由/多路由组网），
            // 这时把家用路由常见的 192.168.0~31 整片扫一遍。
            await ProbeSegments(Stage2Segments(own), "范围较小，正在扩大扫描范围");
        }

        // —— 3) 给只探到端口、没拿到名字的机器补一次单播询问 ——
        List<PeerHit> unnamed;
        lock (hitsGate) unnamed = hits.Where(h => h.Name.Length == 0).ToList();
        if (unnamed.Count > 0) await ResolveNamesAsync(unnamed, tcpPort);

        // —— 4) 按机器名归并：同一台机器从两个网段都能探到时，只保留一条 ——
        //
        // 典型场景：被控端同时挂在 192.168.31.228（自己那个网段的地址）与
        // 192.168.0.2（上级路由给它的地址），两台路由互通时两个地址都能探通。
        // 用户看到列表里同一台机器出现两遍，会以为出了问题 —— 实际只有一条能用。
        // 这里按 机器名 → 最优地址 归并；拿不到名字的（空名）不参与归并，
        // 免得把"恰好都没问到名字的两台不同机器"误并成一台。
        lock (hitsGate)
        {
            var merged = new Dictionary<string, PeerHit>(StringComparer.OrdinalIgnoreCase);
            var unnamedHits = new List<PeerHit>();

            foreach (var h in hits)
            {
                if (h.Name.Length == 0) { unnamedHits.Add(h); continue; }
                var key = h.MachineKey;
                if (merged.TryGetValue(key, out var old))
                {
                    // 分段更"近"的胜出；同分段时保留先到的（先到的通常是直连广播听到的，更可靠）
                    if (h.Rank < old.Rank)
                    {
                        merged[key] = h;
                        Log.Info($"同机多地址归并：{h.Name} {old.Where} → 保留 {h.Where}");
                    }
                    else
                    {
                        Log.Info($"同机多地址归并：{h.Name} {h.Where} → 保留 {old.Where}");
                    }
                }
                else merged[key] = h;
            }

            hits.Clear();
            hits.AddRange(merged.Values);
            hits.AddRange(unnamedHits);
            hits.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Name.Length > 0 ? a.Name : "\uffff", b.Name.Length > 0 ? b.Name : "\uffff");
                if (c != 0) return c;
                c = a.Rank.CompareTo(b.Rank);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Host, b.Host);
            });
        }

        Push("扫描完成");
        Log.Info($"手动扫描完成：{hits.Count} 台（其中 {hits.Count(h => h.Name.Length > 0)} 台拿到了名字）");
        return hits;
    }

    /// <summary>对"只探到端口"的机器发一次单播 probe，把它的名字问出来（列表里好辨认）。</summary>
    async Task ResolveNamesAsync(List<PeerHit> unnamed, int tcpPort)
    {
        try
        {
            using var sock = new UdpClient();
            sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var probe = JsonSerializer.SerializeToUtf8Bytes(new { r = "probe", n = _myName + " (扫描)", p = tcpPort });
            foreach (var h in unnamed)
            {
                try { sock.Send(probe, probe.Length, new IPEndPoint(IPAddress.Parse(h.Host), Port)); } catch { }
            }
            var deadline = Monotonic.Ms + 900;
            while (Monotonic.Ms < deadline)
            {
                UdpReceiveResult res;
                try { res = await sock.ReceiveAsync().WithTimeout(TimeSpan.FromMilliseconds(200)); }
                catch (TimeoutException) { continue; }
                var host = res.RemoteEndPoint.Address.ToString();
                var msg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(res.Buffer);
                if (msg == null || !msg.TryGetValue("r", out var rr) || rr.GetString() != "offer") continue;
                string name = msg.TryGetValue("n", out var nn) ? nn.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                for (int i = 0; i < unnamed.Count; i++)
                    if (unnamed[i].Host == host && unnamed[i].Name.Length == 0)
                    {
                        unnamed[i] = unnamed[i] with { Name = name };
                        Log.Info($"确认对端名称：{name} ({host})");
                    }
            }
        }
        catch (Exception ex) { Log.Debug("补问对端名字失败: " + ex.Message); }
    }

    // ---------------- 网段计算 ----------------

    /// <summary>本机各网卡所在 /24（取前三段）。</summary>
    static List<string> OwnSegments()
    {
        var segs = new List<string>();
        foreach (var (ip, _) in LocalIpsRaw())
        {
            var s = $"{ip[0]}.{ip[1]}.{ip[2]}";
            if (!segs.Contains(s)) segs.Add(s);
        }
        return segs;
    }

    /// <summary>阶段一：本机各段 ±2 邻段 + 常见网段。</summary>
    static List<string> Stage1Segments(List<string> own)
    {
        var segs = new List<string>(HotSegments);
        foreach (var seg in own)
        {
            var p = seg.Split('.');
            int o3 = int.Parse(p[2]);
            for (int d = -2; d <= 2; d++)
            {
                int v = o3 + d;
                if (v is > 0 and < 255)
                {
                    var s = $"{p[0]}.{p[1]}.{v}";
                    if (!segs.Contains(s)) segs.Add(s);
                }
            }
        }
        return segs;
    }

    /// <summary>阶段二：补上 192.168.0~31 整片 + 几个 10.x / 172.16.x 段。</summary>
    static List<string> Stage2Segments(List<string> own)
    {
        var done = new HashSet<string>(Stage1Segments(own));
        var segs = new List<string>();
        for (int i = 0; i <= 31; i++) segs.Add($"192.168.{i}");
        segs.Add("10.1.1");
        for (int i = 1; i <= 4; i++) segs.Add($"172.16.{i}");
        return segs.Where(s => !done.Contains(s)).ToList();
    }

    /// <summary>本机所有 IPv4 地址（含回环），用于"别把自己当对端"。</summary>
    static HashSet<string> LocalIps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };
        foreach (var (ip, _) in LocalIpsRaw()) set.Add(new IPAddress(ip).ToString());
        return set;
    }

    /// <summary>
    /// 自测开关：置 MB_ALLOW_SELF=1 后不再过滤本机地址。
    /// 正常运行时必须过滤（广播会回环、扫描会打到自己，连自己还会"自己控自己"）。
    /// </summary>
    public static bool AllowSelfLoopback => Environment.GetEnvironmentVariable("MB_ALLOW_SELF") == "1";

    public static bool IsLocal(IPAddress addr)
    {
        try
        {
            if (AllowSelfLoopback) return false;
            if (IPAddress.IsLoopback(addr)) return true;
            var s = addr.ToString();
            foreach (var (ip, _) in LocalIpsRaw())
                if (new IPAddress(ip).ToString() == s) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>本机所有可用 IPv4（多网卡时全列出来，让用户挑对的那个告诉对方）。</summary>
    public static List<string> LocalIPv4()
    {
        var list = new List<string>();
        foreach (var (ip, _) in LocalIpsRaw())
        {
            var s = new IPAddress(ip).ToString();
            if (!list.Contains(s)) list.Add(s);
        }
        return list;
    }

    static List<(byte[] ip, byte[] mask)> LocalIpsRaw()
    {
        var list = new List<(byte[], byte[])>();
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    var ip = u.Address.GetAddressBytes();
                    // 169.254.x 是没拿到 DHCP 的自动地址，扫它没意义
                    if (ip[0] == 169 && ip[1] == 254) continue;
                    list.Add((ip, u.IPv4Mask.GetAddressBytes()));
                }
            }
        }
        catch { }
        return list;
    }

    static List<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    var ip = u.Address.GetAddressBytes();
                    var mask = u.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    var dir = new IPAddress(bc);
                    if (!targets.Any(t => t.Equals(dir))) targets.Add(dir);
                }
            }
        }
        catch { }
        return targets;
    }

    public List<string> Snapshot()
    {
        lock (_gate)
        {
            PruneFoundLocked();
            return _found.Values
                .Select(v => (v.Name.Length > 0 ? v.Name : "未知设备") + $"  ({v.Where})")
                .ToList();
        }
    }

    public void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        try { _announce?.Dispose(); } catch { }
        _timer = null;
        _announce = null;
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }

    public void Dispose() => Stop();
}
