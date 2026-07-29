namespace RepoGlean.Scanning;

internal interface IFileTimestampProvider
{
    bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value);
}

internal sealed class FileTimestampProvider : IFileTimestampProvider
{
    public bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value)
    {
        try
        {
            value = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            value = default;
            return false;
        }
    }
}
