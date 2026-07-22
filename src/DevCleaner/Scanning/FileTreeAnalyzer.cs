namespace DevCleaner.Scanning;

public sealed record FileTreeAnalysis(
    bool IsSafe,
    long FileCount,
    long EstimatedBytes,
    FileSystemIdentity? Identity,
    IReadOnlyList<OperationWarning> Warnings);

public sealed class FileTreeAnalyzer
{
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
            return new FileTreeAnalysis(false, 0, 0, null, warnings);
        }

        if (!TryCaptureIdentity(fullPath, warnings, out var identity) || identity is null)
        {
            return new FileTreeAnalysis(false, 0, 0, null, warnings);
        }

        if ((identity.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            warnings.Add(new OperationWarning(fullPath, "Candidate is a filesystem link or reparse point."));
            return new FileTreeAnalysis(false, 0, 0, identity, warnings);
        }

        if ((identity.Attributes & FileAttributes.Directory) == 0)
        {
            return new FileTreeAnalysis(true, 1, identity.Length, identity, warnings);
        }

        long fileCount = 0;
        long estimatedBytes = 0;
        var pending = new Stack<string>();
        pending.Push(fullPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IReadOnlyList<string> entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add(new OperationWarning(directory, $"Unable to analyze candidate: {exception.Message}"));
                return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, warnings);
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(new OperationWarning(entry, "Candidate contains a nested repository boundary."));
                    return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, warnings);
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    warnings.Add(new OperationWarning(entry, $"Unable to analyze candidate entry: {exception.Message}"));
                    return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, warnings);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                try
                {
                    fileCount = SaturatingAdd(fileCount, 1);
                    estimatedBytes = SaturatingAdd(estimatedBytes, new FileInfo(entry).Length);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    warnings.Add(new OperationWarning(entry, $"Unable to read candidate file length: {exception.Message}"));
                    return new FileTreeAnalysis(false, fileCount, estimatedBytes, identity, warnings);
                }
            }
        }

        return new FileTreeAnalysis(true, fileCount, estimatedBytes, identity, warnings);
    }

    private static bool TryCaptureIdentity(string path, List<OperationWarning> warnings, out FileSystemIdentity? identity)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            var length = info is FileInfo file ? file.Length : 0;
            identity = new FileSystemIdentity(attributes, info.CreationTimeUtc, info.LastWriteTimeUtc, length, info.LinkTarget);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            warnings.Add(new OperationWarning(path, $"Unable to capture filesystem identity: {exception.Message}"));
            identity = null;
            return false;
        }
    }

    internal static long SaturatingAdd(long left, long right) => right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;
}
