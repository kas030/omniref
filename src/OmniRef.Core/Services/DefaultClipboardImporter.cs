using OmniRef.Core.Interfaces;

namespace OmniRef.Core.Services;

public sealed class DefaultClipboardImporter : IClipboardImporter
{
    public ClipboardImportResult Classify(ClipboardSnapshot snapshot)
    {
        if (snapshot.FilePaths.Count > 0)
        {
            return new(ClipboardImportKind.Files, snapshot.FilePaths, null, null);
        }

        if (snapshot.PngBytes is { Length: > 0 })
        {
            return new(ClipboardImportKind.Image, [], snapshot.PngBytes, null);
        }

        var text = snapshot.Text?.Trim();
        if (text is null or "")
        {
            return new(ClipboardImportKind.None, [], null, null);
        }

        var kind = Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? ClipboardImportKind.Url
            : ClipboardImportKind.Text;
        return new(kind, [], null, text);
    }
}
