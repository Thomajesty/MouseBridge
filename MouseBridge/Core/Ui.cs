using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;

namespace MouseBridge.Core;

public static class UiUtil
{
    /// <summary>把动作调度到 WPF UI 线程（无 Application 时直接执行，供自检用）</summary>
    public static void Run(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) { try { a(); } catch (Exception ex) { Log.Error("Ui.Run", ex); } return; }
        if (d.CheckAccess()) { try { a(); } catch (Exception ex) { Log.Error("Ui.Run", ex); } }
        else _ = d.BeginInvoke(a, DispatcherPriority.Normal);
    }

    public static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return;
            if (on) k.SetValue("MouseBridge", $"\"{AppExe.Path}\"");
            else k.DeleteValue("MouseBridge", false);
        }
        catch (Exception ex) { Log.Error("设置开机自启失败", ex); }
    }

    public static void SetFirewallRule()
    {
        var exe = AppExe.Path;
        var psi = new System.Diagnostics.ProcessStartInfo(
            "cmd.exe",
            $"/c netsh advfirewall firewall delete rule name=\"MouseBridge\" >nul 2>&1 & netsh advfirewall firewall add rule name=\"MouseBridge\" dir=in action=allow program=\"{exe}\" enable=yes")
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        System.Diagnostics.Process.Start(psi);
    }
}
