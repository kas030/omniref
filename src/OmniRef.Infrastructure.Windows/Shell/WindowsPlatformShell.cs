using System.Diagnostics;
using System.IO;
using OmniRef.Core.Interfaces;

namespace OmniRef.Infrastructure.Windows.Shell;

public sealed class WindowsPlatformShell : IPlatformShell
{
    public bool OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return TryStart(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool RevealPath(string path)
    {
        if (Directory.Exists(path))
        {
            return TryStart(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { path }
            });
        }

        if (!File.Exists(path))
        {
            return false;
        }

        return TryStart(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            ArgumentList = { $"/select,{path}" }
        });
    }

    public bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return TryStart(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static bool TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
