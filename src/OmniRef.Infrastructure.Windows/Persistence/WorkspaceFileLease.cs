using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OmniRef.Core.Interfaces;

namespace OmniRef.Infrastructure.Windows.Persistence;

internal sealed class WorkspaceFileLease : IWorkspaceFileLease
{
    private readonly FileStream _stream;
    private readonly FileIdentity _identity;
    private bool _disposed;

    public WorkspaceFileLease(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1,
            FileOptions.RandomAccess);
        _identity = ReadIdentity(_stream.SafeFileHandle);
    }

    public string Path { get; }

    public bool IsCurrent
    {
        get
        {
            try
            {
                if (_disposed)
                {
                    return false;
                }

                var leasedHandle = _stream.SafeFileHandle;
                if (leasedHandle.IsClosed || leasedHandle.IsInvalid ||
                    ReadIdentity(leasedHandle) != _identity)
                {
                    return false;
                }

                using var current = new FileStream(
                    Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.RandomAccess);
                return ReadIdentity(current.SafeFileHandle) == _identity;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
        }
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                "Could not identify the workspace file.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow);
}
