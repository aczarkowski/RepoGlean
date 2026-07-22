using System.Text.RegularExpressions;
using DevCleaner.Cli;

namespace DevCleaner.Rules;

public sealed record ArtifactRule(
    string Id,
    ArtifactCategory Category,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Markers,
    bool Preselected)
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public bool Matches(string repositoryRelativePath) => Patterns.Any(pattern => GlobMatcher.IsMatch(pattern, repositoryRelativePath));

    public bool IsActiveFor(IReadOnlyList<string> visiblePaths) =>
        Markers.Count == 0 || visiblePaths.Any(path => Markers.Any(marker => GlobMatcher.IsMatch(marker, path)));

    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id);
}
