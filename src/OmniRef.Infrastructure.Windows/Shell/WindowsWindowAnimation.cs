using System.Runtime.InteropServices;

namespace OmniRef.Infrastructure.Windows.Shell;

public static class WindowsWindowAnimation
{
    private const int WindowStyleIndex = -16;
    private const long CaptionStyle = 0x00C00000L;
    private const uint GetClientAreaAnimation = 0x1042;
    private const uint AnimateHide = 0x00010000;
    private const uint AnimateActivate = 0x00020000;
    private const uint AnimateBlend = 0x00080000;
    private const uint VisibilityAnimationMilliseconds = 200;
    private const uint RefreshFrameFlags = 0x00000037;

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

    public static bool TryShow(IntPtr windowHandle) =>
        AreClientAreaAnimationsEnabled() &&
        AnimateWindow(
            windowHandle,
            VisibilityAnimationMilliseconds,
            AnimateActivate | AnimateBlend);

    public static bool TryHide(IntPtr windowHandle) =>
        AreClientAreaAnimationsEnabled() &&
        AnimateWindow(
            windowHandle,
            VisibilityAnimationMilliseconds,
            AnimateHide | AnimateBlend);

    private static bool AreClientAreaAnimationsEnabled() =>
        SystemParametersInfo(
            GetClientAreaAnimation,
            0,
            out var animationsEnabled,
            0) &&
        animationsEnabled;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr newValue);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AnimateWindow(
        IntPtr windowHandle,
        uint durationMilliseconds,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value,
        uint updateFlags);
}
