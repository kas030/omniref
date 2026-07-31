using OmniRef.Core.Models;

namespace OmniRef.Core.Services;

public static class PathResolver
{
    public static string? CreateRelativePath(string workspacePath, string sourcePath)
    {
        var workspaceDirectory = Path.GetDirectoryName(Path.GetFullPath(workspacePath));
        if (workspaceDirectory is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(workspaceDirectory, Path.GetFullPath(sourcePath));
        return Path.IsPathRooted(relative) ||
               relative.Equals("..", StringComparison.Ordinal) ||
               relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? null
            : relative;
    }

    public static string? Resolve(string workspacePath, SourceDescriptor source)
    {
        if (source.RelativePath is { Length: > 0 })
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(workspacePath));
            if (directory is not null)
            {
                var relativeCandidate = Path.GetFullPath(Path.Combine(directory, source.RelativePath));
                if (Exists(relativeCandidate))
                {
                    return relativeCandidate;
                }
            }
        }

        return source.AbsolutePath is { Length: > 0 } && Exists(source.AbsolutePath)
            ? Path.GetFullPath(source.AbsolutePath)
            : null;
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
