using RepoGlean.Cli;
using RepoGlean.Scanning;

namespace RepoGlean.Planning;

public static class ReclaimPlanner
{
    public static ReclaimPlan Create(
        IReadOnlyList<ArtifactCandidate> candidates,
        long requestedBytes,
        DateTimeOffset referenceTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (requestedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedBytes));
        }

        var ordered = candidates
            .Select(candidate => CreateFacts(candidate, referenceTimeUtc))
            .OrderBy(item => item.DisruptionTier)
            .ThenBy(item => item.RecencyBand)
            .ThenByDescending(item => item.Candidate.EstimatedBytes)
            .ThenBy(item => NormalizePath(item.Candidate.RepositoryRoot), PathComparer)
            .ThenBy(item => NormalizePath(item.Candidate.RelativePath), PathComparer)
            .ToArray();

        var selected = new List<ReclaimPlanCandidate>();
        var preserved = new List<ReclaimPlanCandidate>();
        long plannedBytes = 0;
        foreach (var item in ordered)
        {
            if (plannedBytes < requestedBytes)
            {
                plannedBytes = FileTreeAnalyzer.SaturatingAdd(
                    plannedBytes,
                    item.Candidate.EstimatedBytes);
                selected.Add(item with { PlanningOrder = selected.Count + 1 });
            }
            else
            {
                preserved.Add(item);
            }
        }

        var targetMet = plannedBytes >= requestedBytes;
        return new ReclaimPlan(
            requestedBytes,
            ordered.Aggregate(0L, (sum, item) => FileTreeAnalyzer.SaturatingAdd(sum, item.Candidate.EstimatedBytes)),
            plannedBytes,
            targetMet ? plannedBytes - requestedBytes : 0,
            targetMet ? 0 : requestedBytes - plannedBytes,
            targetMet,
            Array.AsReadOnly(selected.ToArray()),
            Array.AsReadOnly(preserved.ToArray()));
    }

    private static ReclaimPlanCandidate CreateFacts(
        ArtifactCandidate candidate,
        DateTimeOffset referenceTimeUtc)
    {
        var tier = candidate.Category switch
        {
            ArtifactCategory.Test => 0,
            ArtifactCategory.Build => 1,
            ArtifactCategory.Cache => 2,
            ArtifactCategory.Ide => 3,
            ArtifactCategory.Dependency => 4,
            _ => throw new ArgumentOutOfRangeException(
                nameof(candidate),
                candidate.Category,
                "Unsupported artifact category."),
        };
        var recency = ClassifyRecency(
            candidate.NewestWriteTimeUtc,
            referenceTimeUtc);
        var tierName = candidate.Category.ToString().ToLowerInvariant();
        var recencyName = recency switch
        {
            ReclaimRecencyBand.Dormant => "dormant",
            ReclaimRecencyBand.Stale => "stale",
            ReclaimRecencyBand.RecentOrUnknown => "recent-or-unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(recency)),
        };
        return new ReclaimPlanCandidate(
            Candidate: candidate,
            PlanningOrder: null,
            DisruptionTier: tier,
            RecencyBand: recency,
            PlanningReason:
                $"tier={tierName}; recency={recencyName}; estimatedBytes={candidate.EstimatedBytes}");
    }

    private static ReclaimRecencyBand ClassifyRecency(
        DateTimeOffset? newestWriteTimeUtc,
        DateTimeOffset referenceTimeUtc)
    {
        if (newestWriteTimeUtc is null ||
            newestWriteTimeUtc > referenceTimeUtc)
        {
            return ReclaimRecencyBand.RecentOrUnknown;
        }

        if (newestWriteTimeUtc <= referenceTimeUtc.AddDays(-30))
        {
            return ReclaimRecencyBand.Dormant;
        }

        if (newestWriteTimeUtc <= referenceTimeUtc.AddDays(-7))
        {
            return ReclaimRecencyBand.Stale;
        }

        return ReclaimRecencyBand.RecentOrUnknown;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
