using RepoGlean.Cli;
using RepoGlean.Planning;
using RepoGlean.Scanning;

namespace RepoGlean.Tests.Planning;

public sealed class ReclaimPlannerTests
{
    [Fact]
    public void Create_orders_by_tier_recency_size_and_stable_path_then_stops_at_target()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            Candidate("/repos/z", "obj", ArtifactCategory.Build, 90, reference.AddDays(-40)),
            Candidate("/repos/a", "TestResults", ArtifactCategory.Test, 20, reference.AddDays(-8)),
            Candidate("/repos/b", "TestResults", ArtifactCategory.Test, 30, reference.AddDays(-8)),
            Candidate("/repos/a", ".cache", ArtifactCategory.Cache, 500, reference.AddDays(-100)),
        };

        var plan = ReclaimPlanner.Create(
            candidates,
            requestedBytes: 45,
            referenceTimeUtc: reference);

        Assert.True(plan.TargetMet);
        Assert.Equal(50, plan.PlannedBytes);
        Assert.Equal(5, plan.OvershootBytes);
        Assert.Equal(0, plan.ShortfallBytes);
        Assert.Equal(
            ["/repos/b/TestResults", "/repos/a/TestResults"],
            plan.SelectedCandidates.Select(item => item.Candidate.AbsolutePath));
        Assert.Equal([1, 2], plan.SelectedCandidates.Select(item => item.PlanningOrder));
        Assert.Equal(2, plan.PreservedCandidates.Count);
    }

    [Theory]
    [InlineData(-30, ReclaimRecencyBand.Dormant)]
    [InlineData(-29, ReclaimRecencyBand.Stale)]
    [InlineData(-7, ReclaimRecencyBand.Stale)]
    [InlineData(-6, ReclaimRecencyBand.RecentOrUnknown)]
    [InlineData(1, ReclaimRecencyBand.RecentOrUnknown)]
    public void Create_classifies_fixed_recency_boundaries(
        int daysFromReference,
        ReclaimRecencyBand expected)
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var plan = ReclaimPlanner.Create(
            [Candidate("/repos/a", "obj", ArtifactCategory.Build, 1, reference.AddDays(daysFromReference))],
            1,
            reference);

        Assert.Equal(expected, plan.SelectedCandidates[0].RecencyBand);
    }

    [Fact]
    public void Create_orders_categories_by_fixed_disruption_tier()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var plan = ReclaimPlanner.Create(
            [
                Candidate("/repos/a", "dependency", ArtifactCategory.Dependency, 1, reference),
                Candidate("/repos/a", "ide", ArtifactCategory.Ide, 1, reference),
                Candidate("/repos/a", "cache", ArtifactCategory.Cache, 1, reference),
                Candidate("/repos/a", "build", ArtifactCategory.Build, 1, reference),
                Candidate("/repos/a", "test", ArtifactCategory.Test, 1, reference),
            ],
            5,
            reference);

        Assert.Equal(
            [ArtifactCategory.Test, ArtifactCategory.Build, ArtifactCategory.Cache, ArtifactCategory.Ide, ArtifactCategory.Dependency],
            plan.SelectedCandidates.Select(item => item.Candidate.Category));
        Assert.Equal([0, 1, 2, 3, 4], plan.SelectedCandidates.Select(item => item.DisruptionTier));
    }

    [Fact]
    public void Create_uses_normalized_repository_and_relative_paths_as_stable_tie_breakers()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var plan = ReclaimPlanner.Create(
            [
                Candidate("/repos/b", "alpha", ArtifactCategory.Build, 1, reference),
                Candidate("/repos/a", "alpha/z", ArtifactCategory.Build, 1, reference),
                Candidate("/repos/a", "alpha\\item", ArtifactCategory.Build, 1, reference),
            ],
            3,
            reference);

        Assert.Equal(
            ["alpha\\item", "alpha/z", "alpha"],
            plan.SelectedCandidates.Select(item => item.Candidate.RelativePath));
    }

    [Fact]
    public void Create_treats_null_timestamps_as_recent_or_unknown()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        var plan = ReclaimPlanner.Create(
            [Candidate("/repos/a", "obj", ArtifactCategory.Build, 1, null)],
            1,
            reference);

        Assert.Equal(ReclaimRecencyBand.RecentOrUnknown, plan.SelectedCandidates[0].RecencyBand);
    }

    [Fact]
    public void Create_reports_an_exact_target_without_overshoot()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        var plan = ReclaimPlanner.Create(
            [Candidate("/repos/a", "obj", ArtifactCategory.Build, 45, reference)],
            45,
            reference);

        Assert.True(plan.TargetMet);
        Assert.Equal(45, plan.PlannedBytes);
        Assert.Equal(0, plan.OvershootBytes);
        Assert.Equal(0, plan.ShortfallBytes);
    }

    [Fact]
    public void Create_reports_an_empty_pool_as_a_shortfall()
    {
        var plan = ReclaimPlanner.Create(
            [],
            45,
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.False(plan.TargetMet);
        Assert.Equal(0, plan.EligibleBytes);
        Assert.Equal(0, plan.PlannedBytes);
        Assert.Equal(0, plan.OvershootBytes);
        Assert.Equal(45, plan.ShortfallBytes);
        Assert.Empty(plan.SelectedCandidates);
        Assert.Empty(plan.PreservedCandidates);
    }

    [Fact]
    public void Create_reports_a_shortfall_when_the_whole_pool_is_insufficient()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        var plan = ReclaimPlanner.Create(
            [
                Candidate("/repos/a", "obj", ArtifactCategory.Build, 20, reference),
                Candidate("/repos/a", ".cache", ArtifactCategory.Cache, 10, reference),
            ],
            45,
            reference);

        Assert.False(plan.TargetMet);
        Assert.Equal(30, plan.EligibleBytes);
        Assert.Equal(30, plan.PlannedBytes);
        Assert.Equal(15, plan.ShortfallBytes);
        Assert.Empty(plan.PreservedCandidates);
    }

    [Fact]
    public void Create_saturates_totals_at_long_max_value()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        var plan = ReclaimPlanner.Create(
            [
                Candidate("/repos/a", "obj", ArtifactCategory.Build, long.MaxValue, reference),
                Candidate("/repos/a", ".cache", ArtifactCategory.Cache, 1, reference),
            ],
            long.MaxValue,
            reference);

        Assert.True(plan.TargetMet);
        Assert.Equal(long.MaxValue, plan.EligibleBytes);
        Assert.Equal(long.MaxValue, plan.PlannedBytes);
        Assert.Equal(0, plan.OvershootBytes);
        Assert.Single(plan.PreservedCandidates);
    }

    [Fact]
    public void Create_returns_immutable_selected_and_preserved_collections()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var plan = ReclaimPlanner.Create(
            [
                Candidate("/repos/a", "obj", ArtifactCategory.Build, 10, reference),
                Candidate("/repos/a", ".cache", ArtifactCategory.Cache, 10, reference),
            ],
            10,
            reference);

        Assert.Throws<NotSupportedException>(() => ((IList<ReclaimPlanCandidate>)plan.SelectedCandidates).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ReclaimPlanCandidate>)plan.PreservedCandidates).Clear());
    }

    [Fact]
    public void Create_explains_each_candidate_with_its_actual_tier_recency_and_bytes()
    {
        var reference = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        var plan = ReclaimPlanner.Create(
            [Candidate("/repos/a", ".cache", ArtifactCategory.Cache, 123, reference.AddDays(-40))],
            123,
            reference);

        Assert.Equal(
            "tier=cache; recency=dormant; estimatedBytes=123",
            plan.SelectedCandidates[0].PlanningReason);
    }

    private static ArtifactCandidate Candidate(
        string repositoryRoot,
        string relativePath,
        ArtifactCategory category,
        long estimatedBytes,
        DateTimeOffset? newestWriteTimeUtc)
    {
        var identity = new FileSystemIdentity(
            1,
            2,
            "mount",
            FileAttributes.Directory,
            LinkTarget: null);
        return new ArtifactCandidate(
            repositoryRoot,
            Path.Combine(repositoryRoot, relativePath),
            relativePath,
            $"test.{category.ToString().ToLowerInvariant()}",
            category,
            Preselected: category != ArtifactCategory.Dependency,
            FileCount: 1,
            estimatedBytes,
            identity,
            identity,
            newestWriteTimeUtc);
    }
}
