using RepoGlean.Output;

namespace RepoGlean.Progress;

internal static class ProgressText
{
    public static string FormatBytes(long bytes) => HumanReportWriter.FormatBytes(bytes);

    public static string FormatRoots(IReadOnlyList<string>? roots) =>
        roots is null || roots.Count == 0 ? "(default root)" : string.Join(", ", roots.Select(Sanitize));

    public static string DisplayPath(string? path)
    {
        var sanitized = Sanitize(path);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "(unknown)";
        }

        var trimmed = sanitized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? sanitized : name;
    }

    public static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : string.Concat(value.Where(static character => !char.IsControl(character)));

    public static string Plural(long count, string singular, string plural) =>
        count == 1 ? singular : plural;
}
