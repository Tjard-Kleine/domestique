using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Domestique.App;

internal static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1;
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x3, WINEVENT_OUTOFCONTEXT = 0x0, WINEVENT_SKIPOWNPROCESS = 0x2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static WinEventProc? _onForeground;                     // Feld hält den Delegate am Leben, sonst räumt ihn der GC weg

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")] private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] char[] buffer, int size, uint flags, IntPtr layout);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    // Zeichen, das Strg+Alt (= AltGr) mit dieser Taste auf der aktuellen Tastatur tippt, z. B. „€“ für E auf deutschen
    // Tastaturen. Ein Kürzel darauf würde das Zeichen in allen Apps blockieren. null, wenn die Kombination nichts tippt.
    public static string? AltGrCharacter(uint vk, bool shift)
    {
        var layout = GetKeyboardLayout(0);
        var state = new byte[256];
        state[0x11] = state[0x12] = state[0xA2] = state[0xA5] = 0x80;          // Strg, Alt, linke Strg, rechte Alt (AltGr)
        if (shift) state[0x10] = 0x80;
        var buffer = new char[4];
        int n = ToUnicodeEx(vk, MapVirtualKeyEx(vk, 0, layout), state, buffer, buffer.Length, 0x4, layout);   // 0x4: Tastaturzustand nicht ändern
        if (n == 0) return null;
        var text = new string(buffer, 0, Math.Abs(n));
        return text.All(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? null : text;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    // Ganzer Bildschirm, auf dem das Fenster liegt, in WPF-Einheiten
    public static Rect MonitorBounds(Window w)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(Handle(w), 2), ref info))           // 2 = nächstgelegener Monitor
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        var toDip = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        return new Rect(toDip.Transform(new Point(info.Monitor.Left, info.Monitor.Top)),
                        toDip.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom)));
    }

    public static void SetClickThrough(Window w, bool on)
    {
        IntPtr h = Handle(w);
        int style = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
    }

    public static void ReassertTopmost(Window w) =>
        SetWindowPos(Handle(w), HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void KeepTopmost(Window w)
    {
        _onForeground = (_, _, _, _, _, _, _) => ReassertTopmost(w);
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onForeground, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }
}