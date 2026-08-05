namespace RepoGlean.Progress;

internal sealed class VerboseProgressRenderer(TextWriter writer) : IOperationProgress
{
    private readonly object sync = new();
    private bool disabled;

    public void Report(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        var line = Format(progressEvent);
        if (line is null) return;

        lock (sync)
        {
            if (disabled) return;

            try
            {
                var originalNewLine = writer.NewLine;
                try
                {
                    writer.NewLine = "\n";
                    writer.WriteLine(line);
                }
                finally
                {
                    writer.NewLine = originalNewLine;
                }
            }
            catch (IOException)
            {
                disabled = true;
            }
            catch (ObjectDisposedException)
            {
                disabled = true;
            }
        }
    }

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string? Format(OperationProgressEvent progressEvent) => progressEvent.Kind switch
    {
        ProgressEventKind.DiscoveryStarted => $"Discovering repositories under {ProgressText.FormatRoots(progressEvent.Roots)}...",
        ProgressEventKind.DiscoveryCompleted => $"Found {progressEvent.RepositoryCount} {ProgressText.Plural(progressEvent.RepositoryCount, "repository", "repositories")}.",
        ProgressEventKind.RepositoryScanStarted when progressEvent.Operation == ProgressOperation.Audit =>
            $"Auditing [{progressEvent.Current}/{progressEvent.Total}] {DisplayValue(progressEvent.Path)}...",
        ProgressEventKind.RepositoryScanStarted => $"Scanning [{progressEvent.Current}/{progressEvent.Total}] {DisplayValue(progressEvent.Path)}...",
        ProgressEventKind.RepositoryScanCompleted when
            progressEvent.Operation == ProgressOperation.Audit &&
            progressEvent.CurrentFindingCount > 0 =>
            $"Found {progressEvent.CurrentFindingCount} {ProgressText.Plural(progressEvent.CurrentFindingCount, "finding", "findings")} in {ProgressText.DisplayPath(progressEvent.Path)} ({ProgressText.FormatBytes(progressEvent.CurrentEstimatedBytes)}).",
        ProgressEventKind.RepositoryScanCompleted when progressEvent.CurrentCandidateCount > 0 =>
            $"Found {progressEvent.CurrentCandidateCount} {ProgressText.Plural(progressEvent.CurrentCandidateCount, "candidate", "candidates")} in {ProgressText.DisplayPath(progressEvent.Path)} ({ProgressText.FormatBytes(progressEvent.CurrentEstimatedBytes)}).",
        ProgressEventKind.CandidateStarted => $"Validating [{progressEvent.Current}/{progressEvent.Total}] {DisplayValue(progressEvent.Path)}...",
        ProgressEventKind.CandidateCompleted => FormatCandidateOutcome(progressEvent),
        ProgressEventKind.Warning => $"Warning: {DisplayValue(progressEvent.Path)}: {DisplayValue(progressEvent.Message, "(no details)")}",
        ProgressEventKind.Completed => FormatCompleted(progressEvent),
        ProgressEventKind.Interrupted => FormatInterrupted(progressEvent),
        ProgressEventKind.Failed => FormatFailed(progressEvent),
        _ => null,
    };

    private static string FormatCandidateOutcome(OperationProgressEvent progressEvent)
    {
        var verb = progressEvent.Outcome switch
        {
            ProgressCandidateOutcome.Deleted => "Deleted",
            ProgressCandidateOutcome.Validated => "Validated",
            ProgressCandidateOutcome.Skipped => "Skipped",
            ProgressCandidateOutcome.Failed => "Failed",
            _ => "Processed",
        };

        var path = FormatCandidatePath(progressEvent.Path);
        return progressEvent.CurrentEstimatedBytes > 0
            ? $"{verb} {path} ({ProgressText.FormatBytes(progressEvent.CurrentEstimatedBytes)})."
            : $"{verb} {path}.";
    }

    private static string FormatCompleted(OperationProgressEvent progressEvent) => progressEvent.Operation switch
    {
        ProgressOperation.Scan => $"Scan complete: {progressEvent.RepositoryCount} {ProgressText.Plural(progressEvent.RepositoryCount, "repository", "repositories")}, {progressEvent.CandidateCount} {ProgressText.Plural(progressEvent.CandidateCount, "candidate", "candidates")}, {progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}.",
        ProgressOperation.Audit => $"Audit complete: {progressEvent.RepositoryCount} {ProgressText.Plural(progressEvent.RepositoryCount, "repository", "repositories")}, {progressEvent.FindingCount} {ProgressText.Plural(progressEvent.FindingCount, "finding", "findings")}, {progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}.",
        ProgressOperation.Plan => $"Plan complete: {progressEvent.CandidateCount} {ProgressText.Plural(progressEvent.CandidateCount, "candidate", "candidates")} selected, {ProgressText.FormatBytes(progressEvent.EstimatedBytes)} planned, {progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}.",
        _ => $"Cleanup complete: {CleanupAggregate(progressEvent)}.",
    };

    private static string FormatInterrupted(OperationProgressEvent progressEvent) => progressEvent.Operation switch
    {
        ProgressOperation.Scan => $"Scan interrupted: {progressEvent.RepositoryCount} {ProgressText.Plural(progressEvent.RepositoryCount, "repository", "repositories")}, {progressEvent.CandidateCount} {ProgressText.Plural(progressEvent.CandidateCount, "candidate", "candidates")}, {progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}.",
        ProgressOperation.Audit => $"Audit interrupted: {progressEvent.RepositoryCount} {ProgressText.Plural(progressEvent.RepositoryCount, "repository", "repositories")}, {progressEvent.FindingCount} {ProgressText.Plural(progressEvent.FindingCount, "finding", "findings")}, {ProgressText.FormatBytes(progressEvent.EstimatedBytes)}, {progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}.",
        ProgressOperation.Plan => $"Plan interrupted: {progressEvent.RepositoryCount} {ProgressText.Plural(progressEvent.RepositoryCount, "repository", "repositories")}, {progressEvent.CandidateCount} {ProgressText.Plural(progressEvent.CandidateCount, "candidate", "candidates")}, {ProgressText.FormatBytes(progressEvent.EstimatedBytes)}, {progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}.",
        _ => $"Cleanup interrupted: {CleanupAggregate(progressEvent)}.",
    };

    private static string FormatFailed(OperationProgressEvent progressEvent)
    {
        var operation = progressEvent.Operation switch
        {
            ProgressOperation.Scan => "Scan",
            ProgressOperation.Audit => "Audit",
            ProgressOperation.Plan => "Plan",
            _ => "Cleanup",
        };
        return string.IsNullOrWhiteSpace(progressEvent.Message)
            ? $"{operation} failed."
            : $"{operation} failed: {ProgressText.Sanitize(progressEvent.Message)}";
    }

    private static string CleanupAggregate(OperationProgressEvent progressEvent) =>
        $"{progressEvent.DeletedCount} {ProgressText.Plural(progressEvent.DeletedCount, "deleted", "deleted")}, " +
        $"{progressEvent.ValidatedCount} {ProgressText.Plural(progressEvent.ValidatedCount, "validated", "validated")}, " +
        $"{progressEvent.SkippedCount} {ProgressText.Plural(progressEvent.SkippedCount, "skipped", "skipped")}, " +
        $"{progressEvent.FailedCount} {ProgressText.Plural(progressEvent.FailedCount, "failed", "failed")}, " +
        $"{progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}";

    private static string FormatCandidatePath(string? path)
    {
        var sanitized = ProgressText.Sanitize(path);
        if (string.IsNullOrWhiteSpace(sanitized)) return "(unknown)";

        var trimmed = sanitized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var parentName = ProgressText.DisplayPath(parent);
        var name = ProgressText.DisplayPath(trimmed);
        return parentName == "(unknown)" ? name : $"{parentName}/{name}";
    }

    private static string DisplayValue(string? value, string fallback = "(unknown)")
    {
        var sanitized = ProgressText.Sanitize(value);
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
