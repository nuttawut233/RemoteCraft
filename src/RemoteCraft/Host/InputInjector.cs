using System.Runtime.InteropServices;

namespace RemoteCraft.Host;

public enum MouseKind : byte { Move = 0, Down = 1, Up = 2, Wheel = 3, HWheel = 4 }
public enum MouseBtn : byte { None = 0, Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5 }

public enum RemoteAction : byte { Lock = 1, CtrlAltDel = 2, TaskManager = 3, WinKey = 4, AltTab = 5, RefreshScreen = 6 }

/// <summary>Replays the viewer's mouse and keyboard on this desk with SendInput.</summary>
public static class InputInjector
{
    static readonly int Size = Marshal.SizeOf<Native.INPUT>();

    public static void Mouse(MouseKind kind, MouseBtn btn, int screenX, int screenY, int delta)
    {
        var vs = SystemInformation.VirtualScreen;
        int nx = (int)((screenX - vs.Left) * 65535L / Math.Max(1, vs.Width - 1));
        int ny = (int)((screenY - vs.Top) * 65535L / Math.Max(1, vs.Height - 1));
        uint flags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
        uint data = 0;
        switch (kind)
        {
            case MouseKind.Down:
            case MouseKind.Up:
                bool down = kind == MouseKind.Down;
                switch (btn)
                {
                    case MouseBtn.Left: flags |= down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
                    case MouseBtn.Right: flags |= down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
                    case MouseBtn.Middle: flags |= down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
                    case MouseBtn.X1: flags |= down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; break;
                    case MouseBtn.X2: flags |= down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; break;
                }
                break;
            case MouseKind.Wheel: flags |= Native.MOUSEEVENTF_WHEEL; data = unchecked((uint)delta); break;
            case MouseKind.HWheel: flags |= Native.MOUSEEVENTF_HWHEEL; data = unchecked((uint)delta); break;
        }
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = nx, dy = ny, mouseData = data, dwFlags = flags } }
        };
        Native.SendInput(1, new[] { input }, Size);
    }

    /// <summary>
    /// Sends a key by scan code when one is known, so the host's own keyboard layout
    /// decides the character (the same keys produce the same result as typing locally).
    /// </summary>
    public static void Key(ushort vk, ushort scan, bool extended, bool up)
    {
        uint flags = (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0);
        if (scan != 0) flags |= Native.KEYEVENTF_SCANCODE;
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = scan != 0 ? (ushort)0 : vk, wScan = scan, dwFlags = flags } }
        };
        Native.SendInput(1, new[] { input }, Size);
    }

    static void Chord(params ushort[] vks)
    {
        foreach (var vk in vks) KeyVk(vk, false);
        foreach (var vk in vks.Reverse()) KeyVk(vk, true);
    }

    static void KeyVk(ushort vk, bool up)
    {
        bool ext = vk is 0x5B or 0x5C; // Win keys are extended
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (ext ? Native.KEYEVENTF_EXTENDEDKEY : 0) } }
        };
        Native.SendInput(1, new[] { input }, Size);
    }

    public static void Run(RemoteAction action)
    {
        switch (action)
        {
            case RemoteAction.Lock: Native.LockWorkStation(); break;
            // Works only where the SoftwareSASGeneration policy allows apps to send Ctrl+Alt+Del.
            case RemoteAction.CtrlAltDel: try { Native.SendSAS(true); } catch { } break;
            case RemoteAction.TaskManager: Chord(0x11, 0x10, 0x1B); break; // Ctrl+Shift+Esc
            case RemoteAction.WinKey: Chord(0x5B); break;
            case RemoteAction.AltTab: Chord(0x12, 0x09); break;
        }
    }
}
