using RepoGlean.Git;
using RepoGlean.Rules;

namespace RepoGlean.Scanning;

internal interface IDriveRootProvider
{
    DriveRootDiscoveryResult GetFixedDriveRoots();
}

internal sealed record DriveRootDiscoveryResult(IReadOnlyList<string> Roots, IReadOnlyList<OperationWarning> Warnings);

internal sealed class SystemDriveRootProvider : IDriveRootProvider
{
    public DriveRootDiscoveryResult GetFixedDriveRoots()
    {
        var roots = new List<string>();
        var warnings = new List<OperationWarning>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            warnings.Add(new OperationWarning("fixed-drives", $"Unable to enumerate fixed drives: {exception.Message}"));
            return new DriveRootDiscoveryResult([], Array.AsReadOnly(warnings.ToArray()));
        }

        foreach (var drive in drives)
        {
            var drivePath = drive.Name;
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed) roots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add(new OperationWarning(drivePath, $"Unable to inspect fixed drive: {exception.Message}"));
            }
        }

        return new DriveRootDiscoveryResult(Array.AsReadOnly(roots.ToArray()), Array.AsReadOnly(warnings.ToArray()));
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

    internal RepositoryDiscovery(GitClient git, IDriveRootProvider driveRootProvider)
        : this(git, GetPlatformExclusions(), new FileSystemIdentityProvider(), driveRootProvider)
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
        var warnings = new List<OperationWarning>();
        if (allDrives)
        {
            var driveRoots = driveRootProvider.GetFixedDriveRoots();
            requestedRoots.AddRange(driveRoots.Roots.Select(Path.GetFullPath));
            warnings.AddRange(driveRoots.Warnings);
        }

        requestedRoots = requestedRoots.Distinct(PathComparer).ToList();
        var configuredExclusions = (exclusions ?? [])
            .Where(exclusion => !string.IsNullOrWhiteSpace(exclusion))
            .Select(exclusion => Path.IsPathRooted(exclusion) ? Path.GetFullPath(exclusion) : exclusion.Replace('\\', '/'))
            .ToArray();
        var repositories = new HashSet<string>(PathComparer);

        foreach (var root in requestedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                warnings.Add(new OperationWarning(root, "Scan root does not exist or is not an accessible directory."));
                continue;
            }

            if (!volumeBoundary.TryGetMountIdentity(root, out var rootMountIdentity, out var rootMountError) || rootMountIdentity is null)
            {
                warnings.Add(new OperationWarning(root, rootMountError ?? "Unable to identify the scan root filesystem mount."));
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

                if (!volumeBoundary.TryGetMountIdentity(path, out var pathMountIdentity, out var volumeError) || pathMountIdentity is null)
                {
                    warnings.Add(new OperationWarning(path, volumeError ?? "Unable to identify path filesystem mount."));
                    continue;
                }

                if (pathMountIdentity != rootMountIdentity)
                {
                    warnings.Add(new OperationWarning(path, "Skipped path on a different filesystem mount or volume."));
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
            Array.AsReadOnly(warnings.ToArray()),
            Array.AsReadOnly(requestedRoots.ToArray()));
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
