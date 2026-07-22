using System.Text.Json;
using DevCleaner.Cli;
using DevCleaner.Cleaning;
using DevCleaner.Output;
using DevCleaner.Scanning;

namespace DevCleaner.Tests.Output;

public sealed class ReportWriterTests
{
    [Fact]
    public void Human_report_is_size_first_marks_estimates_and_emits_details_on_request()
    {
        var report = CreateReport();
        using var output = new StringWriter();

        HumanReportWriter.WriteScan(report, output, new HumanReportOptions(Details: true, Quiet: false, Verbose: false, UseColor: false));

        var text = output.ToString();
        Assert.True(text.IndexOf("large", StringComparison.Ordinal) < text.IndexOf("small", StringComparison.Ordinal));
        Assert.Contains("12 B estimated", text);
        Assert.Contains("obj", text);
        Assert.Contains("dotnet.obj", text);
        Assert.DoesNotContain("\u001b[", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Human_report_quiet_mode_keeps_only_the_total_summary()
    {
        using var output = new StringWriter();

        HumanReportWriter.WriteScan(CreateReport(), output, new HumanReportOptions(Details: true, Quiet: true, Verbose: true, UseColor: false));

        var text = output.ToString();
        Assert.Contains("Total", text);
        Assert.DoesNotContain("large", text);
        Assert.DoesNotContain("warning detail", text);
    }

    [Fact]
    public async Task Json_report_is_versioned_uses_integer_bytes_and_contains_complete_scan_shape()
    {
        using var output = new StringWriter();

        await JsonReportWriter.WriteAsync(CreateReport(), output);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("scan", root.GetProperty("operation").GetString());
        Assert.Equal("partial", root.GetProperty("status").GetString());
        Assert.Equal("/root", root.GetProperty("effectiveRoots")[0].GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("totals").GetProperty("estimatedBytes").ValueKind);
        Assert.Equal(12, root.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
        Assert.Equal("obj", root.GetProperty("repositories")[0].GetProperty("candidates")[0].GetProperty("relativePath").GetString());
        Assert.Equal(1, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
        Assert.DoesNotContain("\u001b[", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dry_run_report_distinguishes_validated_candidates_from_safety_skips()
    {
        var identity = new FileSystemIdentity(1, 2, "mount", FileAttributes.Directory, null);
        var candidate = new ArtifactCandidate("/repos/sample", "/repos/sample/obj", "obj", "dotnet.obj", ArtifactCategory.Build, true, 1, 5, identity);
        var validated = ReportDocument.FromCleanup(
            ["/repos"],
            new CleanupResult([new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, "Validated; dry run did not delete the candidate.")], true, false));
        var rejected = ReportDocument.FromCleanup(
            ["/repos"],
            new CleanupResult([new CleanupCandidateResult(candidate, CleanupOutcome.Skipped, "Candidate filesystem identity changed after the scan.")], true, false));

        Assert.Equal("success", validated.Status);
        Assert.Empty(validated.Warnings);
        Assert.Equal("partial", rejected.Status);
        Assert.Single(rejected.Warnings);
    }

    private static ReportDocument CreateReport()
    {
        var identity = new FileSystemIdentity(1, 2, "mount", FileAttributes.Directory, null);
        var small = new RepositoryScanResult(
            "/repos/small",
            [new ArtifactCandidate("/repos/small", "/repos/small/obj", "obj", "dotnet.obj", ArtifactCategory.Build, true, 1, 2, identity)],
            1,
            2,
            []);
        var large = new RepositoryScanResult(
            "/repos/large",
            [new ArtifactCandidate("/repos/large", "/repos/large/obj", "obj", "dotnet.obj", ArtifactCategory.Build, true, 2, 10, identity)],
            2,
            10,
            []);
        var warning = new OperationWarning("/warning", "warning detail");
        return ReportDocument.FromScan(["/root"], new ScanResult([small, large], 3, 12, [warning]));
    }
}
