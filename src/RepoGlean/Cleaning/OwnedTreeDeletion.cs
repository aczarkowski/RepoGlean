using RepoGlean.Scanning;

namespace RepoGlean.Cleaning;

internal enum OwnedTreeEntryType
{
    File,
    Directory,
    Link,
}

internal sealed record OwnedTreeEntry(
    string RelativePath,
    FileSystemIdentity Identity,
    OwnedTreeEntryType Type);

internal sealed record OwnedTreeSnapshot(IReadOnlyList<OwnedTreeEntry> Entries)
{
    public bool HasSameEntries(OwnedTreeSnapshot other) => Entries.SequenceEqual(other.Entries);
}

internal sealed class OwnedTreeInspector(
    ICleanupFileSystem fileSystem,
    IFileSystemIdentityProvider identityProvider)
{
    public bool TryCapture(
        string rootPath,
        FileSystemIdentity expectedMount,
        CancellationToken cancellationToken,
        out OwnedTreeSnapshot? snapshot,
        out string? error)
    {
        try
        {
            var entries = new List<OwnedTreeEntry>();
            var pending = new Stack<(string Path, string RelativePath)>();
            pending.Push((Path.GetFullPath(rootPath), "."));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                if (!TryCaptureEntry(current.Path, current.RelativePath, expectedMount, out var entry, out error))
                {
                    snapshot = null;
                    return false;
                }

                entries.Add(entry!);
                if (entry!.Type != OwnedTreeEntryType.Directory) continue;

                var children = fileSystem.GetFileSystemEntries(current.Path)
                    .OrderBy(path => path, PathComparer)
                    .ToArray();
                if (!HasExpectedIdentity(current.Path, entry, expectedMount, out error))
                {
                    snapshot = null;
                    return false;
                }

                for (var index = children.Length - 1; index >= 0; index--)
                {
                    var child = children[index];
                    var childRelativePath = current.RelativePath == "."
                        ? Path.GetFileName(child)
                        : Path.Combine(current.RelativePath, Path.GetFileName(child));
                    pending.Push((child, NormalizeRelativePath(childRelativePath)));
                }
            }

            entries.Sort((left, right) => PathComparer.Compare(left.RelativePath, right.RelativePath));
            snapshot = new OwnedTreeSnapshot(Array.AsReadOnly(entries.ToArray()));
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            snapshot = null;
            error = $"Unable to capture the owned descendant snapshot: {exception.Message}";
            return false;
        }
    }

    private bool TryCaptureEntry(
        string path,
        string relativePath,
        FileSystemIdentity expectedMount,
        out OwnedTreeEntry? entry,
        out string? error)
    {
        var attributes = fileSystem.GetAttributes(path);
        if (!identityProvider.TryGetIdentity(path, out var identity, out var identityError) || identity is null)
        {
            entry = null;
            error = identityError ?? $"Stable identity is unavailable for '{path}'.";
            return false;
        }

        var type = GetEntryType(attributes);
        if (type != GetEntryType(identity.Attributes))
        {
            entry = null;
            error = $"Filesystem entry type changed while capturing '{path}'.";
            return false;
        }

        if (!CleanupIdentity.IsSameMount(expectedMount, identity))
        {
            entry = null;
            error = $"Owned descendant '{path}' crosses the repository filesystem mount boundary.";
            return false;
        }

        if (type == OwnedTreeEntryType.Directory &&
            string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase))
        {
            entry = null;
            error = $"Owned descendant snapshot contains a nested repository boundary at '{path}'.";
            return false;
        }

        entry = new OwnedTreeEntry(NormalizeRelativePath(relativePath), identity, type);
        error = null;
        return true;
    }

    private bool HasExpectedIdentity(
        string path,
        OwnedTreeEntry expected,
        FileSystemIdentity expectedMount,
        out string? error)
    {
        if (!TryCaptureEntry(path, expected.RelativePath, expectedMount, out var current, out error) || current is null)
        {
            return false;
        }

        if (current != expected)
        {
            error = $"Filesystem entry '{path}' changed while capturing the owned descendant snapshot.";
            return false;
        }

        error = null;
        return true;
    }

    private static OwnedTreeEntryType GetEntryType(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0) return OwnedTreeEntryType.Link;
        return (attributes & FileAttributes.Directory) != 0
            ? OwnedTreeEntryType.Directory
            : OwnedTreeEntryType.File;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed class BoundaryAwareDeleter(
    ICleanupFileSystem fileSystem,
    IFileSystemIdentityProvider identityProvider)
{
    public void Delete(
        string rootPath,
        OwnedTreeSnapshot snapshot,
        FileSystemIdentity expectedMount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = BuildEntryMap(snapshot, PathComparer);
        if (!entries.TryGetValue(".", out var root))
        {
            throw new IOException("Owned descendant snapshot does not contain its payload root.");
        }

        DeleteEntry(Path.GetFullPath(rootPath), root, entries, expectedMount, cancellationToken);
    }

    internal static IReadOnlyDictionary<string, OwnedTreeEntry> BuildEntryMap(
        OwnedTreeSnapshot snapshot,
        StringComparer pathComparer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pathComparer);
        var entries = new Dictionary<string, OwnedTreeEntry>(pathComparer);
        foreach (var entry in snapshot.Entries)
        {
            if (!entries.TryAdd(entry.RelativePath, entry))
            {
                throw new IOException(
                    $"Owned descendant snapshot contains ambiguous paths under the platform path comparer: '{entry.RelativePath}'.");
            }
        }

        return entries;
    }

    private void DeleteEntry(
        string path,
        OwnedTreeEntry expected,
        IReadOnlyDictionary<string, OwnedTreeEntry> entries,
        FileSystemIdentity expectedMount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevalidateEntry(path, expected, expectedMount);
        if (expected.Type == OwnedTreeEntryType.Directory)
        {
            var expectedChildren = entries.Values
                .Where(entry => string.Equals(GetParent(entry.RelativePath), expected.RelativePath, PathComparison))
                .OrderBy(entry => entry.RelativePath, PathComparer)
                .ToArray();
            var actualChildren = fileSystem.GetFileSystemEntries(path)
                .Select(child => NormalizeRelativePath(Path.GetRelativePath(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), child)))
                .Select(relative => expected.RelativePath == "." ? relative : $"{expected.RelativePath}/{relative}")
                .OrderBy(relative => relative, PathComparer)
                .ToArray();
            if (!actualChildren.SequenceEqual(expectedChildren.Select(child => child.RelativePath), PathComparer))
            {
                throw new IOException($"Owned directory '{path}' changed before boundary-aware deletion.");
            }

            RevalidateEntry(path, expected, expectedMount);
            foreach (var child in expectedChildren)
            {
                var childPath = Path.Combine(path, Path.GetFileName(child.RelativePath));
                DeleteEntry(childPath, child, entries, expectedMount, cancellationToken);
            }

            if (fileSystem.GetFileSystemEntries(path).Count != 0)
            {
                throw new IOException($"Owned directory '{path}' gained entries during boundary-aware deletion.");
            }

            RevalidateEntry(path, expected, expectedMount);
            fileSystem.DeleteDirectory(path);
            return;
        }

        if (expected.Type == OwnedTreeEntryType.Link &&
            (expected.Identity.Attributes & FileAttributes.Directory) != 0)
        {
            fileSystem.DeleteDirectory(path);
            return;
        }

        fileSystem.DeleteFile(path);
    }

    private void RevalidateEntry(
        string path,
        OwnedTreeEntry expected,
        FileSystemIdentity expectedMount)
    {
        var attributes = fileSystem.GetAttributes(path);
        var currentType = (attributes & FileAttributes.ReparsePoint) != 0
            ? OwnedTreeEntryType.Link
            : (attributes & FileAttributes.Directory) != 0
                ? OwnedTreeEntryType.Directory
                : OwnedTreeEntryType.File;
        if (currentType != expected.Type)
        {
            throw new IOException($"Owned entry type changed before deletion at '{path}'.");
        }

        if (!identityProvider.TryGetIdentity(path, out var identity, out var identityError) || identity is null)
        {
            throw new IOException(identityError ?? $"Stable identity is unavailable before deleting '{path}'.");
        }

        if (!CleanupIdentity.HasSameStableIdentity(expected.Identity, identity) ||
            !CleanupIdentity.IsSameMount(expectedMount, identity))
        {
            throw new IOException($"Owned entry identity or filesystem mount changed before deletion at '{path}'.");
        }

        if (expected.Type == OwnedTreeEntryType.Directory &&
            ((identity.Attributes & FileAttributes.ReparsePoint) != 0 || !CleanupIdentity.IsDirectory(identity)))
        {
            throw new IOException($"Boundary-aware deletion refused to enter uncertain directory '{path}'.");
        }
    }

    private static string GetParent(string relativePath)
    {
        if (relativePath == ".") return string.Empty;
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? "." : relativePath[..separator];
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
