using System.Runtime.InteropServices;
using MouseBridge.Net;

namespace MouseBridge.Input;

/// <summary>用 SendInput 把对端发来的输入事件注入本机。</summary>
public static class Injector
{
    static Native.INPUT MouseInput(uint flags, int dx, int dy, uint data) => new()
    {
        type = Native.INPUT_MOUSE,
        U = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = (IntPtr)Proto.Magic } },
    };

    static Native.INPUT KeyInput(int vk, int scan, bool ext, bool down) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)scan,
            dwFlags = (ext ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : Native.KEYEVENTF_KEYUP),
            dwExtraInfo = (IntPtr)Proto.Magic } },
    };

    static void Send(params Native.INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            Log.Debug($"SendInput 部分失败 {sent}/{inputs.Length}（可能目标窗口权限更高）");
    }

    /// <summary>绝对定位到虚拟桌面的归一化坐标 (0..1)。</summary>
    public static void MouseAbs(double nx, double ny)
    {
        nx = MathX.Clamp(nx, 0, 1); ny = MathX.Clamp(ny, 0, 1);
        Send(MouseInput(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
            (int)(nx * 65535), (int)(ny * 65535), 0));
    }

    public static void Button(int btn, bool down)
    {
        uint f = btn switch
        {
            0 => down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP,
            1 => down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP,
            2 => down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP,
            3 or 4 => down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP,
            _ => 0,
        };
        if (f == 0) return;
        uint data = btn is 3 or 4 ? (btn == 3 ? 1u : 2u) : 0;
        Send(MouseInput(f, 0, 0, data));
    }

    public static void Wheel(int dx, int dy)
    {
        if (dy != 0) Send(MouseInput(Native.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)dy)));
        if (dx != 0) Send(MouseInput(Native.MOUSEEVENTF_HWHEEL, 0, 0, unchecked((uint)dx)));
    }

    public static void Key(int vk, int scan, bool ext, bool down)
    {
        if (vk <= 0 && scan <= 0) return;
        Send(KeyInput(vk, scan, ext, down));
    }

    /// <summary>注入组合键（如 Ctrl+V），例如 Combo(0x11, 0x56)。</summary>
    public static void Combo(params int[] vks)
    {
        var downs = new Native.INPUT[vks.Length];
        var ups = new Native.INPUT[vks.Length];
        for (int i = 0; i < vks.Length; i++)
        {
            downs[i] = KeyInput(vks[i], 0, false, true);
            ups[i] = KeyInput(vks[i], 0, false, false);
        }
        Send(downs);
        Send(ups);
    }
}
