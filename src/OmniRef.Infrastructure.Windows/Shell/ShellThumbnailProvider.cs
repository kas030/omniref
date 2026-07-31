using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using OmniRef.Core.Interfaces;
using OmniRef.Core.Models;

namespace OmniRef.Infrastructure.Windows.Shell;

public sealed class ShellThumbnailProvider : IThumbnailProvider, IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private bool _disposed;

    public ShellThumbnailProvider()
    {
        _thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "OmniRef Shell Thumbnail Worker",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<ThumbnailData?> GetThumbnailAsync(
        string path,
        int requestedPixels,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ThumbnailData?>(cancellationToken);
        }

        var completion = new TaskCompletionSource<ThumbnailData?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(GetThumbnail(path, requestedPixels));
                }
                catch (Exception exception) when (
                    exception is COMException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    completion.TrySetResult(null);
                }
            },
            cancellationToken);
        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _work.Dispose();
    }

    private static ThumbnailData? GetThumbnail(string path, int requestedPixels)
    {
        var size = Math.Clamp(requestedPixels, 32, 1024);
        var factoryGuid = typeof(IShellItemImageFactory).GUID;
        var result = SHCreateItemFromParsingName(
            Path.GetFullPath(path),
            IntPtr.Zero,
            ref factoryGuid,
            out var factory);
        if (result < 0 || factory is null)
        {
            return null;
        }

        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            factory.GetImage(
                new NativeSize(size, size),
                ShellItemImageFlags.ResizeToFit | ShellItemImageFlags.BiggerSizeOk,
                out bitmapHandle);
            if (bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return new(stream.ToArray(), source.PixelWidth, source.PixelHeight);
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            Marshal.FinalReleaseComObject(factory);
        }
    }

    private void Worker()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            action();
        }
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(
            NativeSize size,
            ShellItemImageFlags flags,
            out IntPtr bitmapHandle);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory shellItem);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
