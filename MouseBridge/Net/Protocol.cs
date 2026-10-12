using System.IO;
using System.Text;
using System.Text.Json;

namespace MouseBridge.Net;

public record DispInfo(int X, int Y, int W, int H);

public class FEntry
{
    public string N { get; set; } = "";
    public long S { get; set; }
}

/// <summary>通用消息头（JSON），二进制载荷（剪贴板内容/文件块）跟随其后。</summary>
public class Msg
{
    public string T { get; set; } = "";
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Ver { get; set; }
    /// <summary>对端角色：server=主控端 / client=被控端（用于两端角色相同时的仲裁）。</summary>
    public string? Role { get; set; }
    /// <summary>对端计算机名（DisplayName 可能被用户改重，机器名才是唯一可靠的仲裁依据）。</summary>
    public string? Machine { get; set; }

    // 显示器信息（虚拟桌面）
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    // 认证
    public string? Nonce { get; set; }
    public string? Hmac { get; set; }
    public bool Ok { get; set; }
    public string? MsgText { get; set; }
    public string? Side { get; set; }

    // 鼠标/键盘
    public double Nx { get; set; }
    public double Ny { get; set; }
    public int Btn { get; set; }
    public bool Down { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Vk { get; set; }
    public int Scan { get; set; }
    public bool Ext { get; set; }
    public bool Up { get; set; }

    // 剪贴板/文件
    public string? Kind { get; set; }
    public long Size { get; set; }
    public List<FEntry>? Files { get; set; }
    public bool Eager { get; set; }
    public bool Drop { get; set; }
    public int Idx { get; set; }
    public int RootIdx { get; set; } = -1;
    public bool IsDir { get; set; }
    public string? Path1 { get; set; }
    public List<string>? Paths { get; set; }
    public bool On { get; set; }
    public bool Locked { get; set; }
    public int Count { get; set; }

    /// <summary>
    /// 剪贴板消息的"本端单调时刻"（毫秒，随便一个自增基准即可）。
    /// 接收方用它做「以最新为准」的比较：只有对端这次复制比本机最近一次本地复制更晚，
    /// 才允许覆盖本机剪贴板 —— 否则用户刚在本机 Ctrl+C 会被对端旧内容冲掉。
    /// 老版本对端不带该字段时为 0（等于"很旧"），保持向后兼容。
    /// </summary>
    public long Stamp { get; set; }
}

public static class Proto
{
    /// <summary>注入事件标记（extraInfo），用于钩子区分"我们自己注入的事件"，避免回环。</summary>
    public const int Magic = 0x4D425244; // "MBRD"

    public const int ChunkSize = 256 * 1024;

    /// <summary>
    /// 单帧硬上限（含头 + JSON 头 + 载荷）。超过会在 <see cref="ReadFrameSize"/> 直接抛异常断链，
    /// 所以上层（剪贴板容量校验）必须把它当作真实可用载荷的上界来算，而不是各判各的。
    /// </summary>
    public const int MaxFrame = 64 * 1024 * 1024;

    public static byte[] Encode(Msg m, byte[]? payload)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(m);
        int total = 2 + header.Length + (payload?.Length ?? 0);
        var buf = new byte[4 + total];
        buf[0] = (byte)(total >> 24); buf[1] = (byte)(total >> 16); buf[2] = (byte)(total >> 8); buf[3] = (byte)total;
        buf[4] = (byte)(header.Length >> 8); buf[5] = (byte)header.Length;
        Buffer.BlockCopy(header, 0, buf, 6, header.Length);
        if (payload != null) Buffer.BlockCopy(payload, 0, buf, 6 + header.Length, payload.Length);
        return buf;
    }

    public static (Msg, byte[]?) Decode(byte[] frame)
    {
        if (frame.Length < 6) throw new InvalidDataException("frame too short");
        int hlen = (frame[4] << 8) | frame[5];
        if (hlen <= 0 || 6 + hlen > frame.Length) throw new InvalidDataException("bad header len");
        // net48 没有 JsonSerializer 的 ReadOnlySpan<byte> 重载，统一先转字符串
        var m = JsonSerializer.Deserialize<Msg>(Encoding.UTF8.GetString(frame, 6, hlen))
                ?? throw new InvalidDataException("bad header");
        int plen = frame.Length - 6 - hlen;
        byte[]? payload = null;
        if (plen > 0)
        {
            payload = new byte[plen];
            Buffer.BlockCopy(frame, 6 + hlen, payload, 0, plen);
        }
        return (m, payload);
    }

    public static int ReadFrameSize(ReadOnlySpan<byte> prefix)
    {
        uint total = ((uint)prefix[0] << 24) | ((uint)prefix[1] << 16) | ((uint)prefix[2] << 8) | prefix[3];
        if (total > MaxFrame || total < 6) throw new InvalidDataException($"bad frame size {total}");
        return (int)total;
    }
}
