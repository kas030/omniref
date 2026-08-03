using System.Runtime.InteropServices;

namespace OmniRef.Infrastructure.Windows.Shell;

public static class WindowsWindowAnimation
{
    private const int WindowStyleIndex = -16;
    private const long CaptionStyle = 0x00C00000L;
    private const uint RefreshFrameFlags = 0x00000037;
    private const int WindowCornerPreferenceAttribute = 33;
    private const int DoNotRoundWindowCornerPreference = 1;
    private const int RoundWindowCornerPreference = 2;

    public static void EnableSystemWindowTransitions(IntPtr windowHandle)
    {
        var style = GetWindowLongPtr(windowHandle, WindowStyleIndex);
        var captionedStyle = new IntPtr(style.ToInt64() | CaptionStyle);
        if (captionedStyle != style)
        {
            SetWindowLongPtr(windowHandle, WindowStyleIndex, captionedStyle);
        }
    }

    public static void DisableSystemWindowTransitions(IntPtr windowHandle)
    {
        var style = GetWindowLongPtr(windowHandle, WindowStyleIndex);
        var borderlessStyle = new IntPtr(style.ToInt64() & ~CaptionStyle);
        if (borderlessStyle != style)
        {
            SetWindowLongPtr(windowHandle, WindowStyleIndex, borderlessStyle);
            SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                RefreshFrameFlags);
        }
    }

    public static void RefreshSystemWindowFrame(IntPtr windowHandle)
    {
        SetWindowRgn(windowHandle, IntPtr.Zero, redraw: true);
        var margins = new DwmMargins(1);
        DwmExtendFrameIntoClientArea(windowHandle, ref margins);
        SetRoundedCorners(windowHandle, enabled: true);
        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            RefreshFrameFlags);
    }

    public static void SetRoundedCorners(IntPtr windowHandle, bool enabled)
    {
        var cornerPreference = enabled
            ? RoundWindowCornerPreference
            : DoNotRoundWindowCornerPreference;
        DwmSetWindowAttribute(
            windowHandle,
            WindowCornerPreferenceAttribute,
            ref cornerPreference,
            sizeof(int));
    }

    public static void ApplyRoundedWindowRegion(IntPtr windowHandle, int cornerRadius)
    {
        if (windowHandle == IntPtr.Zero || cornerRadius <= 0 ||
            !GetWindowRect(windowHandle, out var windowRect))
        {
            return;
        }

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var diameter = cornerRadius * 2;
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (!SetWindowRgn(windowHandle, region, redraw: true))
        {
            DeleteObject(region);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowRgn(
        IntPtr windowHandle,
        IntPtr regionHandle,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out WindowRect windowRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        IntPtr windowHandle,
        ref DwmMargins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DwmMargins(int thickness)
    {
        public readonly int Left = thickness;
        public readonly int Right = thickness;
        public readonly int Top = thickness;
        public readonly int Bottom = thickness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WindowRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}
