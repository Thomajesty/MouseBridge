using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseBridge.Core;

public class Config
{
    public bool Configured { get; set; }

    /// <summary>本机角色：server=主控端（键鼠所在，往外推鼠标），client=被控端</summary>
    public string Role { get; set; } = "server";

    public int Port { get; set; } = 39501;
    public bool TlsEnabled { get; set; } = true;
    public string PasswordProt { get; set; } = "";
    public bool AutoDiscover { get; set; } = true;

    /// <summary>client 角色手动填写的对端地址，格式 host:port（互联网/跨网段用）</summary>
    public string PeerHost { get; set; } = "";

    /// <summary>对端屏幕位于本机的哪一侧（仅主控端需要配置）：right/left/top/bottom</summary>
    public string PeerSide { get; set; } = "right";

    /// <summary>鼠标顶住屏幕边缘持续多少毫秒后切换（0=立即），防误触</summary>
    public int SwitchDelayMs { get; set; } = 0;

    /// <summary>启用 Scroll Lock 键 = 锁定到当前屏幕（不切换）</summary>
    public bool LockKeyEnabled { get; set; } = true;

    /// <summary>跨机热键：默认 Ctrl+Alt 组合 —— 2 跳到对端 / 1 拉回本机 / L 锁屏 / D 暂停接管。</summary>
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>
    /// 跨机热键的修饰键组合（C2 可自定义）。
    ///
    /// 为什么需要：Ctrl+Alt+1/2/L/D 会和不少软件冲突（输入法、IDE、远程桌面、游戏宏）。
    /// 用户以前只能整体关掉热键，等于把"保命"的 Ctrl+Alt+1 也一起关了。改成可选组合后，
    /// 冲突时换一套即可，功能不丢。
    ///
    /// 取值："CtrlAlt"（默认）| "CtrlShift" | "AltShift"。
    /// 注意：**无论选哪套，"拉回本机"永远可用**（OnHookKey 里的保命分支不受此设置影响，
    /// 但会同时识别当前选定的组合，避免用户改了组合后按默认键反而没反应）。
    /// </summary>
    public string HotkeyMods { get; set; } = "CtrlAlt";

    /// <summary>总开关（关掉=完全不接管键鼠，仅保留文件同步）</summary>
    public bool TakeoverEnabled { get; set; } = true;

    /// <summary>
    /// 主控端：「允许被控端反向控制本机」许可开关（默认关，安全优先）。
    ///
    /// 为什么要它：正常情况下只有主控端能把键鼠推到对端。但很多时候用户会坐在
    /// 被控端那台电脑前（比如被控端就在手边），此时想反过来去操作主控端 ——
    /// 以前要么改角色重启连接、要么没辙。打开这个开关后，被控端可以**申请**
    /// 反向控制，由主控端确认（或按"许可"直接放行）。
    ///
    /// 控制权归属始终由"哪个鼠标/键盘在动"决定：谁的物理鼠标一动，控制权就回到谁手上。
    /// </summary>
    public bool AllowReverseControl { get; set; } = false;

    /// <summary>
    /// 反向控制是否需要人工确认。false（默认）= 主控端开了许可就直接放行；
    /// true = 每次被控端申请反向控制时，主控端都要点一次「允许」。
    /// </summary>
    public bool ConfirmReverseControl { get; set; } = true;

    /// <summary>本机显示名（留空则用计算机名，用于指示器与对端识别）</summary>
    public string MachineLabel { get; set; } = "";

    /// <summary>
    /// 被控端：是否已经「启动服务」。关掉后监听仍在，但会直接拒绝进来的连接
    /// （这样用户能明确地"关掉被别人连"的可能，而不用退出软件）。
    /// </summary>
    public bool ServiceOn { get; set; } = true;

    /// <summary>
    /// 被控端：主控端连进来时是否需要人工点「允许」。
    /// 打开后，密码正确也要在本机确认一次才会建立链路 —— 避免被同网段的人悄悄接管。
    /// </summary>
    public bool ConfirmOnConnect { get; set; } = true;

    public bool SyncText { get; set; } = true;
    public bool SyncImage { get; set; } = true;
    public bool SyncFiles { get; set; } = true;

    /// <summary>对端发来文件时是否自动预接收（粘贴时秒贴）</summary>
    public bool EagerReceive { get; set; } = true;
    public long EagerCapMB { get; set; } = 256;
    public long ClipCapMB { get; set; } = 2048;

    /// <summary>拖拽投递的接收目录："Desktop" | "Downloads" | 绝对路径</summary>
    public string DropDir { get; set; } = "Desktop";
    public bool OpenFolderAfterDrop { get; set; } = true;

    /// <summary>真·跨机拖拽：按住文件推过屏幕边缘后继续拖，松手落到对端光标所在文件夹</summary>
    public bool DragDropAcrossMachines { get; set; } = true;

    /// <summary>松手时不在文件夹上：true=落到默认接收目录，false=取消本次投递</summary>
    public bool DropFallbackToDefaultDir { get; set; } = true;

    public bool IndicatorEnabled { get; set; } = true;
    /// <summary>光环直径（像素）</summary>
    public int IndicatorSize { get; set; } = 44;
    /// <summary>光环下显示文字状态（拖拽/接收/可投递等）</summary>
    public bool IndicatorText { get; set; } = true;
    /// <summary>传输时在光环上画进度弧</summary>
    public bool ShowProgressArc { get; set; } = true;
    public bool ShowBadge { get; set; } = true;

    public bool AutoStart { get; set; }
    public bool AutoStartMinimized { get; set; } = true;
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>client 端记住的服务端证书指纹（首次连接时记录，之后校验防中间人）</summary>
    public string PinnedServerThumb { get; set; } = "";

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>本机对外显示的名字。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(MachineLabel) ? Environment.MachineName : MachineLabel.Trim();

    [JsonIgnore]
    public string Password
    {
        get
        {
            try
            {
                if (string.IsNullOrEmpty(PasswordProt)) return "";
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(PasswordProt), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch (Exception ex)
            {
                // 静默返回空串会让用户看到"密码莫名其妙没了"，却查不到原因（多见于
                // 换了 Windows 账户 / 复制了别人的 config.json，DPAPI 解不开）。
                // 记一笔日志，至少能从日志里看出是解密失败而不是真没设过密码。
                Log.Warn("连接密码解密失败（可能换过系统账户或配置来自其他机器），按空密码处理：" + ex.Message);
                return "";
            }
        }
        set
        {
            try
            {
                if (string.IsNullOrEmpty(value)) { PasswordProt = ""; return; }
                var raw = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                PasswordProt = Convert.ToBase64String(raw);
            }
            catch (Exception ex)
            {
                Log.Error("连接密码加密失败，本次不保存密码", ex);
                PasswordProt = "";
            }
        }
    }

    public static Config Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(AppPaths.ConfigFile));
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex) { Log.Error("读取配置失败，使用默认配置", ex); }
        return new Config();
    }

    static readonly object SaveGate = new();

    /// <summary>
    /// 保存配置。
    ///
    /// 两处加固：
    ///   ① **原子写**：先写临时文件再 File.Replace/Move 覆盖。直接 File.WriteAllText 时
    ///      若在写入途中进程被杀（或断电），会留下一个**半截 JSON**，下次 Load 解析失败
    ///      就被当成"从没配过"，用户的密码/设置全丢。临时文件 + 替换能保证目标文件
    ///      要么是旧的完整内容、要么是新的完整内容。
    ///   ② **加锁**：保存可能从 UI 线程、托盘菜单、多个后台线程同时触发，
    ///      并发写同一文件会互相截断。串行化即可。
    /// </summary>
    public void Save()
    {
        lock (SaveGate)
        {
            AppPaths.Ensure();
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var target = AppPaths.ConfigFile;
            var tmp = target + ".tmp";
            try
            {
                File.WriteAllText(tmp, json);
                // File.Replace 需要目标存在；不存在时（首次保存）退化为 Move。
                if (File.Exists(target)) File.Replace(tmp, target, null);
                else File.Move(tmp, target);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>把对端方位翻译成中文（用于 UI 与日志）。</summary>
    public static string SideText(string? side) => side switch
    {
        "left" => "左侧",
        "top" => "上方",
        "bottom" => "下方",
        _ => "右侧",
    };
}
