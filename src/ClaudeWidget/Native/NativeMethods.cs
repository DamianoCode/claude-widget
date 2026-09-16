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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;

    // Okna „zawsze na wierzchu” układają się między sobą w kolejności aktywacji: pełnoekranowy Pulpit
    // zdalny czy menedżer zadań przykrywają widżet, dopóki ten sam nie wróci na szczyt tej warstwy.
    public static void BringToTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);

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

    private const uint GaRootOwner = 3;
    private static readonly int[] StdHandles = [-10, -11, -12]; // wejście, wyjście, błędy
    private static readonly object ConsoleLock = new();
    private static readonly TimeSpan ConsoleLockTimeout = TimeSpan.FromMilliseconds(500);
    // W polu, żeby GC nie zebrał delegata, dopóki Windows może go wywołać.
    private static readonly ConsoleCtrlHandler IgnoreConsoleEvents = _ => true;
    private static bool _consoleHandlerRegistered;

    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    private static extern bool SetStdHandle(int which, IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>
    /// Okno terminala, w którym działa proces konsolowy: okno jego konsoli, a w Windows Terminal
    /// (ConPTY) — okno, do którego należy ukryte okno pseudokonsoli. Dokładne także wtedy, gdy jeden
    /// proces Windows Terminal ma kilka okien (tytuł okna to tylko aktywna karta) i gdy okno jest
    /// zminimalizowane. Zero, gdy się nie da: brak konsoli lub uprawnień, okno niewidoczne.
    /// </summary>
    public static IntPtr ConsoleOwnerWindow(int pid)
    {
        // Proces może mieć jedną konsolę naraz, więc podłączenia idą po kolei. Zawieszona konsola
        // (np. terminal, który przestał czytać) nie może zablokować wszystkich następnych — po chwili
        // czekania się rezygnuje, a okno wybiera się jak dawniej, po tytule.
        if (!Monitor.TryEnter(ConsoleLock, ConsoleLockTimeout)) return IntPtr.Zero;
        try
        {
            // Podłączona konsola wysyła procesowi swoje zdarzenia (Ctrl+C, Ctrl+Break), a domyślna obsługa
            // zamknęłaby widżet. Własna procedura, nie SetConsoleCtrlHandler(NULL): tamta flaga przechodzi
            // na procesy potomne, które przestałyby reagować na Ctrl+C.
            if (!_consoleHandlerRegistered) _consoleHandlerRegistered = SetConsoleCtrlHandler(IgnoreConsoleEvents, true);
            // AttachConsole podmienia uchwyty standardowe procesu, a FreeConsole ich nie przywraca —
            // procesy potomne dostałyby zamknięte (i potem ponownie użyte) uchwyty.
            var saved = StdHandles.Select(GetStdHandle).ToArray();
            FreeConsole();
            try
            {
                if (!AttachConsole((uint)pid)) return IntPtr.Zero;
                var console = GetConsoleWindow();
                if (console == IntPtr.Zero) return IntPtr.Zero;
                var root = GetAncestor(console, GaRootOwner);
                return root != IntPtr.Zero && IsWindowVisible(root) ? root : IntPtr.Zero;
            }
            finally
            {
                FreeConsole();
                for (var i = 0; i < StdHandles.Length; i++) SetStdHandle(StdHandles[i], saved[i]);
            }
        }
        finally
        {
            Monitor.Exit(ConsoleLock);
        }
    }
}
