using DevCleaner.Git;
using DevCleaner.Rules;

namespace DevCleaner.Scanning;

internal interface IDriveRootProvider
{
    IReadOnlyList<string> GetFixedDriveRoots();
}

internal sealed class SystemDriveRootProvider : IDriveRootProvider
{
    public IReadOnlyList<string> GetFixedDriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed) roots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return roots;
    }
}

public sealed class RepositoryDiscovery
{
    private readonly GitClient git;
    private readonly IReadOnlyList<string> implicitExclusions;
    private readonly IVolumeBoundary volumeBoundary;
    private readonly IDriveRootProvider driveRootProvider;

    public RepositoryDiscovery(GitClient git)
        : this(git, GetPlatformExclusions(), new FileSystemIdentityProvider(), new SystemDriveRootProvider())
    {
    }

    internal RepositoryDiscovery(
        GitClient git,
        IReadOnlyList<string> implicitExclusions,
        IVolumeBoundary? volumeBoundary = null,
        IDriveRootProvider? driveRootProvider = null)
    {
        this.git = git ?? throw new ArgumentNullException(nameof(git));
        this.implicitExclusions = implicitExclusions
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        this.volumeBoundary = volumeBoundary ?? new FileSystemIdentityProvider();
        this.driveRootProvider = driveRootProvider ?? new SystemDriveRootProvider();
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
            requestedRoots.AddRange(driveRootProvider.GetFixedDriveRoots().Select(Path.GetFullPath));
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

            if (!volumeBoundary.TryGetVolumeId(root, out var rootVolumeId, out var rootVolumeError))
            {
                warnings.Add(new OperationWarning(root, rootVolumeError ?? "Unable to identify the scan root volume."));
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = pending.Pop();
                if (string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase) ||
                    IsExcluded(path, root, configuredExclusions) ||
                    IsImplicitlyExcluded(path, root))
                {
                    continue;
                }

                if (!TryGetAttributes(path, warnings, out var attributes)) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add(new OperationWarning(path, "Skipped directory link, junction, or reparse point."));
                    continue;
                }

                if (!volumeBoundary.TryGetVolumeId(path, out var pathVolumeId, out var volumeError))
                {
                    warnings.Add(new OperationWarning(path, volumeError ?? "Unable to identify path volume."));
                    continue;
                }

                if (pathVolumeId != rootVolumeId)
                {
                    warnings.Add(new OperationWarning(path, "Skipped path on a different filesystem volume."));
                    continue;
                }

                if (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))
                {
                    try
                    {
                        if (await git.IsWorkingTreeAsync(path, cancellationToken).ConfigureAwait(false)) repositories.Add(Path.GetFullPath(path));
                    }
                    catch (GitCommandException exception)
                    {
                        warnings.Add(new OperationWarning(path, exception.Message));
                    }
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.Equals(Path.GetFileName(child), ".git", StringComparison.OrdinalIgnoreCase)) pending.Push(child);
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    warnings.Add(new OperationWarning(path, $"Unable to enumerate directory: {exception.Message}"));
                }
            }
        }

        return new RepositoryDiscoveryResult(
            Array.AsReadOnly(repositories.OrderBy(path => path, PathComparer).ToArray()),
            Array.AsReadOnly(warnings.ToArray()));
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
