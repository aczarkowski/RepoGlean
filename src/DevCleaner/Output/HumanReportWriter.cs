namespace DevCleaner.Output;

using DevCleaner.Scanning;

public sealed record HumanReportOptions(bool Details, bool Quiet, bool Verbose, bool UseColor)
{
    public static HumanReportOptions Default { get; } = new(false, false, false, false);
}

public static class HumanReportWriter
{
    public static void WriteScan(ReportDocument report, TextWriter output, HumanReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        options ??= HumanReportOptions.Default;

        if (!options.Quiet)
        {
            output.WriteLine($"Roots: {string.Join(", ", report.EffectiveRoots)}");
            if (report.Totals.CandidateCount == 0)
            {
                output.WriteLine("No candidates found.");
            }
            else
            {
                WriteHeading(output, "Estimated     Candidates  Files  Repository", options.UseColor);
                foreach (var repository in report.Repositories)
                {
                    output.WriteLine($"{FormatBytes(repository.EstimatedBytes),-13} {repository.Candidates.Count,10}  {repository.FileCount,5}  {repository.Root}");
                    if (!options.Details && !options.Verbose) continue;
                    foreach (var candidate in repository.Candidates)
                    {
                        output.WriteLine($"  {FormatBytes(candidate.EstimatedBytes),-11} {candidate.RelativePath} [{candidate.RuleId}; {candidate.Category}; preselected={candidate.Preselected.ToString().ToLowerInvariant()}]");
                    }
                }
            }

            if (report.Warnings.Count > 0)
            {
                output.WriteLine();
                output.WriteLine($"Warnings: {report.Warnings.Count}");
                if (options.Verbose)
                {
                    foreach (var warning in report.Warnings) output.WriteLine($"  {warning.Path}: {warning.Message}");
                }
            }
        }

        output.WriteLine($"Total {FormatBytes(report.Totals.EstimatedBytes)} | {report.Totals.CandidateCount} candidates | {report.Totals.FileCount} files | {report.Totals.RepositoryCount} repositories");
    }

    public static void WriteRules(ReportDocument report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("Enabled  Preselected  Category    Source    Rule");
        foreach (var rule in report.Rules ?? [])
        {
            output.WriteLine($"{YesNo(rule.Enabled),-7}  {YesNo(rule.Preselected),-11}  {rule.Category,-10}  {rule.Source,-8}  {rule.Id}");
        }
    }

    public static void WriteRepositorySelection(IReadOnlyList<RepositoryScanResult> repositories, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("Repositories:");
        for (var index = 0; index < repositories.Count; index++)
        {
            var repository = repositories[index];
            output.WriteLine($"  {index + 1}. {repository.RepositoryRoot} ({repository.Candidates.Count} candidates, {FormatBytes(repository.EstimatedBytes)})");
        }
    }

    public static void WriteCandidateSelection(IReadOnlyList<ArtifactCandidate> candidates, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("Artifacts:");
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var defaultLabel = candidate.Preselected ? "default" : "opt-in";
            output.WriteLine($"  {index + 1}. {candidate.RepositoryRoot}: {candidate.RelativePath} [{candidate.Category.ToString().ToLowerInvariant()}; {defaultLabel}; {FormatBytes(candidate.EstimatedBytes)}]");
        }
    }

    public static void WriteCleanup(ReportDocument report, TextWriter output, bool quiet = false) =>
        WriteCleanup(report, output, new HumanReportOptions(false, quiet, false, false));

    public static void WriteCleanup(ReportDocument report, TextWriter output, HumanReportOptions options)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Quiet)
        {
            foreach (var repository in report.Repositories)
            {
                foreach (var candidate in repository.Candidates)
                {
                    output.WriteLine($"{candidate.Outcome}: {candidate.AbsolutePath} ({FormatBytes(candidate.EstimatedBytes)}) - {candidate.Message}");
                }
            }

            WriteMessages(output, "Warnings", report.Warnings, options.Verbose);
            WriteMessages(output, "Errors", report.Errors, options.Verbose);
        }

        var cleanup = report.Cleanup ?? new CleanupSummaryReport(0, 0, 0, 0, 0, false, false);
        var prefix = cleanup.DryRun ? "Dry run" : "Cleanup";
        output.WriteLine(
            $"{prefix}: {cleanup.DeletedCount} deleted, {cleanup.SkippedCount} skipped, {cleanup.FailedCount} failed | " +
            $"{cleanup.SelectedCount} selected, {report.Totals.CandidateCount} processed | {FormatBytes(cleanup.EstimatedDeletedBytes)} deleted");
    }

    private static void WriteMessages(
        TextWriter output,
        string label,
        IReadOnlyList<ReportMessage> messages,
        bool includeDetails)
    {
        if (messages.Count == 0) return;
        output.WriteLine($"{label}: {messages.Count}");
        if (!includeDetails) return;
        foreach (var message in messages) output.WriteLine($"  {message.Path}: {message.Message}");
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        decimal value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var formatted = unit == 0 ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"{formatted} {units[unit]} estimated";
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static void WriteHeading(TextWriter output, string heading, bool useColor)
    {
        if (useColor) output.Write("\u001b[1m");
        output.Write(heading);
        if (useColor) output.Write("\u001b[0m");
        output.WriteLine();
    }
}
