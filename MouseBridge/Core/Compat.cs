using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MouseBridge.Core;

// ============================================================================
//  差异收敛层
//  ---------------------------------------------------------------------------
//  同一套源码要同时出两个包：
//    · net8.0-windows  —— 自包含单文件（Win10/11）
//    · net48           —— .NET Framework 4.8（Win7 SP1 / 32 位系统）
//  .NET Framework 4.8 只实现 .NET Standard 2.0，缺一批后来才加进 BCL 的 API
//  （Math.Clamp / Environment.TickCount64 / Convert.ToHexString / RandomNumberGenerator.GetBytes(int)
//   / CryptographicOperations.FixedTimeEquals / SHA1.HashData / Task.WaitAsync /
//   Stream.ReadAsync(Memory) / SslStream 的 *Options 异步重载 …）。
//  这里给每个缺口提供一份等价实现，调用方两边写法完全一致，不必到处 #if。
// ============================================================================

/// <summary>单调递增毫秒计数（net8 用 TickCount64，net48 用 Stopwatch）。
/// 不叫 Clock：会和 System.Windows.Media.Animation.Clock 撞名。</summary>
internal static class Monotonic
{
#if NETFRAMEWORK
    static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static long Ms => Sw.ElapsedMilliseconds;
#else
    public static long Ms => Environment.TickCount64;
#endif
}

/// <summary>数值钳制（net48 没有 Math.Clamp）。</summary>
internal static class MathX
{
    public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    public static long Clamp(long v, long min, long max) => v < min ? min : (v > max ? max : v);
    public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
}

/// <summary>十六进制串（net48 没有 Convert.ToHexString）。</summary>
internal static class Hex
{
    public static string Of(byte[] data) => Of(data, 0, data.Length);

    public static string Of(byte[] data, int offset, int count)
    {
        if (data == null) return "";
        if (offset < 0) { count += offset; offset = 0; }
        if (count <= 0) return "";
        var sb = new StringBuilder(count * 2);
        for (int i = 0; i < count; i++) sb.Append(data[offset + i].ToString("X2"));
        return sb.ToString();
    }
}

/// <summary>Channel 读取（net48 的 System.Threading.Channels 没有 ReadAllAsync 扩展）。</summary>
internal static class ChannelEx
{
    public static async IAsyncEnumerable<T> ReadAllEx<T>(
        this System.Threading.Channels.ChannelReader<T> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item)) yield return item;
        }
    }
}

/// <summary>字符串小工具（net48 只有 Contains(string)，没有 Contains(char)）。</summary>
internal static class Str
{
    public static bool Has(string s, char c) => s.IndexOf(c) >= 0;
}

/// <summary>哈希（net48 没有 SHA1.HashData 这类静态便捷方法）。</summary>
internal static class Hashing
{
    public static byte[] Sha1(byte[] data)
    {
#if NETFRAMEWORK
        using var h = SHA1.Create();
        return h.ComputeHash(data);
#else
        return SHA1.HashData(data);
#endif
    }
}

/// <summary>密码学小工具。</summary>
internal static class Crypto
{
    /// <summary>等时比较，防时序侧信道（net48 没有 CryptographicOperations）。</summary>
    public static bool FixedTimeEquals(byte[] a, byte[] b)
    {
#if NETFRAMEWORK
        if (a == null || b == null || a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
#else
        return CryptographicOperations.FixedTimeEquals(a, b);
#endif
    }

    /// <summary>生成 RSA 密钥（net48 的 RSA.Create() 默认只有 1024 位，必须显式给位数）。</summary>
    public static RSA NewRsa(int bits)
    {
#if NETFRAMEWORK
        return new RSACryptoServiceProvider(bits);
#else
        return RSA.Create(bits);
#endif
    }
}

/// <summary>强随机字节（net48 没有静态 RandomNumberGenerator.GetBytes(int)）。</summary>
internal static class Rand
{
    public static byte[] Bytes(int count)
    {
#if NETFRAMEWORK
        var b = new byte[count];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        return b;
#else
        return RandomNumberGenerator.GetBytes(count);
#endif
    }
}

/// <summary>当前可执行文件路径（net48 没有 Environment.ProcessPath）。</summary>
internal static class AppExe
{
    public static string Path
    {
        get
        {
#if NETFRAMEWORK
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return ""; }
#else
            return Environment.ProcessPath ?? "";
#endif
        }
    }
}

/// <summary>Task 超时/取消（net8 的 Task.WaitAsync 在 net48 上不存在）。</summary>
internal static class TaskEx
{
    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny((Task)task, Task.Delay(timeout)).ConfigureAwait(false) != (Task)task)
            throw new TimeoutException("操作超时");
        return await task.ConfigureAwait(false);
    }

    public static async Task<T> WithToken<T>(this Task<T> task, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return await task.ConfigureAwait(false);
        var tcs = new TaskCompletionSource<bool>();
        using (ct.Register(() => tcs.TrySetResult(true)))
        {
            if (await Task.WhenAny((Task)task, tcs.Task).ConfigureAwait(false) != (Task)task)
                throw new OperationCanceledException(ct);
        }
        return await task.ConfigureAwait(false);
    }

    // TcpClient.ConnectAsync 返回的是非泛型 Task，需要单独一组重载
    public static async Task WithTimeout(this Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
            throw new TimeoutException("操作超时");
        await task.ConfigureAwait(false);
    }

    public static async Task WithToken(this Task task, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) { await task.ConfigureAwait(false); return; }
        var tcs = new TaskCompletionSource<bool>();
        using (ct.Register(() => tcs.TrySetResult(true)))
        {
            if (await Task.WhenAny(task, tcs.Task).ConfigureAwait(false) != task)
                throw new OperationCanceledException(ct);
        }
        await task.ConfigureAwait(false);
    }
}

/// <summary>Stream 读取（net8 的 ReadAsync(Memory&lt;byte&gt;) 重载在 net48 上不存在）。</summary>
internal static class StreamEx
{
    public static Task<int> ReadAsync(Stream s, byte[] buf, int offset, int count, CancellationToken ct)
    {
#if NETFRAMEWORK
        return s.ReadAsync(buf, offset, count, ct);
#else
        return s.ReadAsync(buf.AsMemory(offset, count), ct).AsTask();
#endif
    }
}

/// <summary>SslStream 异步认证（net48 用的是老式参数重载，不是 *Options 对象）。</summary>
internal static class SslEx
{
#if NETFRAMEWORK
    // Win7 的 SChannel 没有 TLS 1.3，TLS 1.2 需要 KB3140245。这里 1.2 起、1.1 兜底；
    // 真正的身份认证靠 HMAC 密码挑战应答，TLS 只负责把载荷加密。
    const System.Security.Authentication.SslProtocols Protocols =
        System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls11;
#else
    const System.Security.Authentication.SslProtocols Protocols =
        System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
#endif

    public static Task AuthenticateClientAsync(System.Net.Security.SslStream ssl, string targetHost)
    {
#if NETFRAMEWORK
        return ssl.AuthenticateAsClientAsync(targetHost, null, Protocols, false);
#else
        return ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = Protocols,
        });
#endif
    }

    public static Task AuthenticateServerAsync(System.Net.Security.SslStream ssl, X509Certificate2 cert)
    {
#if NETFRAMEWORK
        return ssl.AuthenticateAsServerAsync(cert, false, Protocols, false);
#else
        return ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
        });
#endif
    }
}
