using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OmniRef.Infrastructure.Windows.Shell;

public sealed class WindowsTrayIcon : IDisposable
{
    private const uint NotifyAdd = 0;
    private const uint NotifyDelete = 2;
    private const uint NotifyMessage = 0x00000001;
    private const uint NotifyIcon = 0x00000002;
    private const uint NotifyTip = 0x00000004;
    private const uint MenuString = 0x00000000;
    private const uint MenuSeparator = 0x00000800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const int CallbackMessage = 0x0400 + 0x4F;
    private const int LeftButtonUp = 0x0202;
    private const int RightButtonUp = 0x0205;
    private const int NullMessage = 0x0000;
    private const int GetIconMessage = 0x007F;
    private const int SmallIcon = 0;
    private const int BigIcon = 1;
    private const int SmallIcon2 = 2;
    private const uint ShowCommand = 1;
    private const uint HideCommand = 2;
    private const uint ExitCommand = 3;
    private const int ApplicationIcon = 32512;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private NotifyIconData _data;
    private string _showText;
    private string _hideText;
    private string _exitText;
    private bool _disposed;

    public WindowsTrayIcon(
        IntPtr windowHandle,
        string toolTip,
        string showText,
        string hideText,
        string exitText)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _showText = showText;
        _hideText = hideText;
        _exitText = exitText;
        _source = HwndSource.FromHwnd(windowHandle)
                  ?? throw new InvalidOperationException("Could not attach the tray icon to the window.");
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _data = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = windowHandle,
            Id = 1,
            Flags = NotifyMessage | NotifyIcon | NotifyTip,
            CallbackMessage = CallbackMessage,
            IconHandle = GetWindowIcon(windowHandle),
            ToolTip = toolTip.Length <= 127 ? toolTip : toolTip[..127],
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        if (_data.IconHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the tray icon.");
        }

        _source.AddHook(WindowProc);
        if (!ShellNotifyIcon(NotifyAdd, ref _data))
        {
            _source.RemoveHook(WindowProc);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not add the tray icon.");
        }
    }

    private static IntPtr GetWindowIcon(IntPtr windowHandle)
    {
        var iconHandle = SendMessage(
            windowHandle,
            GetIconMessage,
            new IntPtr(SmallIcon2),
            IntPtr.Zero);
        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = SendMessage(
                windowHandle,
                GetIconMessage,
                new IntPtr(SmallIcon),
                IntPtr.Zero);
        }
        if (iconHandle == IntPtr.Zero)
        {
            iconHandle = SendMessage(
                windowHandle,
                GetIconMessage,
                new IntPtr(BigIcon),
                IntPtr.Zero);
        }

        if (iconHandle != IntPtr.Zero)
        {
            return iconHandle;
        }

        var moduleHandle = GetModuleHandle(null);
        iconHandle = LoadIcon(moduleHandle, new IntPtr(ApplicationIcon));
        return iconHandle != IntPtr.Zero
            ? iconHandle
            : LoadIcon(IntPtr.Zero, new IntPtr(ApplicationIcon));
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? HideRequested;
    public event EventHandler? ExitRequested;

    public void UpdateLabels(string showText, string hideText, string exitText)
    {
        _showText = showText;
        _hideText = hideText;
        _exitText = exitText;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ShellNotifyIcon(NotifyDelete, ref _data);
        _source.RemoveHook(WindowProc);
    }

    private IntPtr WindowProc(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if ((uint)message == _taskbarCreatedMessage)
        {
            ShellNotifyIcon(NotifyAdd, ref _data);
            return IntPtr.Zero;
        }

        if (message != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        switch (lParam.ToInt32())
        {
            case LeftButtonUp:
                handled = true;
                ShowRequested?.Invoke(this, EventArgs.Empty);
                break;
            case RightButtonUp:
                handled = true;
                ShowContextMenu();
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MenuString, ShowCommand, _showText);
            AppendMenu(menu, MenuString, HideCommand, _hideText);
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, MenuString, ExitCommand, _exitText);
            if (!GetCursorPos(out var cursor))
            {
                return;
            }

            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenuEx(
                menu,
                TrackRightButton | TrackReturnCommand,
                cursor.X,
                cursor.Y,
                _windowHandle,
                IntPtr.Zero);
            PostMessage(_windowHandle, NullMessage, IntPtr.Zero, IntPtr.Zero);
            switch (command)
            {
                case ShowCommand:
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case HideCommand:
                    HideRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case ExitCommand:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ToolTip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(
        uint message,
        ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        IntPtr menu,
        uint flags,
        uint itemId,
        string? itemText);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr windowHandle,
        IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
