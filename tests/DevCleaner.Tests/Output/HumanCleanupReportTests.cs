using DevCleaner.Output;

namespace DevCleaner.Tests.Output;

public sealed class HumanCleanupReportTests
{
    [Fact]
    public void Partial_cleanup_reports_counts_verbose_details_and_a_quiet_summary()
    {
        var report = new ReportDocument(
            ReportSchema.CurrentVersion,
            "clean",
            "partial",
            ["/root"],
            [],
            new ReportTotals(0, 0, 0, 0),
            [new ReportMessage("/warning", "warning detail")],
            [new ReportMessage("/error", "error detail")],
            Cleanup: new CleanupSummaryReport(1, 0, 0, 1, 0, false, false));
        using var standard = new StringWriter();
        using var verbose = new StringWriter();
        using var quiet = new StringWriter();

        HumanReportWriter.WriteCleanup(report, standard, new HumanReportOptions(false, false, false, false));
        HumanReportWriter.WriteCleanup(report, verbose, new HumanReportOptions(false, false, true, false));
        HumanReportWriter.WriteCleanup(report, quiet, new HumanReportOptions(false, true, true, false));

        Assert.Contains("Warnings: 1", standard.ToString(), StringComparison.Ordinal);
        Assert.Contains("Errors: 1", standard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("warning detail", standard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("error detail", standard.ToString(), StringComparison.Ordinal);
        Assert.Contains("/warning: warning detail", verbose.ToString(), StringComparison.Ordinal);
        Assert.Contains("/error: error detail", verbose.ToString(), StringComparison.Ordinal);
        Assert.Contains("Cleanup:", quiet.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Warnings:", quiet.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Errors:", quiet.ToString(), StringComparison.Ordinal);
    }
}
