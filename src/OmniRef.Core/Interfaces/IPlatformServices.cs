using OmniRef.Core.Models;

namespace OmniRef.Core.Interfaces;

public interface IThumbnailProvider
{
    Task<ThumbnailData?> GetThumbnailAsync(
        string path,
        int requestedPixels,
        CancellationToken cancellationToken = default);
}

public interface IPlatformShell
{
    bool OpenPath(string path);
    bool RevealPath(string path);
    bool OpenUrl(string url);
}

public interface IHotkeyService : IDisposable
{
    event EventHandler? Pressed;
    bool Register(IntPtr windowHandle, HotkeyGesture gesture);
    void Unregister();
}

public readonly record struct HotkeyGesture(bool Control, bool Alt, bool Shift, bool Windows, int VirtualKey);

public interface IClipboardImporter
{
    ClipboardImportResult Classify(ClipboardSnapshot snapshot);
}

public sealed record ClipboardSnapshot(
    IReadOnlyList<string> FilePaths,
    byte[]? PngBytes,
    string? Text);

public sealed record ClipboardImportResult(
    ClipboardImportKind Kind,
    IReadOnlyList<string> FilePaths,
    byte[]? PngBytes,
    string? Text);

public enum ClipboardImportKind
{
    None,
    Files,
    Image,
    Url,
    Text
}
