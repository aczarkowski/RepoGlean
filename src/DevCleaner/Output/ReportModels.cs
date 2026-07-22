using DevCleaner.Cli;
using DevCleaner.Configuration;
using DevCleaner.Rules;
using DevCleaner.Scanning;

namespace DevCleaner.Output;

public static class ReportSchema
{
    public const int CurrentVersion = 1;
}

public sealed record ReportMessage(string Path, string Message);

public sealed record ReportTotals(
    long RepositoryCount,
    long CandidateCount,
    long FileCount,
    long EstimatedBytes);

public sealed record CandidateReport(
    string AbsolutePath,
    string RelativePath,
    string RuleId,
    string Category,
    bool Preselected,
    long FileCount,
    long EstimatedBytes);

public sealed record RepositoryReport(
    string Root,
    IReadOnlyList<CandidateReport> Candidates,
    long FileCount,
    long EstimatedBytes);

public sealed record RuleReport(
    string Id,
    string Category,
    string Source,
    bool Enabled,
    bool Preselected,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Markers);

public sealed record ReportDocument(
    int SchemaVersion,
    string Operation,
    string Status,
    IReadOnlyList<string> EffectiveRoots,
    IReadOnlyList<RepositoryReport> Repositories,
    ReportTotals Totals,
    IReadOnlyList<ReportMessage> Warnings,
    IReadOnlyList<ReportMessage> Errors,
    IReadOnlyList<RuleReport>? Rules = null,
    DevCleanerConfig? Configuration = null,
    string? ConfigurationPath = null)
{
    public static ReportDocument FromScan(IReadOnlyList<string> effectiveRoots, ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(effectiveRoots);
        ArgumentNullException.ThrowIfNull(result);
        var repositories = result.Repositories
            .OrderByDescending(repository => repository.EstimatedBytes)
            .ThenBy(repository => repository.RepositoryRoot, PathComparer)
            .Select(repository => new RepositoryReport(
                repository.RepositoryRoot,
                Array.AsReadOnly(repository.Candidates
                    .OrderByDescending(candidate => candidate.EstimatedBytes)
                    .ThenBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
                    .Select(candidate => new CandidateReport(
                        candidate.AbsolutePath,
                        candidate.RelativePath,
                        candidate.RuleId,
                        FormatCategory(candidate.Category),
                        candidate.Preselected,
                        candidate.FileCount,
                        candidate.EstimatedBytes))
                    .ToArray()),
                repository.FileCount,
                repository.EstimatedBytes))
            .ToArray();
        var warnings = result.Warnings.Select(warning => new ReportMessage(warning.Path, warning.Message)).ToArray();
        return new ReportDocument(
            ReportSchema.CurrentVersion,
            "scan",
            warnings.Length == 0 ? "success" : "partial",
            Array.AsReadOnly(effectiveRoots.ToArray()),
            Array.AsReadOnly(repositories),
            new ReportTotals(repositories.LongLength, repositories.Sum(repository => (long)repository.Candidates.Count), result.FileCount, result.EstimatedBytes),
            Array.AsReadOnly(warnings),
            []);
    }

    public static ReportDocument FromRules(DevCleanerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var disabled = new HashSet<string>(config.DisabledRules, StringComparer.OrdinalIgnoreCase);
        var rules = BuiltInRules.All
            .Select(rule => ToRuleReport(rule, "builtIn", !disabled.Contains(rule.Id), rule.Preselected))
            .Concat(config.CustomRules.Select(rule => ToRuleReport(rule, "custom", true, false)))
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
        return Empty("rules.list") with { Rules = Array.AsReadOnly(rules) };
    }

    public static ReportDocument Failure(string operation, string message) =>
        Empty(operation) with
        {
            Status = "failed",
            Errors = [new ReportMessage(string.Empty, message)],
        };

    public static ReportDocument Interrupted(string operation) =>
        Empty(operation) with
        {
            Status = "interrupted",
            Errors = [new ReportMessage(string.Empty, "Operation interrupted.")],
        };

    private static ReportDocument Empty(string operation) => new(
        ReportSchema.CurrentVersion,
        operation,
        "success",
        [],
        [],
        new ReportTotals(0, 0, 0, 0),
        [],
        []);

    private static RuleReport ToRuleReport(ArtifactRule rule, string source, bool enabled, bool preselected) => new(
        rule.Id,
        FormatCategory(rule.Category),
        source,
        enabled,
        preselected,
        Array.AsReadOnly(rule.Patterns.ToArray()),
        Array.AsReadOnly(rule.Markers.ToArray()));

    private static string FormatCategory(ArtifactCategory category) => category.ToString().ToLowerInvariant();

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
