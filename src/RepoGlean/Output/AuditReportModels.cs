using System.Text.Json.Serialization;
using RepoGlean.Auditing;
using RepoGlean.Scanning;

namespace RepoGlean.Output;

public sealed record AuditFindingReport(
    string AbsolutePath,
    string RelativePath,
    long FileCount,
    long EstimatedBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    DateTimeOffset? NewestWriteTimeUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? IgnoreSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    int? IgnoreSourceLine,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? IgnorePattern);

public sealed record AuditRepositoryReport(
    string Root,
    IReadOnlyList<AuditFindingReport> Findings,
    long FileCount,
    long EstimatedBytes);

public sealed record AuditTotalsReport(
    long RepositoryCount,
    long FindingCount,
    long FileCount,
    long EstimatedBytes);

public sealed record AuditReportDocument(
    int SchemaVersion,
    string Operation,
    string Status,
    IReadOnlyList<string> EffectiveRoots,
    IReadOnlyList<AuditRepositoryReport> Repositories,
    AuditTotalsReport Totals,
    IReadOnlyList<ReportMessage> Warnings,
    IReadOnlyList<ReportMessage> Errors)
{
    public static AuditReportDocument FromAudit(IReadOnlyList<string> effectiveRoots, AuditResult result)
    {
        ArgumentNullException.ThrowIfNull(effectiveRoots);
        ArgumentNullException.ThrowIfNull(result);

        var repositories = result.Repositories
            .OrderByDescending(repository => repository.EstimatedBytes)
            .ThenBy(repository => repository.RepositoryRoot, PathComparer)
            .Select(repository => new AuditRepositoryReport(
                repository.RepositoryRoot,
                Array.AsReadOnly(repository.Findings
                    .OrderByDescending(finding => finding.EstimatedBytes)
                    .ThenBy(finding => finding.RelativePath, StringComparer.Ordinal)
                    .Select(finding => new AuditFindingReport(
                        finding.AbsolutePath,
                        finding.RelativePath,
                        finding.FileCount,
                        finding.EstimatedBytes,
                        finding.NewestWriteTimeUtc,
                        NormalizeIgnoreSource(repository.RepositoryRoot, finding.Ignore.Source),
                        finding.Ignore.SourceLine,
                        finding.Ignore.Pattern))
                    .ToArray()),
                repository.FileCount,
                repository.EstimatedBytes))
            .ToArray();
        var warnings = result.Warnings
            .Select(warning => new ReportMessage(warning.Path, warning.Message))
            .ToArray();

        return new AuditReportDocument(
            ReportSchema.CurrentVersion,
            "audit",
            warnings.Length == 0 ? "success" : "partial",
            Array.AsReadOnly(effectiveRoots.ToArray()),
            Array.AsReadOnly(repositories),
            new AuditTotalsReport(
                repositories.LongLength,
                repositories.Aggregate(0L, static (total, repository) => FileTreeAnalyzer.SaturatingAdd(total, repository.Findings.LongCount())),
                result.FileCount,
                result.EstimatedBytes),
            Array.AsReadOnly(warnings),
            []);
    }

    public static AuditReportDocument Interrupted() =>
        Empty() with
        {
            Status = "interrupted",
            Errors = [new ReportMessage(string.Empty, "Operation interrupted.")],
        };

    public static AuditReportDocument Failure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Empty() with
        {
            Status = "failed",
            Errors = [new ReportMessage(string.Empty, message)],
        };
    }

    private static AuditReportDocument Empty() => new(
        ReportSchema.CurrentVersion,
        "audit",
        "success",
        [],
        [],
        new AuditTotalsReport(0, 0, 0, 0),
        [],
        []);

    private static string? NormalizeIgnoreSource(string repositoryRoot, string? source)
    {
        if (string.IsNullOrEmpty(source) || !Path.IsPathRooted(source))
        {
            return source;
        }

        var normalizedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        var normalizedSource = Path.GetFullPath(source);
        var relativeSource = Path.GetRelativePath(normalizedRepositoryRoot, normalizedSource);
        if (!IsOutsideRepository(relativeSource))
        {
            return relativeSource;
        }

        return normalizedSource;
    }

    private static bool IsOutsideRepository(string relativePath) =>
        relativePath.Equals("..", StringComparison.Ordinal) ||
        relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
