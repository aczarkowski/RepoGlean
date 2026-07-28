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
        ProgressEventKind.RepositoryScanStarted => $"Scanning [{progressEvent.Current}/{progressEvent.Total}] {progressEvent.Path ?? "(unknown)"}...",
        ProgressEventKind.RepositoryScanCompleted when progressEvent.CurrentCandidateCount > 0 =>
            $"Found {progressEvent.CurrentCandidateCount} {ProgressText.Plural(progressEvent.CurrentCandidateCount, "candidate", "candidates")} in {ProgressText.DisplayPath(progressEvent.Path)} ({ProgressText.FormatBytes(progressEvent.CurrentEstimatedBytes)}).",
        ProgressEventKind.CandidateStarted => $"Validating [{progressEvent.Current}/{progressEvent.Total}] {progressEvent.Path ?? "(unknown)"}...",
        ProgressEventKind.CandidateCompleted => FormatCandidateOutcome(progressEvent),
        ProgressEventKind.Warning => $"Warning: {progressEvent.Path ?? "(unknown)"}: {progressEvent.Message ?? "(no details)"}",
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
        _ => $"Cleanup complete: {CleanupAggregate(progressEvent)}.",
    };

    private static string FormatInterrupted(OperationProgressEvent progressEvent) => progressEvent.Operation switch
    {
        ProgressOperation.Scan => $"Scan interrupted: {progressEvent.RepositoryCount} {ProgressText.Plural(progressEvent.RepositoryCount, "repository", "repositories")}, {progressEvent.CandidateCount} {ProgressText.Plural(progressEvent.CandidateCount, "candidate", "candidates")}, {progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}.",
        _ => $"Cleanup interrupted: {CleanupAggregate(progressEvent)}.",
    };

    private static string FormatFailed(OperationProgressEvent progressEvent)
    {
        var operation = progressEvent.Operation == ProgressOperation.Scan ? "Scan" : "Cleanup";
        return string.IsNullOrWhiteSpace(progressEvent.Message)
            ? $"{operation} failed."
            : $"{operation} failed: {progressEvent.Message}";
    }

    private static string CleanupAggregate(OperationProgressEvent progressEvent) =>
        $"{progressEvent.DeletedCount} {ProgressText.Plural(progressEvent.DeletedCount, "deleted", "deleted")}, " +
        $"{progressEvent.ValidatedCount} {ProgressText.Plural(progressEvent.ValidatedCount, "validated", "validated")}, " +
        $"{progressEvent.SkippedCount} {ProgressText.Plural(progressEvent.SkippedCount, "skipped", "skipped")}, " +
        $"{progressEvent.FailedCount} {ProgressText.Plural(progressEvent.FailedCount, "failed", "failed")}, " +
        $"{progressEvent.WarningCount} {ProgressText.Plural(progressEvent.WarningCount, "warning", "warnings")}";

    private static string FormatCandidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var parentName = ProgressText.DisplayPath(parent);
        var name = ProgressText.DisplayPath(trimmed);
        return parentName == "(unknown)" ? name : $"{parentName}/{name}";
    }
}
