using DevCleaner.Git;
using DevCleaner.Rules;

namespace DevCleaner.Scanning;

public sealed class RepositoryDiscovery
{
    private readonly GitClient git;
    private readonly IReadOnlyList<string> implicitExclusions;

    public RepositoryDiscovery(GitClient git)
        : this(git, GetPlatformExclusions())
    {
    }

    internal RepositoryDiscovery(GitClient git, IReadOnlyList<string> implicitExclusions)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.implicitExclusions = implicitExclusions
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
    }

    public async Task<RepositoryDiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<string>? exclusions = null,
        bool allDrives = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var requestedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .ToList();
        if (allDrives)
        {
            requestedRoots.AddRange(DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
                .Select(drive => Path.GetFullPath(drive.RootDirectory.FullName)));
        }

        requestedRoots = requestedRoots.Distinct(PathComparer).ToList();
        var configuredExclusions = (exclusions ?? [])
            .Where(exclusion => !string.IsNullOrWhiteSpace(exclusion))
            .Select(exclusion => Path.IsPathRooted(exclusion) ? Path.GetFullPath(exclusion) : exclusion.Replace('\\', '/'))
            .ToArray();
        var repositories = new HashSet<string>(PathComparer);
        var warnings = new List<OperationWarning>();

        foreach (var root in requestedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                warnings.Add(new OperationWarning(root, "Scan root does not exist or is not an accessible directory."));
                continue;
            }

            await DiscoverRootAsync(root, root, configuredExclusions, repositories, warnings, cancellationToken).ConfigureAwait(false);
        }

        return new RepositoryDiscoveryResult(
            Array.AsReadOnly(repositories.OrderBy(path => path, PathComparer).ToArray()),
            Array.AsReadOnly(warnings.ToArray()));
    }

    private async Task DiscoverRootAsync(
        string path,
        string currentRequestedRoot,
        IReadOnlyList<string> configuredExclusions,
        HashSet<string> repositories,
        List<OperationWarning> warnings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase) ||
            IsExcluded(path, currentRequestedRoot, configuredExclusions) ||
            IsImplicitlyExcluded(path, currentRequestedRoot)) return;
        if (!TryGetAttributes(path, warnings, out var attributes) || (attributes & FileAttributes.ReparsePoint) != 0) return;

        if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
        {
            if (await git.IsWorkingTreeAsync(path, cancellationToken).ConfigureAwait(false)) repositories.Add(Path.GetFullPath(path));
        }

        foreach (var child in EnumerateDirectories(path, warnings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(child), ".git", StringComparison.OrdinalIgnoreCase)) continue;
            await DiscoverRootAsync(child, currentRequestedRoot, configuredExclusions, repositories, warnings, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsImplicitlyExcluded(string path, string requestedRoot)
    {
        foreach (var exclusion in implicitExclusions)
        {
            if (!IsSameOrDescendant(path, exclusion)) continue;
            return !IsSameOrDescendant(requestedRoot, exclusion);
        }

        return false;
    }

    private static bool IsExcluded(string path, string requestedRoot, IReadOnlyList<string> exclusions)
    {
        var relativePath = Path.GetRelativePath(requestedRoot, path).Replace('\\', '/');
        foreach (var exclusion in exclusions)
        {
            if (Path.IsPathRooted(exclusion))
            {
                if (IsSameOrDescendant(path, exclusion)) return true;
                continue;
            }

            var normalized = exclusion.Trim('/');
            if (string.Equals(relativePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith($"{normalized}/", StringComparison.OrdinalIgnoreCase) ||
                GlobMatcher.IsMatch(normalized, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> EnumerateDirectories(string path, List<OperationWarning> warnings)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            warnings.Add(new OperationWarning(path, $"Unable to enumerate directory: {exception.Message}"));
            return [];
        }
    }

    private static bool TryGetAttributes(string path, List<OperationWarning> warnings, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            warnings.Add(new OperationWarning(path, $"Unable to inspect path: {exception.Message}"));
            attributes = default;
            return false;
        }
    }

    internal static bool IsSameOrDescendant(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> GetPlatformExclusions()
    {
        var paths = new List<string>();
        AddSpecialFolder(paths, Environment.SpecialFolder.ApplicationData);
        AddSpecialFolder(paths, Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            paths.Add(Path.Combine(home, ".cache"));
            paths.Add(Path.Combine(home, ".local", "share", "Trash"));
            paths.Add(Path.Combine(home, ".Trash"));
            paths.Add(Path.Combine(home, "Library", "Caches"));
        }

        return paths;
    }

    private static void AddSpecialFolder(List<string> paths, Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
