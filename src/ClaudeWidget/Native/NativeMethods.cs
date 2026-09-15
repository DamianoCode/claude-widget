using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeWidget.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int Size;
    public Rect Monitor;
    public Rect Work;
    public uint Flags;
}

internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

/// <summary>P/Invoke widżetu: okno pierwszego planu, pełny ekran, okna procesu, globalny skrót. Port WidgetNative z widget.ps1.</summary>
internal static class NativeMethods
{
    public const uint MonitorDefaultToNearest = 2;
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    public const int SwRestore = 9;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public static string ClassOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, 256);
        return text.ToString();
    }

    public static string TitleOf(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, 512);
        return text.ToString();
    }

    public static uint ProcessOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, MonitorDefaultToNearest);

    // Pełny ekran: okno bez paska tytułu, które zakrywa cały monitor (prezentacja, film, gra).
    // Zmaksymalizowane okno ma pasek tytułu, więc się nie liczy — nawet przy ukrytym pasku zadań.
    public static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return false;
        if ((GetWindowLong(hwnd, GwlStyle) & WsCaption) == WsCaption) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorOf(hwnd), ref info)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    // Widoczne okno procesu. Terminal albo IDE bywa właścicielem kilku okien (VS Code trzyma wszystkie
    // w jednym procesie), więc wygrywa okno z tytułem pasującym do najpewniejszej wskazówki, a gdy
    // żadna nie pasuje — pierwsze okno.
    public static IntPtr FindWindowOf(uint pid, IReadOnlyList<string> titleHints)
    {
        var windows = new List<(IntPtr Hwnd, string Title)>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && ProcessOf(hwnd) == pid)
            {
                var title = TitleOf(hwnd);
                if (title.Length > 0) windows.Add((hwnd, title));
            }
            return true;
        }, IntPtr.Zero);
        if (windows.Count == 0) return IntPtr.Zero;
        foreach (var hint in titleHints)
        {
            foreach (var (hwnd, title) in windows)
            {
                if (title.Contains(hint, StringComparison.OrdinalIgnoreCase)) return hwnd;
            }
        }
        return windows[0].Hwnd;
    }
}
