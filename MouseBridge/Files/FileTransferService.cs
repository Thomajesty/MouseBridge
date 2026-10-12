using System.IO;
using System.Threading.Channels;
using MouseBridge.Net;

namespace MouseBridge.Files;

/// <summary>
/// 文件传输：发送端把文件树切块推送，接收端写入暂存目录。
/// 消息序：file_hdr → file_chunk* → file_done … → xfer_done / xfer_fail
/// 目录按层级还原（文件夹会作为 IsDir 条目先行下发）。
///
/// 性能要点：磁盘读写全部在独立写线程上串行执行，网络读循环只做入队，
/// 因此大文件传输期间鼠标/键盘注入不会被磁盘 IO 卡住。
/// </summary>
public class FileTransferService
{
    readonly Func<Msg, byte[]?, Task> _send;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    CancellationTokenSource _sendCts = new();

    readonly record struct InFrame(Msg M, byte[]? P);
    readonly Channel<InFrame> _inbox = Channel.CreateBounded<InFrame>(
        new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    RecvSession? _recv;

    class RecvSession
    {
        public long Id;
        public string Dir = "";
        public readonly List<string> Roots = new();   // 顶层条目（投递时移动这些）
        public readonly List<string> Paths = new();   // 所有落盘路径（清理用）
        public FileStream? Fs;
        public long Total, Got;
    }

    public event Action<long, long, long>? Progress;      // id, 已传字节, 总字节（收发两端都会触发）
    public event Action<long, List<string>>? RecvDone;    // id, 暂存目录中的顶层路径
    public event Action<long, string>? Failed;
    public event Action<long>? SendDone;

    public FileTransferService(Func<Msg, byte[]?, Task> sendAsync)
    {
        _send = sendAsync;
        _ = Task.Run(WriteLoop);
    }

    public void CancelSending()
    {
        try { _sendCts.Cancel(); } catch { }
        _sendCts = new CancellationTokenSource();
    }

    // ================= 发送 =================

    public async Task SendFilesAsync(long id, List<FileScanner.Item> items)
    {
        await _sendLock.WaitAsync();
        try
        {
            var ct = _sendCts.Token;
            // 真实可发送总量：只统计当下确实能打开的文件。
            // 若用 items.Sum(Size)（含已消失的文件 / 目录条目）作分母，中途文件消失时
            // sent 永远追不上 total，发送端进度会卡在 99% 不动，而接收端早已到 100%
            // —— 这就是"两侧进度不一样、传完了还显示传输中"的根因。
            long total = 0;
            foreach (var it in items)
            {
                if (it.IsDir || it.Size == 0) continue;
                try { using var probe = new FileStream(it.Root, FileMode.Open, FileAccess.Read, FileShare.Read); total += probe.Length; }
                catch { /* 打不开的文件不计入分母 */ }
            }
            if (total <= 0) total = Math.Max(items.Sum(i => i.Size), 1);
            long sent = 0;
            int fileCount = items.Count(i => !i.IsDir);
            Log.Info($"开始发送 id={id}：{fileCount} 个文件 / {FileScanner.Human(total)}");

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var it = items[i];
                await _send(new Msg
                {
                    T = "file_hdr",
                    Id = id,
                    Idx = i,
                    RootIdx = it.RootIndex,
                    Path1 = it.Rel,
                    Size = it.Size,
                    IsDir = it.IsDir,
                }, null);

                if (it.IsDir || it.Size == 0)
                {
                    await _send(new Msg { T = "file_done", Id = id, Idx = i }, null);
                    continue;
                }

                try
                {
                    // net48 的 FileStream 没有 IAsyncDisposable，用同步 using 即可
                    // （写线程本来就是串行磁盘 IO，不需要异步释放）
                    using var fs = new FileStream(it.Root, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                        FileOptions.SequentialScan);
                    var buf = new byte[Proto.ChunkSize];
                    int r;
                    while ((r = await StreamEx.ReadAsync(fs, buf, 0, buf.Length, ct)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var chunk = new byte[r];
                        Buffer.BlockCopy(buf, 0, chunk, 0, r);
                        await _send(new Msg { T = "file_chunk", Id = id, Idx = i }, chunk);
                        sent += r;
                        Progress?.Invoke(id, sent, Math.Max(total, 1));
                    }
                }
                catch (FileNotFoundException) { Log.Warn($"发送中文件消失：{it.Root}"); }
                await _send(new Msg { T = "file_done", Id = id, Idx = i }, null);
            }

            // 发送前已把"打不开的文件"排除在分母外，这里再补一次满格，
            // 避免因某个文件在中途消失导致进度永远差最后一点。
            Progress?.Invoke(id, total, Math.Max(total, 1));

            await _send(new Msg
            {
                T = "xfer_done",
                Id = id,
                Size = total,
                Idx = items.Count,
                Paths = items.Where(x => x.RootIndex >= 0 && !Str.Has(x.Rel, '/')).Select(x => x.Rel).ToList(),
            }, null);
            Log.Info($"发送完成 id={id}");
            SendDone?.Invoke(id);
        }
        catch (OperationCanceledException)
        {
            try { await _send(new Msg { T = "xfer_fail", Id = id, MsgText = "发送已取消" }, null); } catch { }
            Failed?.Invoke(id, "发送已取消");
        }
        catch (Exception ex)
        {
            Log.Error("发送文件失败", ex);
            try { await _send(new Msg { T = "xfer_fail", Id = id, MsgText = ex.Message }, null); } catch { }
            Failed?.Invoke(id, ex.Message);
        }
        finally { _sendLock.Release(); }
    }

    // ================= 接收 =================

    /// <summary>接收端：收到 clip_meta / drop_begin 后先开辟会话（异步落到写线程，不阻塞网络读循环）。</summary>
    public void BeginRecv(long id, long expectedTotal)
        => Enqueue(new Msg { T = "__begin", Id = id, Size = expectedTotal }, null);

    public void CancelRecv() => Enqueue(new Msg { T = "__cancel" }, null);

    /// <summary>网络读循环调用：只入队，磁盘 IO 交给写线程。</summary>
    public void OnMsg(Msg m, byte[]? payload) => Enqueue(m, payload);

    // 通道满时 TryWrite 会失败；原来直接 `_ = WriteAsync(...)` 不 await，等于让这条消息
    // 在后台"插队"——后续消息可能先落进处理循环，**顺序被打乱**（比如 file_hdr 先于
    // __begin 到达，整份文件就永远建不起来）。这里改成一个显式的"补写"路径：满的时候
    // 在锁里同步等容量（带超时），保证入队顺序 == 处理顺序。
    readonly object _enqGate = new();

    void Enqueue(Msg m, byte[]? p)
    {
        var frame = new InFrame(m, p);
        if (_inbox.Writer.TryWrite(frame)) return;
        // 通道满了：不能再 TryWrite 等下一次（会乱序），必须原地把这一条写进去。
        lock (_enqGate)
        {
            try
            {
                if (!_inbox.Writer.TryWrite(frame))
                    _inbox.Writer.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 写线程已退出（通道 Complete）时 WriteAsync 会抛，属于收尾期，记一笔即可。
                Log.Warn($"接收队列写入失败（{m.T}）：{ex.Message}");
            }
        }
    }

    async Task WriteLoop()
    {
        await foreach (var f in _inbox.Reader.ReadAllEx())
        {
            try { Handle(f.M, f.P); }
            catch (Exception ex) { Log.Error($"处理传输消息 {f.M.T} 异常", ex); }
        }
    }

    public long? ActiveRecvId => _recv?.Id;

    void Handle(Msg m, byte[]? payload)
    {
        switch (m.T)
        {
            case "__begin":
                // 暂存目录创建失败必须在**这里**报出来。
                // 原实现把它放在下面的 try 之外，异常只会被 WriteLoop 最外层的 catch 吞掉：
                // _recv 保持 null，后面每条 file_hdr/file_chunk 都被当成"过期消息"丢掉，
                // 界面上就是"传输进度一直不动、也没有任何错误提示"，用户完全不知道发生了什么。
                try
                {
                    CancelRecvInternal();
                    _recv = new RecvSession { Id = m.Id, Dir = Staging.NewDir(m.Id), Total = m.Size };
                    Log.Debug($"开启接收会话 id={m.Id} 预期 {FileScanner.Human(m.Size)}");
                }
                catch (Exception ex)
                {
                    Log.Error($"开启接收会话 id={m.Id} 失败（暂存目录不可用）", ex);
                    _recv = null;
                    Failed?.Invoke(m.Id, "本机无法创建暂存目录：" + ex.Message);
                }
                return;
            case "__cancel":
                CancelRecvInternal();
                return;
        }

        var r = _recv;
        if (r == null || r.Id != m.Id)
        {
            if (m.T is "file_chunk" or "file_hdr") Log.Debug($"丢弃过期传输消息 id={m.Id} ({m.T})");
            return;
        }

        try
        {
            switch (m.T)
            {
                case "file_hdr":
                    r.Fs?.Dispose();
                    r.Fs = null;
                    var rel = m.Path1 ?? "file";
                    var full = FileScanner.SafeJoin(r.Dir, rel);
                    if (!Str.Has(rel, '/'))
                        full = Staging.UniqueName(Path.GetDirectoryName(full)!, Path.GetFileName(full));
                    if (m.IsDir)
                    {
                        Directory.CreateDirectory(full);
                        r.Paths.Add(full);
                        if (!Str.Has(rel, '/')) r.Roots.Add(full);
                    }
                    else
                    {
                        var parent = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        r.Fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16,
                            FileOptions.SequentialScan);
                        r.Paths.Add(full);
                        if (!Str.Has(rel, '/')) r.Roots.Add(full);
                    }
                    break;

                case "file_chunk":
                    if (payload != null && r.Fs != null)
                    {
                        r.Fs.Write(payload, 0, payload.Length);
                        r.Got += payload.Length;
                        Progress?.Invoke(r.Id, r.Got, Math.Max(r.Total, 1));
                    }
                    break;

                case "file_done":
                    r.Fs?.Dispose();
                    r.Fs = null;
                    break;

                case "xfer_done":
                    r.Fs?.Dispose();
                    r.Fs = null;
                    var roots = r.Roots.Count > 0 ? r.Roots : r.Paths;
                    // 以发送端给出的真实总量为准收尾（m.Size 是发送端实际发出的字节数）：
                    // 接收端若比它早到 100% 或永远差一点，界面就会出现"两侧进度不一致"。
                    if (m.Size > 0 && r.Total != m.Size)
                    {
                        r.Total = m.Size;
                        r.Got = m.Size;
                        Progress?.Invoke(r.Id, r.Total, Math.Max(r.Total, 1));
                    }
                    else if (r.Got != r.Total)
                    {
                        r.Got = r.Total;
                        Progress?.Invoke(r.Id, r.Got, Math.Max(r.Total, 1));
                    }
                    _recv = null;
                    Log.Info($"接收完成 id={r.Id} {r.Paths.Count} 项 / {FileScanner.Human(r.Got)}");
                    RecvDone?.Invoke(r.Id, roots);
                    break;

                case "xfer_fail":
                    var dir = r.Dir;
                    _recv = null;
                    try { r.Fs?.Dispose(); } catch { }
                    try { Directory.Delete(dir, true); } catch { }
                    Failed?.Invoke(r.Id, m.MsgText ?? "传输失败");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("接收文件异常", ex);
            var dir = r.Dir;
            _recv = null;
            try { r.Fs?.Dispose(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
            Failed?.Invoke(r.Id, ex.Message);
        }
    }

    void CancelRecvInternal()
    {
        var r = _recv;
        _recv = null;
        if (r == null) return;
        try { r.Fs?.Dispose(); } catch { }
        try { Directory.Delete(r.Dir, true); } catch { }
        Log.Debug($"取消接收 id={r.Id}");
    }
}
