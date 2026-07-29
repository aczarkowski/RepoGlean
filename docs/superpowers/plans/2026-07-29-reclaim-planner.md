# Reclaim Planner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add read-only `plan --free size` recommendations and guarded `clean --free size` execution using one deterministic balanced reclaim policy.

**Architecture:** Extend the existing candidate scan with advisory recency metadata, then pass filtered candidates into a new pure `ReclaimPlanner`. Reporting maps immutable plans into human and source-generated JSON contracts, while `RepoGleanApp` keeps planning read-only or passes the fixed selected list through the existing cleanup revalidation and quarantine pipeline.

**Tech Stack:** .NET 10, C# records, BCL-only production code, `System.Text.Json` source generation, xUnit 2.9.3, real-Git test fixtures, PowerShell Native AOT smoke tests.

## Global Constraints

- Planning considers only artifacts already authorized by active rules and Git-ignore status in Git working trees.
- `plan` is read-only; `clean --free` never bypasses existing revalidation, quarantine, ownership, recovery, or boundary-aware deletion.
- The first release exposes one fixed `balanced` policy and no saved-plan replay, named strategies, package-manager execution, or replacement candidates.
- Ordering is category tier `test`, `build`, `cache`, `ide`, `dependency`; then recency `dormant`, `stale`, `recent-or-unknown`; then estimated size descending; then platform-aware normalized repository and candidate paths.
- `dormant` means at least 30 days old; `stale` means at least 7 but less than 30 days old; future or unavailable timestamps are `recent-or-unknown`.
- Dependency artifacts require `--all` or explicit `--category dependency`.
- All byte values are saturating logical-size estimates, not physical allocation or guaranteed capacity.
- A confirmed plan is fixed: cleanup skips and failures are reported and never trigger substitution.
- Dry-run target achievement uses only successfully safety-validated bytes; live cleanup target achievement uses only candidates whose payload deletion completed.
- Production remains BCL-only, Native AOT-compatible, cross-platform, and report schema version 1.
- Existing commands and ordinary `clean` behavior without `--free` remain unchanged.

---

## File structure

### New production files

- `src/RepoGlean/Scanning/FileTimestampProvider.cs` — injectable, fail-closed last-write-time observation.
- `src/RepoGlean/Planning/ReclaimModels.cs` — immutable domain types for recency, ordered candidates, and aggregate plan values.
- `src/RepoGlean/Planning/ReclaimPlanner.cs` — pure balanced ordering and target accumulation.

### New test files

- `tests/RepoGlean.Tests/Planning/ReclaimPlannerTests.cs` — exhaustive pure-policy tests.
- `tests/RepoGlean.Tests/Application/PlanCommandTests.cs` — end-to-end application contract for the read-only command.
- `tests/RepoGlean.Tests/Output/ReclaimReportTests.cs` — human and JSON plan/target report contracts.

### Existing files modified

- `src/RepoGlean/Scanning/FileTreeAnalyzer.cs` — collect newest observed timestamps during the existing tree walk.
- `src/RepoGlean/Scanning/ScanModels.cs` — carry nullable timestamp metadata on candidates.
- `src/RepoGlean/Scanning/RepositoryScanner.cs` — transfer analysis metadata to authorized candidates.
- `src/RepoGlean/Cli/CliOptions.cs` and `src/RepoGlean/Cli/CliParser.cs` — add `Plan`, `--free`, and the exact option matrix.
- `src/RepoGlean/Output/ReportModels.cs`, `HumanReportWriter.cs`, and `ReportJsonContext.cs` — expose plan and optional cleanup-target documents.
- `src/RepoGlean/Cleaning/CleanupModels.cs` — calculate validated dry-run bytes.
- `src/RepoGlean/Progress/ProgressModels.cs`, `OperationProgressTracker.cs`, and `VerboseProgressRenderer.cs` — identify plan operations without claiming deletion.
- `src/RepoGlean/RepoGleanApp.cs` — orchestrate plan and plan-driven cleanup.
- Existing scanning, CLI, application, output, and progress tests — protect compatibility and integration seams.
- `eng/native-smoke.ps1` and `README.md` — packaged executable acceptance and user contract.

---

### Task 1: Capture advisory candidate recency during scanning

**Files:**
- Create: `src/RepoGlean/Scanning/FileTimestampProvider.cs`
- Modify: `src/RepoGlean/Scanning/FileTreeAnalyzer.cs:3-141`
- Modify: `src/RepoGlean/Scanning/ScanModels.cs:12-23`
- Modify: `src/RepoGlean/Scanning/RepositoryScanner.cs:370-387`
- Test: `tests/RepoGlean.Tests/Scanning/FileTreeAnalyzerTests.cs`
- Test: `tests/RepoGlean.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:**
- Consumes: existing `FileSystemIdentity`, `FileTreeAnalysis`, and `ArtifactCandidate`.
- Produces: `IFileTimestampProvider.TryGetLastWriteTimeUtc(string path, out DateTimeOffset value)`, `FileTreeAnalysis.NewestWriteTimeUtc`, and `ArtifactCandidate.NewestWriteTimeUtc`.

- [ ] **Step 1: Write failing analyzer and scanner tests**

Add an injectable timestamp stub and tests proving the candidate root and every
descendant participate, while any unavailable observation makes the aggregate
timestamp `null`:

```csharp
[Fact]
public void Analyze_records_the_newest_root_or_descendant_write_time()
{
    using var temporary = new TemporaryDirectory();
    var repository = temporary.GetPath("repo");
    var candidate = Path.Combine(repository, "obj");
    Directory.CreateDirectory(candidate);
    File.WriteAllText(Path.Combine(candidate, "old.bin"), "old");
    File.WriteAllText(Path.Combine(candidate, "new.bin"), "new");
    var oldTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var newTime = oldTime.AddDays(1);
    var timestamps = new StubTimestampProvider(new Dictionary<string, DateTimeOffset>
    {
        [Path.GetFullPath(candidate)] = oldTime,
        [Path.GetFullPath(Path.Combine(candidate, "old.bin"))] = oldTime,
        [Path.GetFullPath(Path.Combine(candidate, "new.bin"))] = newTime,
    });
    var analyzer = new FileTreeAnalyzer(new FileSystemIdentityProvider(), timestamps);

    var result = analyzer.Analyze(candidate, repository);

    Assert.True(result.IsSafe);
    Assert.Equal(newTime, result.NewestWriteTimeUtc);
}

[Fact]
public void Analyze_uses_null_when_any_timestamp_is_unavailable()
{
    using var temporary = new TemporaryDirectory();
    var repository = temporary.GetPath("repo");
    var candidate = Path.Combine(repository, "obj");
    Directory.CreateDirectory(candidate);
    var missingTimestamp = Path.Combine(candidate, "artifact.bin");
    File.WriteAllText(missingTimestamp, "payload");
    var timestamps = new StubTimestampProvider(
        new Dictionary<string, DateTimeOffset>
        {
            [Path.GetFullPath(candidate)] = DateTimeOffset.UnixEpoch,
        });
    var analyzer = new FileTreeAnalyzer(new FileSystemIdentityProvider(), timestamps);

    var result = analyzer.Analyze(candidate, repository);

    Assert.True(result.IsSafe);
    Assert.Null(result.NewestWriteTimeUtc);
}
```

Extend `RepositoryScannerTests` with a real ignored artifact whose filesystem
time is fixed, then assert the emitted `ArtifactCandidate.NewestWriteTimeUtc`
matches it.

Add this nested test double to `FileTreeAnalyzerTests`:

```csharp
private sealed class StubTimestampProvider(
    IReadOnlyDictionary<string, DateTimeOffset> values) : IFileTimestampProvider
{
    public bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value) =>
        values.TryGetValue(Path.GetFullPath(path), out value);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~FileTreeAnalyzerTests|FullyQualifiedName~RepositoryScannerTests"
```

Expected: FAIL because the timestamp provider constructor and
`NewestWriteTimeUtc` properties do not exist.

- [ ] **Step 3: Implement fail-closed timestamp collection in the existing walk**

Create the provider:

```csharp
namespace RepoGlean.Scanning;

internal interface IFileTimestampProvider
{
    bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value);
}

internal sealed class FileTimestampProvider : IFileTimestampProvider
{
    public bool TryGetLastWriteTimeUtc(string path, out DateTimeOffset value)
    {
        try
        {
            value = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            value = default;
            return false;
        }
    }
}
```

Add optional trailing positional properties so existing test fixtures continue
to compile:

```csharp
public sealed record FileTreeAnalysis(
    bool IsSafe,
    long FileCount,
    long EstimatedBytes,
    FileSystemIdentity? Identity,
    FileSystemIdentity? RepositoryIdentity,
    IReadOnlyList<OperationWarning> Warnings,
    DateTimeOffset? NewestWriteTimeUtc = null);

public sealed record ArtifactCandidate(
    string RepositoryRoot,
    string AbsolutePath,
    string RelativePath,
    string RuleId,
    ArtifactCategory Category,
    bool Preselected,
    long FileCount,
    long EstimatedBytes,
    FileSystemIdentity Identity,
    FileSystemIdentity RepositoryIdentity,
    DateTimeOffset? NewestWriteTimeUtc = null);
```

Inject `IFileTimestampProvider` into `FileTreeAnalyzer`. Observe the candidate
root before handling file or directory content, observe every enumerated entry
inside the existing loop, and retain `null` once any call fails:

```csharp
private static DateTimeOffset? ObserveTimestamp(
    IFileTimestampProvider provider,
    string path,
    DateTimeOffset? newest,
    ref bool timestampUnavailable)
{
    if (!provider.TryGetLastWriteTimeUtc(path, out var observed))
    {
        timestampUnavailable = true;
        return newest;
    }

    return newest is null || observed > newest.Value ? observed : newest;
}
```

Return `timestampUnavailable ? null : newestWriteTimeUtc` on successful
analysis and pass `analysis.NewestWriteTimeUtc` into `ArtifactCandidate`.
Timestamp failure must not alter `IsSafe`, warnings, size, identity, or mount
checks.

- [ ] **Step 4: Run focused and compatibility tests**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~FileTreeAnalyzerTests|FullyQualifiedName~RepositoryScannerTests|FullyQualifiedName~CleanupServiceTests"
```

Expected: PASS, including existing scanner and cleanup fixtures.

- [ ] **Step 5: Commit**

```bash
git add src/RepoGlean/Scanning tests/RepoGlean.Tests/Scanning
git commit -m "feat: capture artifact recency"
```

---

### Task 2: Implement the pure balanced Reclaim Planner

**Files:**
- Create: `src/RepoGlean/Planning/ReclaimModels.cs`
- Create: `src/RepoGlean/Planning/ReclaimPlanner.cs`
- Create: `tests/RepoGlean.Tests/Planning/ReclaimPlannerTests.cs`

**Interfaces:**
- Consumes: `ArtifactCandidate.Category`, `EstimatedBytes`, `RepositoryRoot`, `RelativePath`, and `NewestWriteTimeUtc`.
- Produces: `ReclaimPlanner.Create(IReadOnlyList<ArtifactCandidate> candidates, long requestedBytes, DateTimeOffset referenceTimeUtc)`.
- Produces: `ReclaimPlan`, `ReclaimPlanCandidate`, and `ReclaimRecencyBand`.

- [ ] **Step 1: Write failing policy tests**

Create a test helper that varies category, bytes, time, and paths. Cover the
complete lexicographic policy with explicit assertions:

```csharp
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
```

Also add tests for category order, `null` time, exact target, empty pool,
whole-pool shortfall, `long.MaxValue` saturation, immutable returned
collections, and ordering explanations containing the actual tier, band, and
byte count.

Use this complete helper inside `ReclaimPlannerTests`:

```csharp
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
```

- [ ] **Step 2: Run planner tests and verify RED**

Run:

```bash
dotnet test RepoGlean.slnx --filter FullyQualifiedName~ReclaimPlannerTests
```

Expected: FAIL because namespace `RepoGlean.Planning` does not exist.

- [ ] **Step 3: Implement immutable models and deterministic greedy ordering**

Define:

```csharp
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
```

Implement `ReclaimPlanner.Create` with:

```csharp
public static ReclaimPlan Create(
    IReadOnlyList<ArtifactCandidate> candidates,
    long requestedBytes,
    DateTimeOffset referenceTimeUtc)
{
    ArgumentNullException.ThrowIfNull(candidates);
    if (requestedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(requestedBytes));

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
```

Map tiers exactly to `Test=0`, `Build=1`, `Cache=2`, `Ide=3`,
`Dependency=4`. Classify `null` and future timestamps as
`RecentOrUnknown`; use `<= referenceTimeUtc.AddDays(-30)` and
`<= referenceTimeUtc.AddDays(-7)` for the two older bands. Normalize both
directory separators before the platform-aware path comparison.

Use these helpers so every ordering key and explanation has one definition:

```csharp
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
        return ReclaimRecencyBand.Dormant;
    if (newestWriteTimeUtc <= referenceTimeUtc.AddDays(-7))
        return ReclaimRecencyBand.Stale;
    return ReclaimRecencyBand.RecentOrUnknown;
}

private static string NormalizePath(string path) =>
    path.Replace('\\', '/');

private static StringComparer PathComparer =>
    OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
```

- [ ] **Step 4: Run planner tests**

Run:

```bash
dotnet test RepoGlean.slnx --filter FullyQualifiedName~ReclaimPlannerTests
```

Expected: PASS with exact target, ordering, shortfall, and saturation cases.

- [ ] **Step 5: Commit**

```bash
git add src/RepoGlean/Planning tests/RepoGlean.Tests/Planning
git commit -m "feat: add balanced reclaim planner"
```

---

### Task 3: Add the `plan` command and `--free` parser contract

**Files:**
- Modify: `src/RepoGlean/Cli/CliOptions.cs:3-112`
- Modify: `src/RepoGlean/Cli/CliParser.cs:7-267`
- Test: `tests/RepoGlean.Tests/Cli/CliParserTests.cs`
- Test: `tests/RepoGlean.Tests/Cli/ByteSizeParserTests.cs`

**Interfaces:**
- Consumes: existing positive `ByteSizeParser.TryParse`.
- Produces: `CommandKind.Plan` and nullable `CliOptions.FreeBytes`.

- [ ] **Step 1: Write failing parser matrix tests**

Add focused theories:

```csharp
[Theory]
[InlineData("20GiB", 21474836480L)]
[InlineData("5GB", 5000000000L)]
public void Plan_requires_and_parses_a_positive_free_target(string value, long expected)
{
    var result = CliParser.Parse(["plan", ".", "--free", value]);

    Assert.True(result.IsSuccess);
    Assert.Equal(CommandKind.Plan, result.Value!.Command);
    Assert.Equal(expected, result.Value.FreeBytes);
}

[Theory]
[InlineData(new[] { "plan", "." })]
[InlineData(new[] { "plan", ".", "--free", "0" })]
[InlineData(new[] { "scan", ".", "--free", "1GiB" })]
[InlineData(new[] { "plan", ".", "--free", "1GiB", "--details" })]
[InlineData(new[] { "plan", ".", "--free", "1GiB", "--dry-run" })]
[InlineData(new[] { "plan", ".", "--free", "1GiB", "--yes" })]
public void Reclaim_option_matrix_rejects_invalid_invocations(string[] arguments)
{
    Assert.False(CliParser.Parse(arguments).IsSuccess);
}

[Fact]
public void Clean_yes_accepts_free_as_explicit_scope()
{
    var result = CliParser.Parse(["clean", ".", "--yes", "--free", "1GiB"]);

    Assert.True(result.IsSuccess);
    Assert.Equal(1073741824L, result.Value!.FreeBytes);
}
```

Add positive cases for every allowed `plan` option and invalid `--free` use
with rules, config, help, and version. Retain the existing byte-size parser
tests to prove decimal/binary units, overflow, zero, and negative inputs.

- [ ] **Step 2: Run CLI tests and verify RED**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~CliParserTests|FullyQualifiedName~ByteSizeParserTests"
```

Expected: FAIL because `Plan`, `--free`, and `FreeBytes` do not exist.

- [ ] **Step 3: Implement command parsing and validation**

Add `Plan` after `Scan`, add a nullable constructor/property value:

```csharp
public long? FreeBytes { get; }
```

Parse the option with the same error style as `--min-size`:

```csharp
case "--free":
    if (!TryReadValue(arguments, ref index, argument, out var freeSize, out error))
        return ParseResult<CliOptions>.Failure(error);
    if (!ByteSizeParser.TryParse(freeSize, out var parsedFreeBytes))
        return ParseResult<CliOptions>.Failure($"Invalid byte size '{freeSize}'.");
    freeBytes = parsedFreeBytes;
    break;
```

Allow positional roots for `Plan`, require `FreeBytes` on `Plan`, allow
`--free` only on `Plan` and `Clean`, allow `--all` on `Plan`, and change
unattended scope validation to:

```csharp
if (command == CommandKind.Clean &&
    yes &&
    !all &&
    repositories.Count == 0 &&
    categories.Count == 0 &&
    freeBytes is null)
{
    return ParseResult<CliOptions>.Failure(
        "clean --yes requires --all, --repo, --category, or --free.");
}
```

Keep `--details`, `--dry-run`, and `--yes` invalid on `Plan`.

- [ ] **Step 4: Run CLI tests**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~CliParserTests|FullyQualifiedName~ByteSizeParserTests"
```

Expected: PASS with the existing command-specific option tests unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/RepoGlean/Cli tests/RepoGlean.Tests/Cli
git commit -m "feat: parse reclaim targets"
```

---

### Task 4: Add human and JSON reclaim report contracts

**Files:**
- Modify: `src/RepoGlean/Output/ReportModels.cs:14-221`
- Modify: `src/RepoGlean/Output/HumanReportWriter.cs:10-153`
- Modify: `src/RepoGlean/Output/ReportJsonContext.cs:5-16`
- Create: `tests/RepoGlean.Tests/Output/ReclaimReportTests.cs`
- Test: `tests/RepoGlean.Tests/Output/ReportWriterTests.cs`
- Test: `tests/RepoGlean.Tests/Output/HumanCleanupReportTests.cs`

**Interfaces:**
- Consumes: `ReclaimPlan` and `ReclaimPlanCandidate`.
- Produces: `PlanningCandidateReport`, `ReclaimPlanReport`,
  `ReclaimTargetReport`, `ReportDocument.Plan`,
  `ReportDocument.FromPlan(IReadOnlyList<string>, ReclaimPlan, IReadOnlyList<OperationWarning>)`,
  and `HumanReportWriter.WritePlan(ReportDocument, TextWriter, HumanReportOptions)`.
- Extends: `CleanupSummaryReport.ReclaimTarget` as nullable.

- [ ] **Step 1: Write failing report contract tests**

Create a plan with one selected and one preserved candidate, then assert the
human summary and exact JSON fields:

```csharp
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
    Assert.False(
        planJson.GetProperty("preservedCandidates")[0].TryGetProperty(
            "planningOrder",
            out _));
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
```

Add a separate whole-pool shortfall test asserting `status: "partial"`,
`targetMet: false`, positive `shortfallBytes`, and an empty preserved array.
Add a quiet human-output test proving selected candidate rows and the target
summary remain visible while roots, warning details, and preserved-candidate
detail are suppressed.
Add source-generation tests that manually construct `CleanupSummaryReport`
with dry-run and live `ReclaimTargetReport` values. Assert ordinary cleanup
omits `reclaimTarget`.

Use this complete met-target helper:

```csharp
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
```

- [ ] **Step 2: Run output tests and verify RED**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~ReclaimReportTests|FullyQualifiedName~ReportWriterTests|FullyQualifiedName~HumanCleanupReportTests"
```

Expected: FAIL because reclaim report records and writers do not exist.

- [ ] **Step 3: Implement report records, mapping, and rendering**

Add source-generated report records:

```csharp
public sealed record PlanningCandidateReport(
    string RepositoryRoot,
    string AbsolutePath,
    string RelativePath,
    string RuleId,
    string Category,
    bool Preselected,
    long FileCount,
    long EstimatedBytes,
    int? PlanningOrder,
    int DisruptionTier,
    string RecencyBand,
    DateTimeOffset? NewestWriteTimeUtc,
    string PlanningReason);

public sealed record ReclaimPlanReport(
    long RequestedBytes,
    long EligibleBytes,
    long PlannedBytes,
    long OvershootBytes,
    long ShortfallBytes,
    bool TargetMet,
    long SelectedCandidateCount,
    long PreservedCandidateCount,
    IReadOnlyList<PlanningCandidateReport> SelectedCandidates,
    IReadOnlyList<PlanningCandidateReport> PreservedCandidates);

public sealed record ReclaimTargetReport(
    long RequestedBytes,
    long PlannedBytes,
    long ValidatedBytes,
    long CompletedDeletionBytes,
    long AchievedBytes,
    long OvershootBytes,
    long ShortfallBytes,
    bool TargetMet);
```

Extend `CleanupSummaryReport` with optional
`ReclaimTargetReport? ReclaimTarget = null` and `ReportDocument` with optional
`ReclaimPlanReport? Plan = null`.

Implement:

```csharp
public static ReportDocument FromPlan(
    IReadOnlyList<string> effectiveRoots,
    ReclaimPlan plan,
    IReadOnlyList<OperationWarning> warnings)
```

Use `status: "partial"` when warnings exist or `plan.TargetMet` is false.
Set common totals from the complete eligible pool. Build common repository
reports by grouping the selected and preserved candidate union without
changing plan order inside the operation-specific arrays. Map selected and
preserved arrays without re-sorting them. Format enum values as lower tokens:
`dormant`, `stale`, and `recent-or-unknown`.

Add every new record to `ReportJsonContext`. Implement `WritePlan` so selected
candidate rows, target, planned total, target-met result, and overshoot or
shortfall always print because they are the command's primary result. Quiet
mode suppresses roots, warning details, and preserved-candidate detail; normal
mode also prints those sections. Do not print physical-space wording.

- [ ] **Step 4: Run output tests**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~ReclaimReportTests|FullyQualifiedName~ReportWriterTests|FullyQualifiedName~HumanCleanupReportTests"
```

Expected: PASS; existing scan and ordinary cleanup JSON shapes remain valid.

- [ ] **Step 5: Commit**

```bash
git add src/RepoGlean/Output tests/RepoGlean.Tests/Output
git commit -m "feat: report reclaim plans"
```

---

### Task 5: Orchestrate the read-only `plan` command and its progress

**Files:**
- Modify: `src/RepoGlean/Progress/ProgressModels.cs:8-42`
- Modify: `src/RepoGlean/Progress/OperationProgressTracker.cs:1-73`
- Modify: `src/RepoGlean/Progress/VerboseProgressRenderer.cs:52-103`
- Modify: `src/RepoGlean/RepoGleanApp.cs:13-38`
- Modify: `src/RepoGlean/RepoGleanApp.cs:94-175`
- Modify: `src/RepoGlean/RepoGleanApp.cs:443-531`
- Modify: `src/RepoGlean/RepoGleanApp.cs:567-601`
- Create: `tests/RepoGlean.Tests/Application/PlanCommandTests.cs`
- Test: `tests/RepoGlean.Tests/Progress/OperationProgressTrackerTests.cs`
- Test: `tests/RepoGlean.Tests/Progress/ProgressSnapshotTests.cs`
- Test: `tests/RepoGlean.Tests/Progress/VerboseProgressRendererTests.cs`

**Interfaces:**
- Consumes: `CliOptions.FreeBytes`, `ReclaimPlanner.Create`,
  `ReportDocument.FromPlan`, and `HumanReportWriter.WritePlan`.
- Produces: `RepoGleanApp.RunPlanAsync(CliOptions, RepoGleanConfig, AppRuntime, TextWriter, TextWriter, CancellationToken)`
  and `ProgressOperation.Plan`.

- [ ] **Step 1: Write failing application and progress tests**

Build real Git repositories with authorized build/test/cache artifacts and an
opt-in dependency. Set deterministic last-write times before invocation.
Cover target met, shortfall exit `3`, filters, dependency opt-in, JSON
cleanliness, configured roots, no candidates, cancellation, and help text:

```csharp
[Fact]
public async Task Plan_json_selects_only_the_balanced_prefix_and_is_read_only()
{
    using var temporary = new TemporaryDirectory();
    var repository = await CreatePlanningRepositoryAsync(temporary.GetPath("repo"));

    var result = await RunAsync(
        ["plan", repository.Path, "--free", "4B", "--format", "json", "--no-progress"]);

    Assert.Equal(0, result.ExitCode);
    Assert.Equal(string.Empty, result.Stderr);
    using var document = JsonDocument.Parse(result.Stdout);
    var root = document.RootElement;
    Assert.Equal("plan", root.GetProperty("operation").GetString());
    Assert.True(root.GetProperty("plan").GetProperty("targetMet").GetBoolean());
    Assert.Equal(
        "TestResults",
        root.GetProperty("plan").GetProperty("selectedCandidates")[0].GetProperty("relativePath").GetString());
    Assert.True(Directory.Exists(repository.GetPath("TestResults")));
    Assert.True(Directory.Exists(repository.GetPath("obj")));
}

[Fact]
public async Task Plan_shortfall_is_a_valid_partial_result()
{
    using var temporary = new TemporaryDirectory();
    var repository = await CreatePlanningRepositoryAsync(temporary.GetPath("repo"));

    var result = await RunAsync(
        ["plan", repository.Path, "--free", "1TiB", "--format", "json"]);

    Assert.Equal(3, result.ExitCode);
    using var document = JsonDocument.Parse(result.Stdout);
    Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
    Assert.True(document.RootElement.GetProperty("plan").GetProperty("shortfallBytes").GetInt64() > 0);
}
```

Add progress tests requiring verbose completion text such as
`Plan complete: 2 candidates selected, 12 B estimated planned, 0 warnings.`
and requiring interactive scan phases to keep “Discovering” and “Scanning”
rather than ever saying “reclaimed.”

Use these complete helpers in `PlanCommandTests` so application recency is
deterministic:

```csharp
private static readonly DateTimeOffset ReferenceTime =
    new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

private static async Task<GitTestRepository> CreatePlanningRepositoryAsync(
    string path)
{
    var repository = await GitTestRepository.CreateAsync(path);
    repository.Write("project.csproj", "<Project />");
    repository.Write("build.gradle", "plugins {}");
    repository.Write("package.json", "{}");
    repository.Write(
        ".gitignore",
        "TestResults/\nobj/\n.gradle/\nnode_modules/\n");
    repository.WriteBytes("TestResults/result.bin", 4);
    repository.WriteBytes("obj/artifact.bin", 6);
    repository.WriteBytes(".gradle/cache.bin", 8);
    repository.WriteBytes("node_modules/package.bin", 10);
    await repository.CommitAllAsync();
    Directory.SetLastWriteTimeUtc(
        repository.GetPath("TestResults"),
        ReferenceTime.AddDays(-40).UtcDateTime);
    File.SetLastWriteTimeUtc(
        repository.GetPath("TestResults/result.bin"),
        ReferenceTime.AddDays(-40).UtcDateTime);
    return repository;
}

private static async Task<AppResult> RunAsync(
    string[] arguments,
    string inputText = "",
    bool isErrorInteractive = false,
    CancellationToken cancellationToken = default)
{
    using var input = new StringReader(inputText);
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();
    var runtime = new AppRuntime(
        "git",
        Path.GetTempPath(),
        isErrorInteractive,
        UtcNowProvider: () => ReferenceTime);
    var exitCode = await RepoGleanApp.RunAsync(
        arguments,
        input,
        stdout,
        stderr,
        runtime,
        cancellationToken);
    return new AppResult(exitCode, stdout.ToString(), stderr.ToString());
}

private sealed record AppResult(int ExitCode, string Stdout, string Stderr);
```

- [ ] **Step 2: Run application and progress tests and verify RED**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~PlanCommandTests|FullyQualifiedName~OperationProgressTrackerTests|FullyQualifiedName~ProgressSnapshotTests|FullyQualifiedName~VerboseProgressRendererTests"
```

Expected: FAIL because the app switch and progress model do not support
`Plan`.

- [ ] **Step 3: Implement read-only orchestration and factual progress**

Add `ProgressOperation.Plan`. Let `OperationProgressTracker` track both scan
and plan discovery/scanning events, and replace the fixed interruption method
with:

```csharp
public OperationProgressEvent CreateReadOnlyInterruptedEvent(
    ProgressOperation operation)
```

Format plan completion, interruption, and failure separately in
`VerboseProgressRenderer`. Keep existing interactive discovery/scanning
snapshot text because no bytes have been reclaimed.

Add an optional clock to the end of `AppRuntime`:

```csharp
Func<DateTimeOffset>? UtcNowProvider = null
```

Add `CommandKind.Plan` to the application switch and implement
`RunPlanAsync` by following the existing `RunScanAsync` lifecycle:

```csharp
var referenceTimeUtc =
    runtime.UtcNowProvider?.Invoke() ??
    DateTimeOffset.UtcNow;
var roots = ResolveRoots(options.Roots, config.Roots, runtime.HomeDirectory);
var exclusions = config.Excludes.Concat(options.Exclusions).ToArray();
var git = new GitClient(runtime.GitExecutable);
await git.GetVersionAsync(cancellationToken).ConfigureAwait(false);
var discoveryService = runtime.DriveRootProvider is null
    ? new RepositoryDiscovery(git, progress, ProgressOperation.Plan)
    : new RepositoryDiscovery(
        git,
        runtime.DriveRootProvider,
        progress,
        ProgressOperation.Plan);
var discovery = await discoveryService
    .DiscoverAsync(roots, exclusions, options.AllDrives, cancellationToken)
    .ConfigureAwait(false);
var scan = await new RepositoryScanner(git, progress, ProgressOperation.Plan)
    .ScanAsync(
        discovery.Repositories,
        RuleCatalog.Create(config),
        new ScanOptions(options.Repositories, options.Categories, exclusions, options.MinimumBytes),
        cancellationToken)
    .ConfigureAwait(false);
var includeDependencies =
    options.All ||
    options.Categories.Contains(ArtifactCategory.Dependency);
var pool = FilterCandidates(scan.Repositories, includeDependencies);
var plan = ReclaimPlanner.Create(pool, options.FreeBytes!.Value, referenceTimeUtc);
var warnings = discovery.Warnings.Concat(scan.Warnings).ToArray();
var report = ReportDocument.FromPlan(discovery.EffectiveRoots ?? roots, plan, warnings);
```

Emit a terminal plan event only after the plan exists, pause progress before
writing, select JSON or human output, and return `3` when the report is partial.
The outer JSON cancellation/failure path must use operation name `"plan"`.
Update help with the new usage and `--free`.

- [ ] **Step 4: Run focused tests**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~PlanCommandTests|FullyQualifiedName~ReadOnlyCommandTests|FullyQualifiedName~OperationProgressTrackerTests|FullyQualifiedName~ProgressSnapshotTests|FullyQualifiedName~VerboseProgressRendererTests"
```

Expected: PASS; ordinary scan progress and JSON remain unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/RepoGlean/Progress src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests/Application tests/RepoGlean.Tests/Progress
git commit -m "feat: add read-only reclaim planning"
```

---

### Task 6: Execute a fixed plan through guarded cleanup

**Files:**
- Modify: `src/RepoGlean/Cleaning/CleanupModels.cs:20-35`
- Modify: `src/RepoGlean/Output/ReportModels.cs:49-184`
- Modify: `src/RepoGlean/Output/HumanReportWriter.cs:89-116`
- Modify: `src/RepoGlean/RepoGleanApp.cs:177-326`
- Test: `tests/RepoGlean.Tests/Application/CleanCommandTests.cs`
- Test: `tests/RepoGlean.Tests/Cleaning/CleanupServiceTests.cs`
- Test: `tests/RepoGlean.Tests/Output/ReclaimReportTests.cs`

**Interfaces:**
- Consumes: `ReclaimPlan.SelectedCandidates`, existing `CleanupService`, and
  existing `CleanupCandidateResult.DeletionCompleted`.
- Produces: `CleanupResult.EstimatedValidatedBytes` and
  `ReportDocument.FromCleanup(IReadOnlyList<string>, CleanupResult, IReadOnlyList<OperationWarning>?, ReclaimPlan?)`.

- [ ] **Step 1: Write failing clean, dry-run, and non-substitution tests**

Add tests proving interactive plan display replaces both selection prompts,
exact confirmation remains required, `--yes --free` is unattended, dependency
opt-in works, and ordinary cleanup still uses its original prompts.

Exercise target accounting:

```csharp
[Fact]
public async Task Reclaim_dry_run_counts_validated_bytes_without_claiming_deletion()
{
    using var temporary = new TemporaryDirectory();
    var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

    var result = await RunAsync(
        ["clean", repository.Path, "--free", "5B", "--dry-run", "--format", "json"],
        string.Empty);

    Assert.Equal(0, result.ExitCode);
    using var document = JsonDocument.Parse(result.Stdout);
    var target = document.RootElement.GetProperty("cleanup").GetProperty("reclaimTarget");
    Assert.Equal(5, target.GetProperty("validatedBytes").GetInt64());
    Assert.Equal(0, target.GetProperty("completedDeletionBytes").GetInt64());
    Assert.Equal(5, target.GetProperty("achievedBytes").GetInt64());
    Assert.True(target.GetProperty("targetMet").GetBoolean());
    Assert.True(Directory.Exists(repository.GetPath("obj")));
}

[Fact]
public async Task Reclaim_cleanup_does_not_substitute_after_a_selected_candidate_changes()
{
    using var temporary = new TemporaryDirectory();
    var repository = await CreateRepositoryAsync(temporary.GetPath("repo"));

    var result = await RunAsync(
        ["clean", repository.Path, "--free", "5B", "--verbose"],
        "delete\n",
        beforeReadLine: lineNumber =>
        {
            if (lineNumber != 1) return;
            Directory.Delete(repository.GetPath("obj"), recursive: true);
            Directory.CreateDirectory(repository.GetPath("obj"));
            File.WriteAllText(repository.GetPath("obj/replacement.bin"), "replacement");
        });

    Assert.Equal(3, result.ExitCode);
    Assert.True(Directory.Exists(repository.GetPath("node_modules")));
    Assert.Contains("Shortfall", result.Stdout, StringComparison.Ordinal);
}
```

Add cases for live `DeletionCompleted=true`, post-deletion quarantine failure
counting toward achievement, safety skip, failure, interruption, planned
shortfall before cleanup, and a target reached despite an unrelated warning
still returning partial.

- [ ] **Step 2: Run clean and output tests and verify RED**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~CleanCommandTests|FullyQualifiedName~CleanupServiceTests|FullyQualifiedName~ReclaimReportTests"
```

Expected: FAIL because cleanup does not yet carry a plan or target accounting.

- [ ] **Step 3: Implement fixed selection and achieved-byte accounting**

Add:

```csharp
public long EstimatedValidatedBytes => Items
    .Where(item =>
        DryRun &&
        item.Outcome == CleanupOutcome.Skipped &&
        item.Message.StartsWith("Validated; dry run", StringComparison.Ordinal))
    .Aggregate(
        0L,
        (total, item) => FileTreeAnalyzer.SaturatingAdd(
            total,
            item.Candidate.EstimatedBytes));
```

When `options.FreeBytes` is present, create the plan after scanning and before
any prompt. Use:

```csharp
selectedCandidates = Array.AsReadOnly(
    reclaimPlan.SelectedCandidates
        .Select(item => item.Candidate)
        .ToArray());
```

For interactive table output, pause progress, write the complete plan, and
then issue the existing exact confirmation. Do not call
`WriteRepositorySelection`, `WriteCandidateSelection`, or
`ReadSelectionAsync` in this branch. JSON and dry-run branches write no
pre-clean document.

Extend `FromCleanup` with an optional plan. Compute:

```csharp
var validatedBytes = result.EstimatedValidatedBytes;
var completedDeletionBytes = result.EstimatedDeletedBytes;
var achievedBytes = result.DryRun ? validatedBytes : completedDeletionBytes;
var targetMet = reclaimPlan is null || achievedBytes >= reclaimPlan.RequestedBytes;
```

Use saturating-safe difference helpers for overshoot and shortfall. Mark the
report partial when a reclaim plan itself had a shortfall or achieved bytes
finish below target. Show the reclaim target block in human cleanup output.
Return based on the authoritative report status after preserving interruption
as exit `130`.

Never call the planner again after confirmation and never append a preserved
candidate after a cleanup outcome.

- [ ] **Step 4: Run clean, safety, and regression tests**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~CleanCommandTests|FullyQualifiedName~CleanupServiceTests|FullyQualifiedName~BoundaryAwareDeleterTests|FullyQualifiedName~ReclaimReportTests|FullyQualifiedName~EndToEndTests"
```

Expected: PASS, including ordinary interactive selection and adversarial
cleanup behavior.

- [ ] **Step 5: Commit**

```bash
git add src/RepoGlean/Cleaning/CleanupModels.cs src/RepoGlean/Output src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests
git commit -m "feat: execute guarded reclaim plans"
```

---

### Task 7: Document and Native AOT-verify the complete feature

**Files:**
- Modify: `README.md:55-145`
- Modify: `README.md:195-231`
- Modify: `eng/native-smoke.ps1:87-117`
- Test: `tests/RepoGlean.Tests/Acceptance/EndToEndTests.cs`

**Interfaces:**
- Consumes: final CLI, human/JSON, exit-code, and cleanup contracts.
- Produces: public documentation and packaged-executable acceptance evidence.

- [ ] **Step 1: Extend acceptance tests and Native AOT smoke assertions**

Add an acceptance test that invokes help, JSON plan, reclaim dry run, and live
reclaim cleanup against a real Git repository. Assert the dependency, tracked
file, unrelated untracked file, and `.git` survive.

In `eng/native-smoke.ps1`, run these before the existing filesystem assertions:

```powershell
$plan = Invoke-JsonCommand -Arguments @(
    "plan", $repository, "--free", "1B",
    "--config", $configPath, "--format", "json", "--no-progress"
)
if ($plan.operation -ne "plan" -or
    $plan.status -ne "success" -or
    -not $plan.plan.targetMet -or
    $plan.plan.selectedCandidateCount -ne 1 -or
    $plan.plan.selectedCandidates[0].relativePath -ne "obj") {
    throw "Native reclaim plan returned an unexpected JSON result."
}

$dryRun = Invoke-JsonCommand -Arguments @(
    "clean", $repository, "--free", "1B", "--dry-run",
    "--config", $configPath, "--format", "json", "--no-progress"
)
if (-not $dryRun.cleanup.reclaimTarget.targetMet -or
    $dryRun.cleanup.reclaimTarget.validatedBytes -lt 1 -or
    $dryRun.cleanup.reclaimTarget.completedDeletionBytes -ne 0) {
    throw "Native reclaim dry run returned incorrect target accounting."
}
if (-not (Test-Path -LiteralPath (Join-Path $repository "obj"))) {
    throw "Native reclaim dry run deleted the build artifact."
}

$clean = Invoke-JsonCommand -Arguments @(
    "clean", $repository, "--free", "1B", "--yes",
    "--config", $configPath, "--format", "json", "--no-progress"
)
if ($clean.operation -ne "clean" -or
    $clean.status -ne "success" -or
    -not $clean.cleanup.reclaimTarget.targetMet -or
    $clean.cleanup.reclaimTarget.completedDeletionBytes -lt 1) {
    throw "Native reclaim cleanup returned incorrect target accounting."
}
```

Replace the old category-build cleanup invocation so the smoke deletes `obj`
only once.

- [ ] **Step 2: Run acceptance tests and verify RED before smoke edits are complete**

Run:

```bash
dotnet test RepoGlean.slnx --filter FullyQualifiedName~EndToEndTests
```

Expected: FAIL until the new help/output assertions and complete reclaim flow
match.

- [ ] **Step 3: Update help-facing documentation and smoke flow**

Update the README command synopsis and option matrix with `plan` and `--free`.
Add examples for a met target, dependency opt-in, dry-run, unattended cleanup,
and JSON automation. Document:

- the fixed tier and recency order;
- 30-day and 7-day bands;
- future/unknown timestamps ranking as recent;
- greedy estimated-byte selection;
- best-effort shortfall and exit `3`;
- dry-run validated versus live completed-deletion achievement;
- no substitution and no saved-plan replay;
- logical-size estimates rather than physical capacity.

Keep the existing safety section authoritative and explicitly state that the
planner cannot authorize deletion.

- [ ] **Step 4: Run full repository acceptance**

Run in order:

```bash
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx --configuration Release --no-restore --warnaserror
dotnet test RepoGlean.slnx --configuration Release --no-build
dotnet format RepoGlean.slnx --verify-no-changes --no-restore
git diff --check
```

Publish and smoke the host RID using the same packaged executable shape as CI.
On Apple Silicon:

```bash
dotnet publish src/RepoGlean/RepoGlean.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained \
  -p:PublishAot=true \
  -o artifacts/native/osx-arm64
pwsh eng/native-smoke.ps1 \
  -ExecutablePath artifacts/native/osx-arm64/repoglean
```

For another host, replace `osx-arm64` with the current supported RID and use
the emitted `repoglean` or `repoglean.exe`. Expected: every command exits `0`
and the smoke ends with `Native packaged-executable smoke PASS`.

- [ ] **Step 5: Commit**

```bash
git add README.md eng/native-smoke.ps1 tests/RepoGlean.Tests/Acceptance
git commit -m "docs: explain reclaim planning"
```

---

## Final review gate

After Task 7, inspect the complete branch rather than relying only on per-task
tests:

```bash
git status --short
git log --oneline --decorate master..HEAD
git diff --stat master...HEAD
git diff --check master...HEAD
```

Review every requirement in
`docs/superpowers/specs/2026-07-29-reclaim-planner-design.md` against code and
fresh test evidence. Pay particular attention to:

- no planner path around Git authority or cleanup revalidation;
- no dependency entry without explicit opt-in;
- no timestamp use as safety authority;
- no post-confirmation substitution;
- no dry-run claim of deleted or reclaimed bytes;
- correct `partial` status and exit `3` for shortfalls;
- exactly one JSON document on stdout;
- unchanged ordinary scan and cleanup behavior;
- no new production dependencies;
- successful Native AOT packaged-executable smoke.
