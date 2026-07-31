using System.Globalization;
using System.IO;

namespace OmniRef.Infrastructure.Windows.Diagnostics;

public sealed class RollingFileLogger
{
    private readonly object _sync = new();
    private readonly string _logDirectory;

    public RollingFileLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warning(string message) => Write("WARN", message, null);
    public void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_logDirectory);
                var path = Path.Combine(
                    _logDirectory,
                    $"omniref-{DateTimeOffset.UtcNow:yyyyMMdd}.log");
                var line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.UtcNow:O} [{level}] {message}");
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(path, line + Environment.NewLine);
                Prune();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Prune()
    {
        foreach (var file in new DirectoryInfo(_logDirectory)
                     .EnumerateFiles("omniref-*.log")
                     .OrderByDescending(file => file.Name)
                     .Skip(7))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
            }
        }
    }
}
