using System.Windows.Interop;

namespace ClaudeWidget.Native;

/// <summary>
/// Globalny skrót klawiszowy zarejestrowany na oknie widżetu: odbiera WM_HOTKEY nawet gdy okno
/// jest ukryte. Port WidgetHotkey z widget.ps1 (tam osobne ukryte okno WinForms; tu ten sam hwnd
/// wystarczy, bo WPF daje dostęp do komunikatów przez HwndSource).
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;

    public event Action? Pressed;

    public bool Registered { get; }

    public GlobalHotkey(HwndSource source, uint modifiers, uint key)
    {
        _source = source;
        _source.AddHook(WndProc);
        Registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, key);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Registered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WndProc);
    }
}
