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
    public async Task Plan_json_includes_a_null_newest_write_time_for_unknown_recency()
    {
        var metPlan = CreateMetPlan();
        var plan = metPlan with
        {
            SelectedCandidates =
            [
                metPlan.SelectedCandidates[0] with
                {
                    Candidate = metPlan.SelectedCandidates[0].Candidate with { NewestWriteTimeUtc = null },
                },
            ],
        };
        var report = ReportDocument.FromPlan(["/repos"], plan, []);
        using var output = new StringWriter();

        await JsonReportWriter.WriteAsync(report, output);

        using var document = JsonDocument.Parse(output.ToString());
        var candidate = document.RootElement
            .GetProperty("plan")
            .GetProperty("selectedCandidates")[0];
        Assert.True(candidate.TryGetProperty("newestWriteTimeUtc", out var newestWriteTimeUtc));
        Assert.Equal(JsonValueKind.Null, newestWriteTimeUtc.ValueKind);
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
    public void Human_plan_shows_repository_eligible_pool_and_preserved_size_context()
    {
        var report = ReportDocument.FromPlan(["/repos"], CreateMetPlan(), []);
        using var output = new StringWriter();

        HumanReportWriter.WritePlan(
            report,
            output,
            new HumanReportOptions(false, false, false, false));

        var text = output.ToString();
        Assert.Contains("/repos/sample: TestResults", text, StringComparison.Ordinal);
        Assert.Contains("Eligible pool: 14 B estimated", text, StringComparison.Ordinal);
        Assert.Contains("Preserved candidates: 1 | 8 B estimated", text, StringComparison.Ordinal);
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

    [Fact]
    public void Cleanup_target_uses_live_deletion_completion_as_achievement()
    {
        var plan = CreateMetPlan();
        var candidate = plan.SelectedCandidates[0].Candidate;
        var cleanup = new CleanupResult(
            [new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Deleted,
                "Deleted.",
                DeletionCompleted: true)],
            DryRun: false,
            IsInterrupted: false,
            SelectedCount: 1);

        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            reclaimPlan: plan);

        Assert.Equal("success", report.Status);
        var target = Assert.IsType<ReclaimTargetReport>(report.Cleanup!.ReclaimTarget);
        Assert.Equal(0, target.ValidatedBytes);
        Assert.Equal(6, target.CompletedDeletionBytes);
        Assert.Equal(6, target.AchievedBytes);
        Assert.Equal(1, target.OvershootBytes);
        Assert.Equal(0, target.ShortfallBytes);
        Assert.True(target.TargetMet);
    }

    [Fact]
    public void Cleanup_target_counts_post_deletion_failure_but_keeps_partial_status()
    {
        var plan = CreateMetPlan();
        var candidate = plan.SelectedCandidates[0].Candidate;
        var cleanup = new CleanupResult(
            [new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Failed,
                "Payload deleted; empty quarantine cleanup failed.",
                DeletionCompleted: true)],
            DryRun: false,
            IsInterrupted: false,
            SelectedCount: 1);

        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            reclaimPlan: plan);

        Assert.Equal("partial", report.Status);
        var target = Assert.IsType<ReclaimTargetReport>(report.Cleanup!.ReclaimTarget);
        Assert.Equal(6, target.CompletedDeletionBytes);
        Assert.Equal(6, target.AchievedBytes);
        Assert.True(target.TargetMet);
        Assert.Single(report.Errors);
    }

    [Fact]
    public void Cleanup_target_excludes_a_safety_skip_from_achievement()
    {
        var plan = CreateMetPlan();
        var candidate = plan.SelectedCandidates[0].Candidate;
        var cleanup = new CleanupResult(
            [new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Skipped,
                "Candidate filesystem identity changed after the scan.")],
            DryRun: false,
            IsInterrupted: false,
            SelectedCount: 1);

        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            reclaimPlan: plan);

        Assert.Equal("partial", report.Status);
        var target = Assert.IsType<ReclaimTargetReport>(report.Cleanup!.ReclaimTarget);
        Assert.Equal(0, target.AchievedBytes);
        Assert.Equal(5, target.ShortfallBytes);
        Assert.False(target.TargetMet);
        Assert.Single(report.Warnings);
    }

    [Fact]
    public void Cleanup_target_excludes_a_failed_candidate_from_achievement()
    {
        var plan = CreateMetPlan();
        var candidate = plan.SelectedCandidates[0].Candidate;
        var cleanup = new CleanupResult(
            [new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Failed,
                "Cleanup failed.")],
            DryRun: false,
            IsInterrupted: false,
            SelectedCount: 1);

        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            reclaimPlan: plan);

        Assert.Equal("partial", report.Status);
        var target = Assert.IsType<ReclaimTargetReport>(report.Cleanup!.ReclaimTarget);
        Assert.Equal(0, target.CompletedDeletionBytes);
        Assert.Equal(0, target.AchievedBytes);
        Assert.Equal(5, target.ShortfallBytes);
        Assert.False(target.TargetMet);
        Assert.Single(report.Errors);
    }

    [Fact]
    public void Cleanup_target_interruption_uses_processed_achievement_and_preserves_status()
    {
        var plan = CreateMetPlan();
        var cleanup = new CleanupResult(
            [],
            DryRun: false,
            IsInterrupted: true,
            SelectedCount: 1);

        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            reclaimPlan: plan);

        Assert.Equal("interrupted", report.Status);
        var target = Assert.IsType<ReclaimTargetReport>(report.Cleanup!.ReclaimTarget);
        Assert.Equal(0, target.AchievedBytes);
        Assert.Equal(5, target.ShortfallBytes);
        Assert.False(target.TargetMet);
    }

    [Fact]
    public void Cleanup_target_is_partial_when_the_original_plan_had_a_shortfall()
    {
        var metPlan = CreateMetPlan();
        var plan = metPlan with
        {
            RequestedBytes = 10,
            PlannedBytes = 6,
            OvershootBytes = 0,
            ShortfallBytes = 4,
            TargetMet = false,
        };
        var candidate = plan.SelectedCandidates[0].Candidate;
        var cleanup = new CleanupResult(
            [new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Deleted,
                "Deleted.",
                DeletionCompleted: true)],
            DryRun: false,
            IsInterrupted: false,
            SelectedCount: 1);

        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            reclaimPlan: plan);

        Assert.Equal("partial", report.Status);
        var target = Assert.IsType<ReclaimTargetReport>(report.Cleanup!.ReclaimTarget);
        Assert.Equal(6, target.PlannedBytes);
        Assert.Equal(6, target.AchievedBytes);
        Assert.Equal(4, target.ShortfallBytes);
        Assert.False(target.TargetMet);
    }

    [Fact]
    public void Cleanup_target_met_with_an_unrelated_warning_remains_partial()
    {
        var plan = CreateMetPlan();
        var candidate = plan.SelectedCandidates[0].Candidate;
        var cleanup = new CleanupResult(
            [new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Deleted,
                "Deleted.",
                DeletionCompleted: true)],
            DryRun: false,
            IsInterrupted: false,
            SelectedCount: 1);

        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            [new OperationWarning("/repos/warning", "warning detail")],
            plan);

        Assert.Equal("partial", report.Status);
        Assert.True(report.Cleanup!.ReclaimTarget!.TargetMet);
        Assert.Single(report.Warnings);
    }

    [Fact]
    public void Human_cleanup_shows_the_authoritative_reclaim_target_block()
    {
        var plan = CreateMetPlan();
        var candidate = plan.SelectedCandidates[0].Candidate;
        var cleanup = new CleanupResult(
            [new CleanupCandidateResult(
                candidate,
                CleanupOutcome.Deleted,
                "Deleted.",
                DeletionCompleted: true)],
            DryRun: false,
            IsInterrupted: false,
            SelectedCount: 1);
        var report = ReportDocument.FromCleanup(
            ["/repos"],
            cleanup,
            reclaimPlan: plan);
        using var output = new StringWriter();

        HumanReportWriter.WriteCleanup(
            report,
            output,
            new HumanReportOptions(false, false, false, false));

        var text = output.ToString();
        Assert.Contains("Reclaim target", text, StringComparison.Ordinal);
        Assert.Contains("Planned: 6 B estimated", text, StringComparison.Ordinal);
        Assert.Contains("Completed deletion: 6 B estimated", text, StringComparison.Ordinal);
        Assert.Contains("Achieved: 6 B estimated", text, StringComparison.Ordinal);
        Assert.Contains("Target met: yes", text, StringComparison.Ordinal);
        Assert.Contains("Overshoot: 1 B estimated", text, StringComparison.Ordinal);
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
