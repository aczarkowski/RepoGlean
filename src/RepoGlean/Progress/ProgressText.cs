using RepoGlean.Output;

namespace RepoGlean.Progress;

internal static class ProgressText
{
    public static string FormatBytes(long bytes) => HumanReportWriter.FormatBytes(bytes);

    public static string FormatRoots(IReadOnlyList<string>? roots) =>
        roots is null || roots.Count == 0 ? "(default root)" : string.Join(", ", roots);

    public static string DisplayPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    public static string Plural(long count, string singular, string plural) =>
        count == 1 ? singular : plural;
}
