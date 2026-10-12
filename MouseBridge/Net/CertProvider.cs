using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MouseBridge.Net;

/// <summary>首次运行时生成自签名证书（TLS 用），PFX 以 DPAPI 加密存放在用户目录。</summary>
public static class CertProvider
{
    static X509Certificate2? _cached;
    static readonly object Gate = new();

    /// <summary>
    /// 取证书，没有就生成。
    ///
    /// 全流程加锁：GetOrCreate 会被 ServerLoop / ClientLoop 同时调用（两循环无条件并行启动），
    /// 首启竞态下两个线程会**各生成一张证书**，然后一个写盘、另一个也写盘 —— 后写的覆盖前写的，
    /// 而先返回的那个线程手里还攥着已经被覆盖掉的那张。两端各拿一张不同的自签名证书，
    /// TLS 握手直接失败。加锁后只有一个线程真正生成，其余复用同一实例。
    /// </summary>
    public static X509Certificate2 GetOrCreate()
    {
        if (_cached != null) return _cached;
        lock (Gate)
        {
            if (_cached != null) return _cached;   // 双检：等锁期间可能已被别人建好
            try
            {
                if (File.Exists(AppPaths.CertFile))
                {
                    var prot = File.ReadAllBytes(AppPaths.CertFile);
                    var pfx = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
                    _cached = new X509Certificate2(pfx, "mb-local");
                    Log.Info($"已加载 TLS 证书: {_cached.Thumbprint}");
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取证书失败，将重新生成", ex);
            }

            var rsa = Crypto.NewRsa(2048);
            var req = new CertificateRequest("CN=MouseBridge", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            var pfxBytes = cert.Export(X509ContentType.Pkcs12, "mb-local");
            var protBytes = ProtectedData.Protect(pfxBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AppPaths.CertFile, protBytes);
            _cached = new X509Certificate2(pfxBytes, "mb-local");
            Log.Info($"已生成 TLS 证书: {_cached.Thumbprint}");
            return _cached;
        }
    }
}
