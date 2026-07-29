using RepoGlean.Scanning;

namespace RepoGlean.Planning;

public enum ReclaimRecencyBand
{
    Dormant,
    Stale,
    RecentOrUnknown,
}

public sealed record ReclaimPlanCandidate(
    ArtifactCandidate Candidate,
    int? PlanningOrder,
    int DisruptionTier,
    ReclaimRecencyBand RecencyBand,
    string PlanningReason);

public sealed record ReclaimPlan(
    long RequestedBytes,
    long EligibleBytes,
    long PlannedBytes,
    long OvershootBytes,
    long ShortfallBytes,
    bool TargetMet,
    IReadOnlyList<ReclaimPlanCandidate> SelectedCandidates,
    IReadOnlyList<ReclaimPlanCandidate> PreservedCandidates);
