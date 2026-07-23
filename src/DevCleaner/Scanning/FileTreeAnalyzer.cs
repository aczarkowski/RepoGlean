namespace DevCleaner.Scanning;

public sealed record FileTreeAnalysis(
    bool IsSafe,
    long FileCount,
    long EstimatedBytes,
    FileSystemIdentity? Identity,
    FileSystemIdentity? RepositoryIdentity,
    IReadOnlyList<OperationWarning> Warnings);

public sealed class FileTreeAnalyzer
{
    private readonly IFileSystemIdentityProvider identityProvider;

    public FileTreeAnalyzer()
        : this(new FileSystemIdentityProvider())
    {
    }

    internal FileTreeAnalyzer(IFileSystemIdentityProvider identityProvider)
    {
        this.identityProvider = identityProvider;
    }

    public FileTreeAnalysis Analyze(string path, string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var warnings = new List<OperationWarning>();
        var fullPath = Path.GetFullPath(path);
        var fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
        if (!RepositoryDiscovery.IsSameOrDescendant(fullPath, fullRepositoryRoot))
        {
            warnings.Add(new OperationWarning(fullPath, "Candidate is outside its repository root."));
            return new FileTreeAnalysis(false, 0, 0, null, null, warnings);
        }

        if (!identityProvider.TryGetIdentity(fullRepositoryRoot, out var repositoryIdentity, out var repositoryIdentityError) || repositoryIdentity is null)
        {
            warnings.Add(new OperationWarning(fullRepositoryRoot, repositoryIdentityError ?? "Repository mount identity is unavailable."));
            return new FileTreeAnalysis(false, 0, 0, null, null, warnings);
        }

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(fullPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            warnings.Add(new OperationWarning(fullPath, $"Unable to inspect candidate: {exception.Message}"));
            return new FileTreeAnalysis(false, 0, 0, null, repositoryIdentity, warnings);
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            warnings.Add(new OperationWarning(fullPath, "Candidate is a filesystem link or reparse point."));
            return new FileTreeAnalysis(false, 0, 0, null, repositoryIdentity, warnings);
        }

        if (!identityProvider.TryGetIdentity(fullPath, out var identity, out var identityError) || identity is null)
        {
            warnings.Add(new OperationWarning(fullPath, identityError ?? "Stable filesystem identity is unavailable."));
            return new FileTreeAnalysis(false, 0, 0, null, repositoryIdentity, warnings);
        }

        if (!IsSameMount(identity, repositoryIdentity))
        {
            warnings.Add(new OperationWarning(fullPath, "Candidate crosses the repository filesystem mount boundary."));
            return new FileTreeAnalysis(false, 0, 0, identity, repositoryIdentity, warnings);
        }

        if ((identity.Attributes & FileAttributes.Directory) == 0)
        {
            try
            {
                return new FileTreeAnalysis(true, 1, new FileInfo(fullPath).Length, identity, repositoryIdentity, warnings);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add(new OperationWarning(fullPath, $"Unable to read candidate file length: {exception.Message}"));
                return new FileTreeAnalysis(false, 0, 0, identity, repositoryIdentity, warnings);
            }
        }

        long fileCount = 0;
        long estimatedBytes = 0;
        var pending = new Stack<string>();
        pending.Push(fullPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add(new OperationWarning(entry, "Candidate contains a nested repository boundary."));
                        return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, repositoryIdentity, warnings);
                    }

                    var attributes = File.GetAttributes(entry);

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add(new OperationWarning(entry, "Candidate contains a filesystem link or reparse point."));
                        return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, repositoryIdentity, warnings);
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!identityProvider.TryGetIdentity(entry, out var directoryIdentity, out var directoryIdentityError) || directoryIdentity is null)
                        {
                            warnings.Add(new OperationWarning(entry, directoryIdentityError ?? "Directory mount identity is unavailable."));
                            return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, repositoryIdentity, warnings);
                        }

                        if (!IsSameMount(directoryIdentity, repositoryIdentity))
                        {
                            warnings.Add(new OperationWarning(entry, "Candidate contains a directory on a different filesystem mount."));
                            return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, repositoryIdentity, warnings);
                        }

                        pending.Push(entry);
                        continue;
                    }

                    fileCount = SaturatingAdd(fileCount, 1);
                    estimatedBytes = SaturatingAdd(estimatedBytes, new FileInfo(entry).Length);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add(new OperationWarning(directory, $"Unable to analyze candidate: {exception.Message}"));
                return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, repositoryIdentity, warnings);
            }
        }

        return new FileTreeAnalysis(true, fileCount, estimatedBytes, identity, repositoryIdentity, warnings);
    }

    private static bool IsSameMount(FileSystemIdentity left, FileSystemIdentity right) =>
        left.VolumeId == right.VolumeId && string.Equals(left.MountId, right.MountId, StringComparison.Ordinal);

    internal static long SaturatingAdd(long left, long right) => right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
