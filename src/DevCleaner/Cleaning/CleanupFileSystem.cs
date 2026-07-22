using DevCleaner.Scanning;

namespace DevCleaner.Cleaning;

internal interface ICleanupFileSystem
{
    FileAttributes GetAttributes(string path);

    void CreateDirectory(string path);

    void DeleteOwnedObject(string path, bool isDirectory, CancellationToken cancellationToken);

    void DeleteDirectory(string path);
}

internal sealed class SystemCleanupFileSystem : ICleanupFileSystem
{
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteOwnedObject(string path, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // .NET treats symbolic links and Windows name-surrogate reparse points as leaf entries here.
        // The approved v1 threat model covers accidental concurrency before this primitive, not a
        // malicious same-user process racing the runtime's internal enumeration of this GUID-private path.
        if (isDirectory) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);
}

internal interface ICleanupMutationObserver
{
    void BeforeQuarantineMove(ArtifactCandidate candidate, string quarantineRoot, string destinationPath);

    void BeforeMovedIdentityCheck(ArtifactCandidate candidate, string quarantineRoot, string destinationPath);

    void BeforeRecursiveDelete(ArtifactCandidate candidate, string quarantineRoot, string destinationPath);
}

internal sealed class NullCleanupMutationObserver : ICleanupMutationObserver
{
    public void BeforeQuarantineMove(ArtifactCandidate candidate, string quarantineRoot, string destinationPath)
    {
    }

    public void BeforeMovedIdentityCheck(ArtifactCandidate candidate, string quarantineRoot, string destinationPath)
    {
    }

    public void BeforeRecursiveDelete(ArtifactCandidate candidate, string quarantineRoot, string destinationPath)
    {
    }
}

internal sealed class CleanupBoundaryInspector(ICleanupFileSystem fileSystem)
{
    public string? Inspect(string requestedRoot, string targetPath)
    {
        var relativePath = Path.GetRelativePath(requestedRoot, targetPath);
        var currentPath = requestedRoot;
        foreach (var component in new[] { string.Empty }.Concat(relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries)))
        {
            if (component.Length > 0) currentPath = Path.Combine(currentPath, component);
            FileAttributes attributes;
            try
            {
                attributes = fileSystem.GetAttributes(currentPath);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return $"Cleanup boundary component no longer exists: {exception.Message}";
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return $"Cleanup boundary contains a filesystem link, junction, or reparse point: {currentPath}";
            }
        }

        return null;
    }

    public bool EntryExists(string path)
    {
        try
        {
            _ = fileSystem.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}

internal static class CleanupIdentity
{
    public static bool HasSameStableIdentity(FileSystemIdentity captured, FileSystemIdentity current) =>
        captured.VolumeId == current.VolumeId &&
        captured.FileId == current.FileId &&
        string.Equals(captured.MountId, current.MountId, StringComparison.Ordinal);

    public static bool IsSameMount(FileSystemIdentity left, FileSystemIdentity right) =>
        left.VolumeId == right.VolumeId && string.Equals(left.MountId, right.MountId, StringComparison.Ordinal);

    public static bool IsDirectory(FileSystemIdentity identity) =>
        (identity.Attributes & FileAttributes.Directory) != 0 && (identity.Attributes & FileAttributes.ReparsePoint) == 0;
}
