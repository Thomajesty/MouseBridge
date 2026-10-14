using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MouseBridge.Core;
using MouseBridge.Files;
using MouseBridge.Net;

namespace MouseBridge.Control;

/// <summary>
/// 自检（--selftest）：不安装钩子、不碰真实桌面，验证
/// ① 协议帧编解码 ② 配置存取 ③ TLS+HMAC 认证握手（含错误密码拒绝）
/// ④ 剪贴板消息往返 ⑤ 文件传输完整性（SHA1 校验）。
/// </summary>
public static class SelfTest
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"[PASS] {name}"); }
        else { _fail++; Console.WriteLine($"[FAIL] {name} {detail}"); }
        Log.Info($"SELFTEST {(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    public static int Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        AppPaths.Ensure();
        Log.Init(LogLevel.Debug);
        Console.WriteLine($"{AppInfo.Title} v{AppInfo.Version} 自检开始");

        // 备份真实配置，测试后恢复（测试会写入自己的配置）
        string? origCfg = null;
        try { if (File.Exists(AppPaths.ConfigFile)) origCfg = File.ReadAllText(AppPaths.ConfigFile); } catch { }

        // 1. 协议帧编解码（含二进制载荷）
        try
        {
            var m = new Msg { T = "clip_meta", Id = 42, Kind = "files", Size = 123, Eager = true,
                Files = new List<FEntry> { new FEntry { N = "a.txt", S = 1 }, new FEntry { N = "b", S = 2 } } };
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var frame = Proto.Encode(m, payload);
            var (m2, p2) = Proto.Decode(frame);
            Check("协议帧编解码", m2.T == m.T && m2.Id == 42 && m2.Files!.Count == 2
                && p2 != null && p2.SequenceEqual(payload));
        }
        catch (Exception ex) { Check("协议帧编解码", false, ex.Message); }

        // 2. 配置存取（含 DPAPI 密码）
        try
        {
            var c = new Config { Password = "abc123", PeerSide = "left", Port = 12345 };
            c.Save();
            var c2 = Config.Load();
            Check("配置存取+DPAPI", c2.PeerSide == "left" && c2.Port == 12345 && c2.Password == "abc123");
        }
        catch (Exception ex) { Check("配置存取+DPAPI", false, ex.Message); }

        // 3. TLS + HMAC 认证 + 剪贴板往返 + 文件传输（loopback）
        //    放到线程池执行：在 WPF UI 线程上直接 GetResult() 会与 await 续延死锁
        try
        {
            Task.Run(RunLinkTest).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Check("TLS+认证+剪贴板+文件传输", false, ex.Message); }

        // 4. 错误密码必须被拒绝
        try
        {
            Task.Run(RunBadPasswordTest).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Check("错误密码拒绝", false, ex.Message); }

        // 5. 文件清单扫描（含目录递归、路径安全拼接）
        try { RunScannerTest(); }
        catch (Exception ex) { Check("文件清单扫描", false, ex.Message); }

        // 6. 本机地址识别：扫描/连接都要能认出"这是我自己"，否则会连到自己或把自己列成对端
        try
        {
            bool loopback = Discovery.IsLocal(System.Net.IPAddress.Loopback);
            bool pub = Discovery.IsLocal(System.Net.IPAddress.Parse("8.8.8.8"));
            Check("本机地址识别（排除自连/自发现）", loopback && !pub, $"loopback={loopback} 公网={pub}");
        }
        catch (Exception ex) { Check("本机地址识别（排除自连/自发现）", false, ex.Message); }

        // 7. 拖拽投递目标解析：无论光标在哪，都必须给出一个真实存在的目录
        try { RunDropTargetTest(); }
        catch (Exception ex) { Check("投递目标解析", false, ex.Message); }

        // 7b. 传输进度的分母只统计真实可发字节（否则会"传完了还显示传输中"）
        try { RunXferTotalTest(); }
        catch (Exception ex) { Check("传输进度分母", false, ex.Message); }

        // 8. 传输台账（记录/进度/完成/落盘）与运行日志增量读取
        try { RunJournalTest(); }
        catch (Exception ex) { Check("传输台账", false, ex.Message); }

        // 恢复原配置（无则删除测试残留）
        try
        {
            if (origCfg != null) File.WriteAllText(AppPaths.ConfigFile, origCfg);
            else if (File.Exists(AppPaths.ConfigFile)) File.Delete(AppPaths.ConfigFile);
        }
        catch { }

        Console.WriteLine($"自检完成：通过 {_pass}，失败 {_fail}");
        Log.Info($"SELFTEST 完成 pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>传输台账：记录 → 进度 → 完成，并确认确实落盘；最后清掉自检记录。</summary>
    static void RunJournalTest()
    {
        const long testId = -987654321L;
        try
        {
            TransferJournal.Remove(testId);
            TransferJournal.Begin(testId, true, 1024 * 1024, 3, "发送 3 个文件", "SELFTEST", "自检");
            TransferJournal.Progress(testId, 512 * 1024, 1024 * 1024);
            var mid = TransferJournal.Snapshot().FirstOrDefault(r => r.Id == testId);
            bool running = mid is { State: XferState.Running } && mid.DoneBytes == 512 * 1024
                           && mid.Progress > 0.49 && mid.Progress < 0.51;

            TransferJournal.Finish(testId, XferState.Done, "→ SELFTEST");
            var end = TransferJournal.Snapshot().FirstOrDefault(r => r.Id == testId);
            bool done = end is { State: XferState.Done } && end.Target == "→ SELFTEST";

            Check("传输台账 记录/进度/完成", running && done,
                $"running={running} done={done} 进度={mid?.Progress:F2}");

            string body = File.Exists(TransferJournal.FilePath)
                ? File.ReadAllText(TransferJournal.FilePath) : "";
            Check("传输台账 落盘 JSONL", body.Contains(testId.ToString()) && body.Contains("SELFTEST"));
        }
        catch (Exception ex) { Check("传输台账 记录/进度/完成", false, ex.Message); }
        finally
        {
            try { TransferJournal.Remove(testId); } catch { }
        }

        try
        {
            var tail = new LogTail(200);
            tail.Poll();
            var lines = tail.Tail(50, LogLevel.Debug);
            Check("运行日志增量读取", tail.CurrentFile != null && lines.Count > 0,
                $"file={Path.GetFileName(tail.CurrentFile ?? "-")} lines={lines.Count}");
        }
        catch (Exception ex) { Check("运行日志增量读取", false, ex.Message); }
    }

    static void RunScannerTest()    {
        var root = Path.Combine(Path.GetTempPath(), "mb-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub", "deep"));
        File.WriteAllBytes(Path.Combine(root, "a.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "sub", "b.bin"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(root, "sub", "deep", "c.dat"), new byte[777]);
        try
        {
            var (items, total, files) = FileScanner.Expand(new[] { root });
            bool ok = items.Count == 6 && files == 3 && total == 2877
                      && items.Any(i => i.IsDir && i.Rel.EndsWith("/sub"))
                      && items.Any(i => i.Rel.EndsWith("/sub/deep/c.dat") && !i.IsDir);
            Check("文件清单扫描（目录递归）", ok, $"items={items.Count} files={files} total={total}");

            var entries = FileScanner.ToEntries(items);
            Check("清单顶层条目", entries.Count == 1 && entries[0].N == Path.GetFileName(root) + "/",
                string.Join(",", entries.Select(e => e.N)));

            var joined = FileScanner.SafeJoin(root, "sub/deep/../../evil.txt");
            Check("相对路径越权被拦", joined.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !joined.Contains(".."), joined);

            // 协议字段（IsDir / RootIdx）编解码
            var frame = Proto.Encode(new Msg { T = "file_hdr", Id = 7, Path1 = "sub/x", IsDir = true, RootIdx = 3, Size = 9 }, null);
            var (m2, _) = Proto.Decode(frame);
            Check("协议新增字段编解码", m2.IsDir && m2.RootIdx == 3 && m2.Path1 == "sub/x");

            // 剪贴板「以最新为准」时间戳字段要能往返（老对端不带则为 0）
            var cf = Proto.Encode(new Msg { T = "clip_meta", Id = 8, Kind = "text", Stamp = 12345 }, null);
            var (cm, _) = Proto.Decode(cf);
            var (old, _) = Proto.Decode(Proto.Encode(new Msg { T = "clip_meta", Id = 9, Kind = "text" }, null));
            Check("剪贴板时间戳字段编解码", cm.Stamp == 12345 && old.Stamp == 0 && cm.Kind == "text",
                $"stamp={cm.Stamp} 旧对端={old.Stamp}");

            // 两侧复制粘贴共用：Ctrl+V 时向对端索取的 clip_req 报文要能往返，
            // 并且带着"本机手上那份的时间戳"，对端据此判断要不要补发。
            var rq = Proto.Encode(new Msg { T = "clip_req", Stamp = 777 }, null);
            var (rm, rp) = Proto.Decode(rq);
            Check("剪贴板索取报文编解码", rm.T == "clip_req" && rm.Stamp == 777 && rp == null,
                $"t={rm.T} stamp={rm.Stamp}");

            // 反向控制授权报文（角色切换需授权）也要能往返
            var rv = Proto.Encode(new Msg { T = "rev_req", Name = "PEER", Ok = true }, null);
            var (vm, _) = Proto.Decode(rv);
            Check("反向控制报文编解码", vm.T == "rev_req" && vm.Name == "PEER" && vm.Ok,
                $"t={vm.T} name={vm.Name}");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// 文件传输进度的"分母"必须等于实际能发出的字节数。
    /// 用含"打不开的文件"的清单验证：旧写法会把它们算进分母，导致进度永远差最后一点、
    /// 传完了还显示传输中（用户反馈的现象）。
    /// </summary>
    static void RunXferTotalTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mb_xfer_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var real = Path.Combine(dir, "a.bin");
            File.WriteAllBytes(real, new byte[64 * 1024]);
            var gone = Path.Combine(dir, "gone.bin");   // 不存在，用来模拟"清单里有但打不开"

            var items = new List<FileScanner.Item>
            {
                new() { Root = real, Rel = "a.bin", Size = 64 * 1024, RootIndex = 0 },
                new() { Root = gone, Rel = "gone.bin", Size = 999 * 1024, RootIndex = 0 },
            };
            long naive = items.Sum(i => i.Size);          // 旧写法
            long realTotal = 0;
            foreach (var it in items)
            {
                try { using var fs = new FileStream(it.Root, FileMode.Open, FileAccess.Read, FileShare.Read); realTotal += fs.Length; }
                catch { }
            }
            Check("传输进度分母只含真实可发字节", realTotal == 64 * 1024 && naive > realTotal,
                $"real={realTotal} naive={naive}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void RunDropTargetTest()
    {
        var cfg = new Config { DropDir = "Desktop" };
        var t = ExplorerHelper.ResolveDropTarget(cfg);
        Check("投递目标解析（回退可用）", t.Dir.Length > 0 && Directory.Exists(t.Dir) && t.Label.Length > 0,
            $"dir={t.Dir} real={t.IsRealFolder}");

        var desktop = ExplorerHelper.DesktopDir();
        Check("桌面目录可解析", desktop.Length > 0 && Directory.Exists(desktop), desktop);
    }

    static async Task<T?> WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var v = probe();
            if (v != null) return v;
            await Task.Delay(50);
        }
        return probe();
    }

    static async Task RunLinkTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var cert = CertProvider.GetOrCreate();

        // 文件传输的测试数据（服务端任务里要用其大小，提前准备）
        var src = Path.Combine(Path.GetTempPath(), "mb-selftest-" + Guid.NewGuid().ToString("N") + ".bin");
        var content = Rand.Bytes(3 * 1024 * 1024 + 137);
        File.WriteAllBytes(src, content);
        var expectSha = Hex.Of(Hashing.Sha1(content));

        // 再加一个小目录，验证"目录能连层级一起传"
        var dirPath = Path.Combine(Path.GetTempPath(), "mb-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dirPath, "sub"));
        File.WriteAllBytes(Path.Combine(dirPath, "sub", "b.bin"), new byte[4096]);
        var (items, totalBytes, _) = FileScanner.Expand(new[] { src, dirPath });

        // 服务端（主控端角色）：收剪贴板文本 + 收文件
        var textTcs = new TaskCompletionSource<string>();
        var doneTcs = new TaskCompletionSource<List<string>>();
        FileTransferService? serverXfer = null;   // 暂停/取消用例要在服务端会话外操纵传输服务
        var server = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            var link = await SecureLink.AcceptAsync(tcp, true, cert);
            // 自检里没有真实用户，确认环节由服务端一侧直接放行（drivesConfirm 为 true 的那端负责）
            var ok = await link.HandshakeAsync(true, "pw123", "SRV", AppInfo.Version,
                new DispInfo(0, 0, 1920, 1080), "right", drivesConfirm: _ => true);
            if (!ok) { textTcs.TrySetException(new IOException("服务端认证失败")); return; }

            var xfer = new FileTransferService((m, p) => link.SendAsync(m, p));
            serverXfer = xfer;
            link.OnMsg += (m, p) =>
            {
                if (m.T == "clip_data" && m.Kind == "text" && p != null)
                    textTcs.TrySetResult(Encoding.UTF8.GetString(p));
                xfer.OnMsg(m, p);
            };
            xfer.RecvDone += (_, paths) => doneTcs.TrySetResult(paths);
            xfer.BeginRecv(9, totalBytes); // 模拟 Orchestrator 收到 clip_meta 后开启接收会话
            link.Start();
        });

        // 客户端（被控端角色）：发剪贴板文本 + 发文件
        var link2 = await SecureLink.ConnectAsync("127.0.0.1", port, true);
        var ok2 = await link2.HandshakeAsync(false, "pw123", "CLI", AppInfo.Version,
            new DispInfo(0, 0, 2560, 1440), "", drivesConfirm: _ => false);
        if (!ok2) throw new IOException("客户端认证失败");
        Check("TLS+HMAC 认证握手", true);

        var clientXfer = new FileTransferService((m, p) => link2.SendAsync(m, p));
        link2.OnMsg += (m, p) => clientXfer.OnMsg(m, p);
        link2.Start();

        // 剪贴板文本往返
        await link2.SendAsync(new Msg { T = "clip_meta", Id = 1, Kind = "text", Size = 5 }, null);
        await link2.SendAsync(new Msg { T = "clip_data", Id = 1, Kind = "text" }, Encoding.UTF8.GetBytes("你好AB"));
        var gotText = await textTcs.Task.WithTimeout(TimeSpan.FromSeconds(5));
        Check("剪贴板文本往返", gotText == "你好AB", $"got={gotText}");


        await clientXfer.SendFilesAsync(9, items);
        var paths = await doneTcs.Task.WithTimeout(TimeSpan.FromSeconds(20));
        Check("文件传输完成", paths.Count == 2, $"paths={paths.Count} [{string.Join(",", paths)}]");
        var gotFile = paths.FirstOrDefault(p => File.Exists(p));
        if (gotFile != null)
        {
            var got = File.ReadAllBytes(gotFile);
            var gotSha = Hex.Of(Hashing.Sha1(got));
            Check("文件内容 SHA1 一致", gotSha == expectSha && got.Length == content.Length);
        }
        var gotDir = paths.FirstOrDefault(Directory.Exists);
        if (gotDir != null)
        {
            var nested = Path.Combine(gotDir, "sub", "b.bin");
            Check("目录层级还原", File.Exists(nested) && new FileInfo(nested).Length == 4096, nested);
        }
        else Check("目录层级还原", false, "没有收到目录");

        // —— 暂停 / 恢复 / 取消（需求1：传输框的暂停与停止按钮背后就是这三个动作）——
        var bigSrc = Path.Combine(Path.GetTempPath(), "mb-selftest-big-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(bigSrc, Rand.Bytes(32 * 1024 * 1024));
        var (bigItems, bigTotal, _) = FileScanner.Expand(new[] { bigSrc });

        // 暂停→恢复：先暂停再发起发送（确定性：一个块都发不出去），验证服务端进度 400ms 内纹丝不动，
        // 恢复后能正常传完。
        serverXfer = await WaitFor(() => serverXfer, TimeSpan.FromSeconds(3));
        if (serverXfer == null) throw new IOException("服务端传输服务未就绪");
        var recvDone2 = new TaskCompletionSource<List<string>>();
        serverXfer.RecvDone += (_, p2) => recvDone2.TrySetResult(p2);
        long serverGot = -1, serverGotAfterPause = -1;
        serverXfer.Progress += (_, g, _) => Interlocked.Exchange(ref serverGot, g);

        serverXfer.BeginRecv(10, bigTotal);
        clientXfer.PauseTransfer();
        var sendTask = Task.Run(() => clientXfer.SendFilesAsync(10, bigItems));
        await Task.Delay(400);
        serverGotAfterPause = Interlocked.CompareExchange(ref serverGot, -1, -1);
        Check("传输暂停后进度停滞", serverGotAfterPause <= 0, $"服务端已收 {serverGotAfterPause}B（应≈0）");
        clientXfer.ResumeTransfer();
        var paths2 = await recvDone2.Task.WithTimeout(TimeSpan.FromSeconds(30));
        Check("暂停后恢复传输完成", paths2.Count == 1, $"paths={paths2.Count}");

        // 取消：先暂停再发起发送（发不出去），随后取消 —— 100% 命中"传输中取消"路径
        //（loopback 速度太快，先发后取消会撞上"已经传完"的竞态）。
        // 取消会 UnpauseForCancel + ct.Cancel → 发送循环抛 OCE → 向服务端发 xfer_fail。
        var failedTcs = new TaskCompletionSource<string>();
        serverXfer.Failed += (_, r) => failedTcs.TrySetResult(r);
        serverXfer.BeginRecv(11, bigTotal);
        clientXfer.PauseTransfer();
        var sendTask2 = Task.Run(() => clientXfer.SendFilesAsync(11, bigItems));
        await Task.Delay(150);
        clientXfer.CancelSending();
        var failReason = await failedTcs.Task.WithTimeout(TimeSpan.FromSeconds(5));
        Check("传输取消", failReason.Contains("取消"), failReason);
        try { await sendTask; } catch { }
        try { await sendTask2; } catch { }
        try { File.Delete(bigSrc); } catch { }

        link2.Close();
        try { File.Delete(src); } catch { }
        try { Directory.Delete(dirPath, true); } catch { }
        try { listener.Stop(); } catch { }
    }

    static async Task RunBadPasswordTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cert = CertProvider.GetOrCreate();

        var serverTask = Task.Run(async () =>
        {
            var tcp = await listener.AcceptTcpClientAsync();
            var link = await SecureLink.AcceptAsync(tcp, true, cert);
            var ok = await link.HandshakeAsync(true, "right-pw", "SRV", AppInfo.Version, new DispInfo(0, 0, 100, 100), "right");
            link.Close();
            return ok;
        });

        var client = await SecureLink.ConnectAsync("127.0.0.1", port, true);
        var okClient = await client.HandshakeAsync(false, "wrong-pw", "CLI", AppInfo.Version, new DispInfo(0, 0, 100, 100), "");
        var okServer = await serverTask.WithTimeout(TimeSpan.FromSeconds(5));
        Check("错误密码被拒绝", !okClient && !okServer);

        try { listener.Stop(); } catch { }
    }
}
