using System.Text.Json;
using RepoGlean.Cleaning;
using RepoGlean.Cli;
using RepoGlean.Output;
using RepoGlean.Planning;
using RepoGlean.Scanning;

namespace RepoGlean.Tests.Output;

public sealed class ReclaimReportTests
{
    [Fact]
    public async Task Plan_json_is_version_1_and_exposes_ordering_fields()
    {
        var plan = CreateMetPlan();
        var report = ReportDocument.FromPlan(["/repos"], plan, []);
        using var output = new StringWriter();

        await JsonReportWriter.WriteAsync(report, output);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("plan", root.GetProperty("operation").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
        var planJson = root.GetProperty("plan");
        Assert.Equal(plan.RequestedBytes, planJson.GetProperty("requestedBytes").GetInt64());
        Assert.True(planJson.GetProperty("targetMet").GetBoolean());
        Assert.Equal(1, planJson.GetProperty("selectedCandidates")[0].GetProperty("planningOrder").GetInt32());
        Assert.Equal("dormant", planJson.GetProperty("selectedCandidates")[0].GetProperty("recencyBand").GetString());
        Assert.False(planJson.GetProperty("preservedCandidates")[0].TryGetProperty("planningOrder", out _));
        Assert.Equal(14, root.GetProperty("totals").GetProperty("estimatedBytes").GetInt64());
    }

    [Fact]
    public void Human_plan_always_lists_selected_rows_and_marks_estimated_values()
    {
        var report = ReportDocument.FromPlan(["/repos"], CreateMetPlan(), []);
        using var output = new StringWriter();

        HumanReportWriter.WritePlan(
            report,
            output,
            new HumanReportOptions(false, false, false, false));

        Assert.Contains("Reclaim plan", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Target:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Overshoot:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("test", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("estimated", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whole_pool_shortfall_is_partial_and_has_no_preserved_candidates()
    {
        var metPlan = CreateMetPlan();
        var plan = new ReclaimPlan(
            RequestedBytes: 20,
            EligibleBytes: 14,
            PlannedBytes: 14,
            OvershootBytes: 0,
            ShortfallBytes: 6,
            TargetMet: false,
            SelectedCandidates:
            [
                metPlan.SelectedCandidates[0],
                metPlan.PreservedCandidates[0] with { PlanningOrder = 2 },
            ],
            PreservedCandidates: []);
        var report = ReportDocument.FromPlan(["/repos"], plan, []);
        using var output = new StringWriter();

        await JsonReportWriter.WriteAsync(report, output);

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("partial", root.GetProperty("status").GetString());
        var planJson = root.GetProperty("plan");
        Assert.False(planJson.GetProperty("targetMet").GetBoolean());
        Assert.Equal(6, planJson.GetProperty("shortfallBytes").GetInt64());
        Assert.Equal(0, planJson.GetProperty("preservedCandidates").GetArrayLength());
    }

    [Fact]
    public void Quiet_human_plan_keeps_selected_rows_and_summary_only()
    {
        var report = ReportDocument.FromPlan(
            ["/repos"],
            CreateMetPlan(),
            [new OperationWarning("/repos/warning", "warning detail")]);
        using var output = new StringWriter();

        HumanReportWriter.WritePlan(
            report,
            output,
            new HumanReportOptions(false, true, true, false));

        var text = output.ToString();
        Assert.Contains("TestResults", text, StringComparison.Ordinal);
        Assert.Contains("Target:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Roots:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("warning detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain("obj", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_reclaim_target_values_are_source_generated_while_ordinary_cleanup_omits_them()
    {
        var dryRun = CreateCleanupReport(
            new ReclaimTargetReport(10, 12, 12, 0, 0, 2, 0, true),
            dryRun: true);
        var live = CreateCleanupReport(
            new ReclaimTargetReport(10, 12, 12, 12, 12, 2, 0, true),
            dryRun: false);
        var ordinary = ReportDocument.FromCleanup(
            ["/repos"],
            new CleanupResult([], false, false, 0));
        using var dryRunOutput = new StringWriter();
        using var liveOutput = new StringWriter();
        using var ordinaryOutput = new StringWriter();

        await JsonReportWriter.WriteAsync(dryRun, dryRunOutput);
        await JsonReportWriter.WriteAsync(live, liveOutput);
        await JsonReportWriter.WriteAsync(ordinary, ordinaryOutput);

        using var dryRunJson = JsonDocument.Parse(dryRunOutput.ToString());
        using var liveJson = JsonDocument.Parse(liveOutput.ToString());
        using var ordinaryJson = JsonDocument.Parse(ordinaryOutput.ToString());
        Assert.Equal(0, dryRunJson.RootElement.GetProperty("cleanup").GetProperty("reclaimTarget").GetProperty("completedDeletionBytes").GetInt64());
        Assert.Equal(12, liveJson.RootElement.GetProperty("cleanup").GetProperty("reclaimTarget").GetProperty("achievedBytes").GetInt64());
        Assert.False(ordinaryJson.RootElement.GetProperty("cleanup").TryGetProperty("reclaimTarget", out _));
    }

    private static ReportDocument CreateCleanupReport(ReclaimTargetReport reclaimTarget, bool dryRun) => new(
        ReportSchema.CurrentVersion,
        "clean",
        "success",
        ["/repos"],
        [],
        new ReportTotals(0, 0, 0, 0),
        [],
        [],
        Cleanup: new CleanupSummaryReport(1, 0, 0, 0, 0, dryRun, false, reclaimTarget));

    private static ReclaimPlan CreateMetPlan()
    {
        var identity = new FileSystemIdentity(
            1,
            2,
            "mount",
            FileAttributes.Directory,
            LinkTarget: null);
        var selectedCandidate = new ArtifactCandidate(
            "/repos/sample",
            "/repos/sample/TestResults",
            "TestResults",
            "dotnet.test-results",
            ArtifactCategory.Test,
            Preselected: true,
            FileCount: 1,
            EstimatedBytes: 6,
            identity,
            identity,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var preservedCandidate = new ArtifactCandidate(
            "/repos/sample",
            "/repos/sample/obj",
            "obj",
            "dotnet.obj",
            ArtifactCategory.Build,
            Preselected: true,
            FileCount: 1,
            EstimatedBytes: 8,
            identity,
            identity,
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        return new ReclaimPlan(
            RequestedBytes: 5,
            EligibleBytes: 14,
            PlannedBytes: 6,
            OvershootBytes: 1,
            ShortfallBytes: 0,
            TargetMet: true,
            SelectedCandidates:
            [
                new ReclaimPlanCandidate(
                    selectedCandidate,
                    PlanningOrder: 1,
                    DisruptionTier: 0,
                    ReclaimRecencyBand.Dormant,
                    "tier=test; recency=dormant; estimatedBytes=6"),
            ],
            PreservedCandidates:
            [
                new ReclaimPlanCandidate(
                    preservedCandidate,
                    PlanningOrder: null,
                    DisruptionTier: 1,
                    ReclaimRecencyBand.Dormant,
                    "tier=build; recency=dormant; estimatedBytes=8"),
            ]);
    }
}
