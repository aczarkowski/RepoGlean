namespace DevCleaner.Output;

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
