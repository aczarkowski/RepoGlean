using System.Text.Json.Serialization;
using RepoGlean.Cli;
using RepoGlean.Cleaning;
using RepoGlean.Configuration;
using RepoGlean.Planning;
using RepoGlean.Rules;
using RepoGlean.Scanning;

namespace RepoGlean.Output;

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
    long EstimatedBytes,
    string? Outcome = null,
    string? Message = null,
    bool? DeletionCompleted = null);

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

public sealed record CleanupSummaryReport(
    long SelectedCount,
    long DeletedCount,
    long SkippedCount,
    long FailedCount,
    long EstimatedDeletedBytes,
    bool DryRun,
    bool Interrupted,
    ReclaimTargetReport? ReclaimTarget = null);

public sealed record PlanningCandidateReport(
    string RepositoryRoot,
    string AbsolutePath,
    string RelativePath,
    string RuleId,
    string Category,
    bool Preselected,
    long FileCount,
    long EstimatedBytes,
    int? PlanningOrder,
    int DisruptionTier,
    string RecencyBand,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    DateTimeOffset? NewestWriteTimeUtc,
    string PlanningReason);

public sealed record ReclaimPlanReport(
    long RequestedBytes,
    long EligibleBytes,
    long PlannedBytes,
    long OvershootBytes,
    long ShortfallBytes,
    bool TargetMet,
    long SelectedCandidateCount,
    long PreservedCandidateCount,
    IReadOnlyList<PlanningCandidateReport> SelectedCandidates,
    IReadOnlyList<PlanningCandidateReport> PreservedCandidates);

public sealed record ReclaimTargetReport(
    long RequestedBytes,
    long PlannedBytes,
    long ValidatedBytes,
    long CompletedDeletionBytes,
    long AchievedBytes,
    long OvershootBytes,
    long ShortfallBytes,
    bool TargetMet);

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
    RepoGleanConfig? Configuration = null,
    string? ConfigurationPath = null,
    CleanupSummaryReport? Cleanup = null,
    ReclaimPlanReport? Plan = null)
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

    public static ReportDocument FromRules(RepoGleanConfig config)
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

    public static ReportDocument FromCleanup(
        IReadOnlyList<string> effectiveRoots,
        CleanupResult result,
        IReadOnlyList<OperationWarning>? operationWarnings = null,
        ReclaimPlan? reclaimPlan = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveRoots);
        ArgumentNullException.ThrowIfNull(result);
        var repositories = result.Items
            .GroupBy(item => item.Candidate.RepositoryRoot, PathComparer)
            .OrderBy(group => group.Key, PathComparer)
            .Select(group => new RepositoryReport(
                group.Key,
                Array.AsReadOnly(group.Select(item => new CandidateReport(
                        item.Candidate.AbsolutePath,
                        item.Candidate.RelativePath,
                        item.Candidate.RuleId,
                        FormatCategory(item.Candidate.Category),
                        item.Candidate.Preselected,
                        item.Candidate.FileCount,
                        item.Candidate.EstimatedBytes,
                        item.Outcome.ToString().ToLowerInvariant(),
                        item.Message,
                        item.DeletionCompleted))
                    .ToArray()),
                group.Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.FileCount)),
                group.Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.EstimatedBytes))))
            .ToArray();
        var skippedWarnings = result.Items
            .Where(item => item.Outcome == CleanupOutcome.Skipped)
            .Where(item => !(result.DryRun && item.Message.StartsWith("Validated; dry run", StringComparison.Ordinal)))
            .Select(item => new ReportMessage(item.Candidate.AbsolutePath, item.Message))
            .ToArray();
        var warnings = (operationWarnings ?? [])
            .Select(warning => new ReportMessage(warning.Path, warning.Message))
            .Concat(skippedWarnings)
            .ToArray();
        var errors = result.Items
            .Where(item => item.Outcome == CleanupOutcome.Failed)
            .Select(item => new ReportMessage(item.Candidate.AbsolutePath, item.Message))
            .ToArray();
        ReclaimTargetReport? reclaimTarget = null;
        if (reclaimPlan is not null)
        {
            var validatedBytes = result.EstimatedValidatedBytes;
            var completedDeletionBytes = result.EstimatedDeletedBytes;
            var achievedBytes = result.DryRun
                ? validatedBytes
                : completedDeletionBytes;
            var targetMet = achievedBytes >= reclaimPlan.RequestedBytes;
            reclaimTarget = new ReclaimTargetReport(
                reclaimPlan.RequestedBytes,
                reclaimPlan.PlannedBytes,
                validatedBytes,
                completedDeletionBytes,
                achievedBytes,
                targetMet
                    ? SaturatingNonNegativeDifference(
                        achievedBytes,
                        reclaimPlan.RequestedBytes)
                    : 0,
                targetMet
                    ? 0
                    : SaturatingNonNegativeDifference(
                        reclaimPlan.RequestedBytes,
                        achievedBytes),
                targetMet);
        }

        var hasReclaimShortfall =
            reclaimPlan is not null &&
            (!reclaimPlan.TargetMet || reclaimTarget is not { TargetMet: true });
        var status = result.IsInterrupted
            ? "interrupted"
            : warnings.Length > 0 || errors.Length > 0 || hasReclaimShortfall
                ? "partial"
                : "success";
        var selectedFileCount = result.Items.Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.FileCount));
        var selectedBytes = result.Items.Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.EstimatedBytes));
        return new ReportDocument(
            ReportSchema.CurrentVersion,
            "clean",
            status,
            Array.AsReadOnly(effectiveRoots.ToArray()),
            Array.AsReadOnly(repositories),
            new ReportTotals(repositories.LongLength, result.Items.Count, selectedFileCount, selectedBytes),
            Array.AsReadOnly(warnings),
            Array.AsReadOnly(errors),
            Cleanup: new CleanupSummaryReport(
                result.SelectedCount,
                result.DeletedCount,
                result.SkippedCount,
                result.FailedCount,
                result.EstimatedDeletedBytes,
                result.DryRun,
                result.IsInterrupted,
                reclaimTarget));
    }

    public static ReportDocument FromPlan(
        IReadOnlyList<string> effectiveRoots,
        ReclaimPlan plan,
        IReadOnlyList<OperationWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(effectiveRoots);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(warnings);
        var allCandidates = plan.SelectedCandidates.Concat(plan.PreservedCandidates).ToArray();
        var repositories = allCandidates
            .GroupBy(item => item.Candidate.RepositoryRoot, PathComparer)
            .OrderBy(group => group.Key, PathComparer)
            .Select(group => new RepositoryReport(
                group.Key,
                Array.AsReadOnly(group.Select(item => ToCandidateReport(item.Candidate)).ToArray()),
                group.Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.FileCount)),
                group.Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.EstimatedBytes))))
            .ToArray();
        var reportWarnings = warnings
            .Select(warning => new ReportMessage(warning.Path, warning.Message))
            .ToArray();
        var selected = plan.SelectedCandidates.Select(ToPlanningCandidateReport).ToArray();
        var preserved = plan.PreservedCandidates.Select(ToPlanningCandidateReport).ToArray();
        return new ReportDocument(
            ReportSchema.CurrentVersion,
            "plan",
            reportWarnings.Length == 0 && plan.TargetMet ? "success" : "partial",
            Array.AsReadOnly(effectiveRoots.ToArray()),
            Array.AsReadOnly(repositories),
            new ReportTotals(
                repositories.LongLength,
                allCandidates.LongLength,
                allCandidates.Aggregate(0L, (total, item) => FileTreeAnalyzer.SaturatingAdd(total, item.Candidate.FileCount)),
                plan.EligibleBytes),
            Array.AsReadOnly(reportWarnings),
            [],
            Plan: new ReclaimPlanReport(
                plan.RequestedBytes,
                plan.EligibleBytes,
                plan.PlannedBytes,
                plan.OvershootBytes,
                plan.ShortfallBytes,
                plan.TargetMet,
                selected.LongLength,
                preserved.LongLength,
                Array.AsReadOnly(selected),
                Array.AsReadOnly(preserved)));
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

    private static CandidateReport ToCandidateReport(ArtifactCandidate candidate) => new(
        candidate.AbsolutePath,
        candidate.RelativePath,
        candidate.RuleId,
        FormatCategory(candidate.Category),
        candidate.Preselected,
        candidate.FileCount,
        candidate.EstimatedBytes);

    private static PlanningCandidateReport ToPlanningCandidateReport(ReclaimPlanCandidate candidate) => new(
        candidate.Candidate.RepositoryRoot,
        candidate.Candidate.AbsolutePath,
        candidate.Candidate.RelativePath,
        candidate.Candidate.RuleId,
        FormatCategory(candidate.Candidate.Category),
        candidate.Candidate.Preselected,
        candidate.Candidate.FileCount,
        candidate.Candidate.EstimatedBytes,
        candidate.PlanningOrder,
        candidate.DisruptionTier,
        FormatRecencyBand(candidate.RecencyBand),
        candidate.Candidate.NewestWriteTimeUtc,
        candidate.PlanningReason);

    private static string FormatCategory(ArtifactCategory category) => category.ToString().ToLowerInvariant();

    private static string FormatRecencyBand(ReclaimRecencyBand band) => band switch
    {
        ReclaimRecencyBand.Dormant => "dormant",
        ReclaimRecencyBand.Stale => "stale",
        ReclaimRecencyBand.RecentOrUnknown => "recent-or-unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    private static long SaturatingNonNegativeDifference(long minuend, long subtrahend)
    {
        if (minuend <= subtrahend) return 0;
        if (subtrahend < 0 && minuend > long.MaxValue + subtrahend)
            return long.MaxValue;
        return minuend - subtrahend;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
