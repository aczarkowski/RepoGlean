using RepoGlean.Git;
using RepoGlean.Rules;

namespace RepoGlean.Scanning;

internal static class RepositoryPathPolicy
{
    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string NormalizeRelativePath(string path)
    {
        var normalized = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        return normalized.TrimStart('/');
    }

    internal static bool IsExcluded(
        string absolutePath,
        string relativePath,
        IReadOnlyList<string> exclusions)
    {
        foreach (var exclusion in exclusions)
        {
            if (string.IsNullOrWhiteSpace(exclusion)) continue;
            if (Path.IsPathRooted(exclusion) &&
                RepositoryDiscovery.IsSameOrDescendant(absolutePath, Path.GetFullPath(exclusion)))
            {
                return true;
            }

            var normalized = NormalizeRelativePath(exclusion).TrimEnd('/');
            if (string.Equals(relativePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith($"{normalized}/", StringComparison.OrdinalIgnoreCase) ||
                GlobMatcher.IsMatch(normalized, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsReservedRootQuarantine(string relativePath) =>
        !relativePath.Contains('/', StringComparison.Ordinal) &&
        relativePath.StartsWith(GitClient.QuarantineDirectoryPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool IsRepositoryBoundary(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    internal static bool ContainsVisibleContent(
        string candidateRelativePath,
        IReadOnlyList<string> visiblePaths)
    {
        var prefix = candidateRelativePath.EndsWith("/", StringComparison.Ordinal)
            ? candidateRelativePath
            : $"{candidateRelativePath}/";
        return visiblePaths.Any(path =>
            string.Equals(path, candidateRelativePath, StringComparison.Ordinal) ||
            path.StartsWith(prefix, StringComparison.Ordinal));
    }
}
