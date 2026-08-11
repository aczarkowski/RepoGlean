using RepoGlean.Scanning;

namespace RepoGlean.Auditing;

internal sealed record AuditFileSystemEntry(
    string Name,
    string AbsolutePath,
    FileSystemEntryKind Kind,
    long Length,
    DateTimeOffset? LastWriteTimeUtc,
    string? InspectionError);

internal interface IAuditFileSystem
{
    bool TryInspect(string absolutePath, out AuditFileSystemEntry? entry, out string? error);

    bool TryEnumerate(
        string absolutePath,
        CancellationToken cancellationToken,
        out IReadOnlyList<AuditFileSystemEntry> entries,
        out string? error);
}

internal sealed class AuditFileSystem(IFileTimestampProvider? timestampProvider = null) : IAuditFileSystem
{
    public bool TryInspect(string absolutePath, out AuditFileSystemEntry? entry, out string? error)
    {
        try
        {
            var attributes = File.GetAttributes(absolutePath);
            FileSystemInfo information = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(absolutePath)
                : new FileInfo(absolutePath);
            var kind = Classify(attributes, information);
            var length = kind == FileSystemEntryKind.RegularFile
                ? ((FileInfo)information).Length
                : 0;
            DateTimeOffset? lastWriteTimeUtc = null;
            if (kind is FileSystemEntryKind.RegularFile or FileSystemEntryKind.Directory)
            {
                lastWriteTimeUtc = timestampProvider is null
                    ? new DateTimeOffset(information.LastWriteTimeUtc)
                    : timestampProvider.TryGetLastWriteTimeUtc(absolutePath, out var timestamp)
                        ? timestamp
                        : null;
            }

            entry = new AuditFileSystemEntry(
                information.Name,
                Path.GetFullPath(absolutePath),
                kind,
                length,
                lastWriteTimeUtc,
                null);
            error = null;
            return true;
        }
        catch (Exception exception) when (IsPathLocalFailure(exception))
        {
            entry = null;
            error = $"Unable to inspect filesystem entry: {exception.Message}";
            return false;
        }
    }

    public bool TryEnumerate(
        string absolutePath,
        CancellationToken cancellationToken,
        out IReadOnlyList<AuditFileSystemEntry> entries,
        out string? error)
    {
        var snapshots = new List<AuditFileSystemEntry>();
        try
        {
            foreach (var childPath in Directory.EnumerateFileSystemEntries(absolutePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryInspect(childPath, out var child, out var inspectionError))
                {
                    snapshots.Add(child!);
                }
                else
                {
                    snapshots.Add(new AuditFileSystemEntry(
                        Path.GetFileName(childPath),
                        Path.GetFullPath(childPath),
                        FileSystemEntryKind.Other,
                        0,
                        null,
                        inspectionError));
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            entries = Array.AsReadOnly(snapshots.ToArray());
            error = null;
            return true;
        }
        catch (Exception exception) when (IsPathLocalFailure(exception))
        {
            entries = Array.Empty<AuditFileSystemEntry>();
            error = $"Unable to enumerate directory: {exception.Message}";
            return false;
        }
    }

    internal static string NormalizeRootPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var filesystemRoot = Path.GetPathRoot(fullPath);
        if (filesystemRoot is not null &&
            string.Equals(
                fullPath,
                filesystemRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static FileSystemEntryKind Classify(FileAttributes attributes, FileSystemInfo information)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 || information.LinkTarget is not null)
        {
            return FileSystemEntryKind.Link;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return FileSystemEntryKind.Directory;
        }

        return (attributes & FileAttributes.Device) != 0
            ? FileSystemEntryKind.Other
            : FileSystemEntryKind.RegularFile;
    }

    private static bool IsPathLocalFailure(Exception exception) =>
        exception is UnauthorizedAccessException or IOException;
}
