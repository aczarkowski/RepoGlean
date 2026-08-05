namespace RepoGlean.Git;

public sealed record GitIgnoreMatch(
    string Path,
    string? Source,
    int? SourceLine,
    string? Pattern)
{
    public bool IsIgnored =>
        !string.IsNullOrEmpty(Pattern) && !Pattern.StartsWith('!');
}
