using System.IO;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MouseBridge.Net;

public class PeerInfo
{
    public string Name = "";
    public string Ver = "";
    public DispInfo Disp = new(0, 0, 0, 0);
    public string Side = "right";
    /// <summary>对端角色（server/client），握手时带过来。</summary>
    public string Role = "";
    /// <summary>对端计算机名，用于两端同名时的确定性仲裁。</summary>
    public string Machine = "";
}

/// <summary>
/// 两台机器之间的一条加密链路：TCP + TLS（可选）+ HMAC 密码认证。
/// 帧格式：[4B 总长][2B 头长][JSON 头][二进制载荷]，全部大端。
/// </summary>
public class SecureLink : IDisposable
{
    readonly TcpClient _tcp;
    Stream _s;
    readonly bool _isServerSide;
    readonly Channel<(Msg, byte[]?)> _outq = Channel.CreateBounded<(Msg, byte[]?)>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
    readonly CancellationTokenSource _cts = new();
    Task? _rd, _wr;
    long _lastRecv;
    string? _remoteThumb;

    public PeerInfo Peer { get; } = new();
    public volatile bool Alive;

    /// <summary>最近一次握手失败的原因（给界面显示，比"连不上"有用得多）。</summary>
    public string LastError = "";

    /// <summary>对端地址（握手前就能拿到，用于弹窗/日志里说明是谁在连）。</summary>
    public string RemoteAddress = "";

    public event Action<Msg, byte[]?>? OnMsg;
    public event Action? OnClosed;

    SecureLink(TcpClient tcp, Stream s, bool isServerSide)
    {
        _tcp = tcp; _s = s; _isServerSide = isServerSide;
        try { _tcp.NoDelay = true; } catch { }
        _lastRecv = Monotonic.Ms;
    }

    /// <summary>客户端侧：主动连接。连接失败会清理半开的 TcpClient（调用方可带超时 WaitAsync）。</summary>
    public static async Task<SecureLink> ConnectAsync(string host, int port, bool tls)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        tcp.NoDelay = true;
        Stream st = tcp.GetStream();
        var link = new SecureLink(tcp, st, false) { RemoteAddress = host };
        if (tls)
        {
            var ssl = new SslStream(st, false, link.ValidateRemoteCert);
            await SslEx.AuthenticateClientAsync(ssl, "mousebridge");
            link._s = ssl;
        }
        return link;
    }

    /// <summary>服务端侧：接受连接（需要本地证书）。</summary>
    public static async Task<SecureLink> AcceptAsync(TcpClient tcp, bool tls, X509Certificate2? cert)
    {
        tcp.NoDelay = true;
        Stream st = tcp.GetStream();
        var link = new SecureLink(tcp, st, true) { RemoteAddress = PeerAddressOf(tcp) };
        if (tls)
        {
            var ssl = new SslStream(st, false);
            await SslEx.AuthenticateServerAsync(ssl, cert!);
            link._s = ssl;
        }
        return link;
    }

    static string PeerAddressOf(TcpClient tcp)
    {
        try { return (tcp.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? ""; }
        catch { return ""; }
    }

    // 客户端不验证证书链，而是握手成功后按指纹钉扎（TOFU + 密码认证），见 Orchestrator
    bool ValidateRemoteCert(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is X509Certificate2 c2) _remoteThumb = c2.Thumbprint;
        return true;
    }

    public string? RemoteThumb => _remoteThumb;

    static byte[] Nonce() => Rand.Bytes(32);
    static byte[] HmacOf(string pw, byte[] nonce)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(pw));
        return h.ComputeHash(nonce);
    }

    /// <summary>
    /// 密码认证握手 + 人工确认。
    ///
    /// authServer=true 表示本端是 TCP 被动接受方（发挑战）。
    /// 密码校验通过后还有一道「人工确认」：
    ///   · 由**会话里的被控端**负责（drivesConfirm 回调判定，两端结论一致且相反）；
    ///   · 需要确认时先发 confirm_req 让对方界面显示"等待对方确认…"，再弹窗问本机用户；
    ///   · 不需要确认（或用户关掉了确认开关）就直接回 confirm_result=true。
    /// 这样两端都在密码正确之后才建链，被控端始终有一次说"不"的机会。
    /// </summary>
    public async Task<bool> HandshakeAsync(bool authServer, string password, string myName, string myVer,
        DispInfo mine, string mySide, string myRole = "", string myMachine = "",
        Func<SecureLink, bool>? drivesConfirm = null,
        Func<SecureLink, Task<bool>>? askUser = null,
        Action? onPeerAsking = null,
        int timeoutMs = 6000, int confirmTimeoutMs = 60000)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        linked.CancelAfter(timeoutMs);
        var ct = linked.Token;
        try
        {
            if (authServer)
            {
                var nonce = Nonce();
                await WriteFrameAsync(new Msg { T = "auth_challenge", Nonce = Convert.ToBase64String(nonce) }, null, ct);
                var (resp, _) = await ReadFrameAsync(ct);
                if (resp.T != "auth_response") { LastError = "对端协议不匹配"; return false; }
                var expect = HmacOf(password, nonce);
                byte[] got;
                try { got = Convert.FromBase64String(resp.Hmac ?? ""); } catch { LastError = "对端认证数据异常"; return false; }
                if (expect.Length != got.Length || !Crypto.FixedTimeEquals(expect, got))
                {
                    await WriteFrameAsync(new Msg { T = "auth_result", Ok = false, MsgText = "密码错误" }, null, ct);
                    Log.Warn($"认证失败：对端密码错误 ({resp.Name})");
                    LastError = "连接密码不一致";
                    return false;
                }
                Peer.Name = resp.Name ?? "?"; Peer.Ver = resp.Ver ?? "";
                Peer.Role = resp.Role ?? ""; Peer.Machine = resp.Machine ?? "";
                Peer.Side = resp.Side ?? "right";
                Peer.Disp = new DispInfo(resp.X, resp.Y, resp.W, resp.H);
                await WriteFrameAsync(new Msg { T = "auth_result", Ok = true, Name = myName, Ver = myVer,
                    Role = myRole, Machine = myMachine,
                    X = mine.X, Y = mine.Y, W = mine.W, H = mine.H, Side = mySide }, null, ct);
            }
            else
            {
                var (ch, _) = await ReadFrameAsync(ct);
                if (ch.T != "auth_challenge" || ch.Nonce == null) { LastError = "对端未发起认证"; return false; }
                var nonce = Convert.FromBase64String(ch.Nonce);
                var mac = HmacOf(password, nonce);
                await WriteFrameAsync(new Msg { T = "auth_response", Hmac = Convert.ToBase64String(mac),
                    Name = myName, Ver = myVer, Role = myRole, Machine = myMachine, Side = mySide,
                    X = mine.X, Y = mine.Y, W = mine.W, H = mine.H }, null, ct);
                var (res, _) = await ReadFrameAsync(ct);
                if (res.T != "auth_result" || !res.Ok)
                {
                    Log.Warn($"认证被拒绝：{res.MsgText}");
                    LastError = string.IsNullOrEmpty(res.MsgText) ? "连接密码不一致" : res.MsgText!;
                    return false;
                }
                Peer.Name = res.Name ?? "?"; Peer.Ver = res.Ver ?? "";
                Peer.Role = res.Role ?? ""; Peer.Machine = res.Machine ?? "";
                Peer.Disp = new DispInfo(res.X, res.Y, res.W, res.H);
                Peer.Side = res.Side ?? "right";
            }
        }
        catch (OperationCanceledException) { Log.Warn("认证握手超时"); LastError = "认证超时（对方没响应）"; return false; }
        catch (Exception ex) { Log.Error("认证握手失败", ex); LastError = "认证失败：" + ex.Message; return false; }

        // ---------------- 人工确认 ----------------
        using var cct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cct.CancelAfter(confirmTimeoutMs);
        var cctk = cct.Token;
        try
        {
            bool drives = drivesConfirm?.Invoke(this) ?? false;
            if (drives)
            {
                bool allow = true;
                if (askUser != null)
                {
                    // 先告诉对端"我正在问本机用户"，让它的界面能显示"等待对方确认…"
                    await WriteFrameAsync(new Msg { T = "confirm_req", Name = myName, Role = myRole }, null, cctk);
                    allow = await askUser(this);
                }
                await WriteFrameAsync(new Msg
                {
                    T = "confirm_result",
                    Ok = allow,
                    MsgText = allow ? "" : "对方在被控端拒绝了本次连接",
                }, null, cctk);
                if (!allow)
                {
                    Log.Info("本机用户拒绝了本次连接");
                    LastError = "已在本机拒绝";
                    return false;
                }
                return true;
            }

            var (cf, _) = await ReadFrameAsync(cctk);
            if (cf.T == "confirm_req")
            {
                onPeerAsking?.Invoke();
                (cf, _) = await ReadFrameAsync(cctk);
            }
            if (cf.T != "confirm_result" || !cf.Ok)
            {
                LastError = string.IsNullOrEmpty(cf.MsgText) ? "对方拒绝了本次连接" : cf.MsgText!;
                Log.Warn("连接被对方拒绝：" + LastError);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "等待对方确认超时";
            Log.Warn("等待对方确认超时");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("确认环节失败", ex);
            LastError = "确认环节失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>启动收发循环与心跳。</summary>
    public void Start()
    {
        Alive = true;
        _lastRecv = Monotonic.Ms;
        _wr = Task.Run(WriteLoop);
        _rd = Task.Run(ReadLoop);
    }

    async Task WriteLoop()
    {
        try
        {
            await foreach (var (m, p) in _outq.Reader.ReadAllEx(_cts.Token))
            {
                var frame = Proto.Encode(m, p);
                await _s.WriteAsync(frame, 0, frame.Length, _cts.Token);
                await _s.FlushAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug("写循环退出: " + ex.Message); }
        Close();
    }

    async Task ReadLoop()
    {
        try
        {
            _ = Task.Run(HeartbeatLoop);
            while (true)
            {
                var (m, p) = await ReadFrameAsync(_cts.Token);
                _lastRecv = Monotonic.Ms;
                // pong 只是心跳回执，直接丢给 RTT 计算，不上报给上层。
                if (m.T == "ping") { Post(new Msg { T = "pong" }, null); continue; }
                if (m.T == "pong") { UpdateRtt(); continue; }
                OnMsg?.Invoke(m, p);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Debug("读循环退出: " + ex.Message); }
        Close();
    }

    // —— 往返时延（C9）——
    // 只做展示用：网线/无线切换、跨网段时用户能直观看到"这条链路多快"，
    // 也方便判断"卡"是网络问题还是机器问题。取指数平滑，避免单次抖动乱跳。
    //
    // 实现要点：Monotonic.Ms 是**进程内**单调时钟，对端时钟值毫无意义，
    // 所以时延只能在发送方本机测 —— 发 ping 前记下本机时刻，收到 pong 时相减。
    // 心跳本来就是 2 秒一轮，直接复用它当探针，不额外增加流量。
    long _rttMs = -1;
    long _lastPingSentMs = 0;
    public long RttMs => _rttMs;

    void UpdateRtt()
    {
        if (_lastPingSentMs <= 0) return;
        long rtt = Monotonic.Ms - _lastPingSentMs;
        if (rtt < 0) return;
        _rttMs = _rttMs < 0 ? rtt : (long)(_rttMs * 0.7 + rtt * 0.3);
    }

    async Task HeartbeatLoop()
    {
        while (Alive)
        {
            await Task.Delay(2000);
            if (!Alive) return;
            _lastPingSentMs = Monotonic.Ms;    // 记下发出时刻，pong 回来时据此算 RTT
            Post(new Msg { T = "ping" }, null);
            if (Monotonic.Ms - _lastRecv > 9000)
            {
                Log.Warn("心跳超时，断开连接");
                Close();
                return;
            }
        }
    }

    /// <summary>入队发送（有背压：通道满时等待，保证大文件传输不打爆内存）。入队顺序即发送顺序。</summary>
    public Task SendAsync(Msg m, byte[]? payload = null) => _outq.Writer.WriteAsync((m, payload), _cts.Token).AsTask();

    /// <summary>尽力而为的快速发送（队列满则丢弃，仅用于可重发的状态消息）。</summary>
    public void Post(Msg m, byte[]? payload = null) => _outq.Writer.TryWrite((m, payload));

    async Task WriteFrameAsync(Msg m, byte[]? payload, CancellationToken ct)
    {
        var frame = Proto.Encode(m, payload);
        await _s.WriteAsync(frame, 0, frame.Length, ct);
        await _s.FlushAsync(ct);
    }

    async Task<(Msg, byte[]?)> ReadFrameAsync(CancellationToken ct)
    {
        var prefix = await ReadExactAsync(_s, 4, ct);
        int total = Proto.ReadFrameSize(prefix); // total = 前缀之后的字节数
        var rest = await ReadExactAsync(_s, total, ct);
        var frame = new byte[4 + total];
        Buffer.BlockCopy(prefix, 0, frame, 0, 4);
        Buffer.BlockCopy(rest, 0, frame, 4, total);
        try { return Proto.Decode(frame); }
        catch (Exception ex)
        {
            Log.Error($"帧解码失败 total={total} dump={Hex.Of(frame, 0, Math.Min(48, frame.Length))}", ex);
            throw;
        }
    }

    static async Task<byte[]> ReadExactAsync(Stream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = await StreamEx.ReadAsync(s, buf, off, n - off, ct);
            if (r <= 0) throw new IOException("对端关闭连接");
            off += r;
        }
        return buf;
    }

    /// <summary>
    /// 关闭链路。注意 Alive 只在 Start() 里置位，所以"已握手但还没 Start"的链路
    /// 也必须真的把 socket 关掉，否则会漏一条连着的 TCP 出去（对端还以为连上了）。
    /// OnClosed 只在原本活着时触发一次。
    /// </summary>
    public void Close()
    {
        bool wasAlive = Alive;
        Alive = false;
        try { _cts.Cancel(); } catch { }
        try { _tcp.Close(); } catch { }
        if (wasAlive) { try { OnClosed?.Invoke(); } catch { } }
    }

    public void Dispose() => Close();
}
