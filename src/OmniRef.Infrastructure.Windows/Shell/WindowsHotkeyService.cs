using System.Runtime.InteropServices;
using System.Windows.Interop;
using OmniRef.Core.Interfaces;

namespace OmniRef.Infrastructure.Windows.Shell;

public sealed class WindowsHotkeyService : IHotkeyService
{
    private const int HotkeyId = 0x4F4D;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public bool Register(IntPtr windowHandle, HotkeyGesture gesture)
    {
        Unregister();
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WindowProc);

        var modifiers = ModNoRepeat;
        if (gesture.Alt)
        {
            modifiers |= ModAlt;
        }
        if (gesture.Control)
        {
            modifiers |= ModControl;
        }
        if (gesture.Shift)
        {
            modifiers |= ModShift;
        }
        if (gesture.Windows)
        {
            modifiers |= ModWin;
        }

        _registered = RegisterHotKey(windowHandle, HotkeyId, modifiers, (uint)gesture.VirtualKey);
        if (!_registered)
        {
            _source?.RemoveHook(WindowProc);
            _source = null;
            _windowHandle = IntPtr.Zero;
        }

        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WindowProc);
        _source = null;
        _windowHandle = IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
