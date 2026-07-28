# Interactive Progress Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add restrained live progress to `scan` and `clean`, with one compact terminal status line by default and stable milestone diagnostics under `--verbose`.

**Architecture:** Discovery, scanning, and cleanup publish immutable structured events through an internal observational interface. An application-owned factory selects a no-op, verbose, or timer-backed interactive renderer; renderers alone know about stderr, terminal width, refresh timing, and text formatting. Existing public service APIs, final reports, JSON, exit codes, prompts, and cleanup authority remain unchanged.

**Tech Stack:** .NET 10, C# records, `PeriodicTimer`, BCL-only console I/O, xUnit, real temporary Git repositories, Native AOT.

## Global Constraints

- Start from design commit `8668fa0` and follow `docs/superpowers/specs/2026-07-28-interactive-progress-design.md`.
- Keep the production project BCL-only; add no NuGet package or terminal UI dependency.
- Keep all final human reports and machine-readable JSON on stdout.
- Write progress and verbose diagnostics only to stderr.
- Preserve JSON schema version 1, report fields, exit codes, cleanup prompts, selection rules, and every cleanup authority check.
- `--details` continues to expand only the final scan report.
- `--quiet` suppresses progress and verbose narration; `--verbose` otherwise selects append-only milestones even with redirected stderr, JSON, or `--no-progress`.
- Normal JSON and non-interactive stderr remain progress-free.
- Normal table output uses compact animation only when stderr is interactive and `--no-progress` is absent.
- Compact rendering refreshes no more than eight times per second and never reports a discovery percentage.
- Do not emit directory-level or file-level traversal events, internal Git commands, quarantine details, timestamps, or stack traces.
- Progress is observational: a reporter or renderer failure must not alter scan, clean, cancellation, or mutation behavior.
- Progress counters update only after authoritative results exist; dry runs say `validated`, and deleted counts/bytes advance only when `DeletionCompleted` is true.
- Keep public `RepositoryDiscovery`, `RepositoryScanner`, and `CleanupService` constructor and method signatures source-compatible.

---

## File Structure

- Create `src/RepoGlean/Progress/ProgressModels.cs` for event, mode, operation, and outcome records/enums.
- Create `src/RepoGlean/Progress/IOperationProgress.cs` for the observational interface and no-op implementation.
- Create `src/RepoGlean/Progress/ProgressModeSelector.cs` for flag/terminal precedence.
- Create `src/RepoGlean/Progress/ProgressText.cs` for shared byte, root, path, and width-safe text formatting.
- Create `src/RepoGlean/Progress/VerboseProgressRenderer.cs` for append-only milestones.
- Create `src/RepoGlean/Progress/ProgressSnapshot.cs` for folding events into compact cumulative state.
- Create `src/RepoGlean/Progress/InteractiveProgressRenderer.cs` for the bounded refresh loop and single-line lifecycle.
- Create `src/RepoGlean/Progress/ProgressReporterFactory.cs` for renderer construction.
- Create `tests/RepoGlean.Tests/Support/RecordingProgress.cs` for service event assertions.
- Create focused tests under `tests/RepoGlean.Tests/Progress/`.
- Modify discovery, scanner, and cleanup services only to publish events through injected internal dependencies.
- Modify `src/RepoGlean/RepoGleanApp.cs` to own renderer selection/lifecycle and pause compact output around prompts/reports.
- Modify application and acceptance tests for the public stderr/stdout contract.
- Modify `README.md` and CLI help copy to document the approved flag semantics.

---

### Task 1: Progress Contract and Mode Selection

**Files:**
- Create: `src/RepoGlean/Progress/ProgressModels.cs`
- Create: `src/RepoGlean/Progress/IOperationProgress.cs`
- Create: `src/RepoGlean/Progress/ProgressModeSelector.cs`
- Create: `tests/RepoGlean.Tests/Support/RecordingProgress.cs`
- Create: `tests/RepoGlean.Tests/Progress/ProgressModeSelectorTests.cs`

**Interfaces:**
- Consumes: existing `RepoGlean.Cli.OutputFormat`.
- Produces: `ProgressMode`, `ProgressOperation`, `ProgressEventKind`,
  `ProgressCandidateOutcome`, `OperationProgressEvent`, `IOperationProgress`,
  `NullOperationProgress.Instance`,
  `ProgressModeSelector.Select(bool, OutputFormat, bool, bool, bool)`, and the
  test-only `RecordingProgress`.

- [ ] **Step 1: Write the failing renderer-selection tests**

Create `ProgressModeSelectorTests.cs` with the complete precedence matrix:

```csharp
using RepoGlean.Cli;
using RepoGlean.Progress;

namespace RepoGlean.Tests.Progress;

public sealed class ProgressModeSelectorTests
{
    [Theory]
    [InlineData(true, OutputFormat.Table, false, false, false, ProgressMode.Interactive)]
    [InlineData(true, OutputFormat.Table, false, false, true, ProgressMode.None)]
    [InlineData(false, OutputFormat.Table, false, false, false, ProgressMode.None)]
    [InlineData(true, OutputFormat.Json, false, false, false, ProgressMode.None)]
    [InlineData(false, OutputFormat.Json, false, true, false, ProgressMode.Verbose)]
    [InlineData(false, OutputFormat.Table, false, true, false, ProgressMode.Verbose)]
    [InlineData(false, OutputFormat.Table, false, true, true, ProgressMode.Verbose)]
    [InlineData(true, OutputFormat.Table, true, true, false, ProgressMode.None)]
    public void Select_applies_quiet_verbose_json_interactivity_and_no_progress_precedence(
        bool isErrorInteractive,
        OutputFormat format,
        bool quiet,
        bool verbose,
        bool noProgress,
        ProgressMode expected)
    {
        var actual = ProgressModeSelector.Select(
            isErrorInteractive,
            format,
            quiet,
            verbose,
            noProgress);

        Assert.Equal(expected, actual);
    }
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter FullyQualifiedName~ProgressModeSelectorTests
```

Expected: FAIL because `RepoGlean.Progress` and `ProgressModeSelector` do not exist.

- [ ] **Step 3: Add the immutable progress contract**

Create `ProgressModels.cs` with these exact types:

```csharp
namespace RepoGlean.Progress;

internal enum ProgressMode
{
    None,
    Interactive,
    Verbose,
}

internal enum ProgressOperation
{
    Scan,
    Clean,
}

internal enum ProgressEventKind
{
    DiscoveryStarted,
    RepositoryFound,
    DiscoveryCompleted,
    RepositoryScanStarted,
    RepositoryScanCompleted,
    CandidateStarted,
    CandidateCompleted,
    Warning,
    Completed,
    Interrupted,
    Failed,
}

internal enum ProgressCandidateOutcome
{
    Deleted,
    Validated,
    Skipped,
    Failed,
}

internal sealed record OperationProgressEvent(
    ProgressEventKind Kind,
    ProgressOperation Operation,
    IReadOnlyList<string>? Roots = null,
    string? Path = null,
    string? Message = null,
    int Current = 0,
    int Total = 0,
    long RepositoryCount = 0,
    long CurrentCandidateCount = 0,
    long CandidateCount = 0,
    long CurrentEstimatedBytes = 0,
    long EstimatedBytes = 0,
    long DeletedCount = 0,
    long ValidatedCount = 0,
    long SkippedCount = 0,
    long FailedCount = 0,
    long WarningCount = 0,
    bool DryRun = false,
    ProgressCandidateOutcome? Outcome = null);
```

Create `IOperationProgress.cs`:

```csharp
namespace RepoGlean.Progress;

internal interface IOperationProgress : IAsyncDisposable
{
    void Report(OperationProgressEvent progressEvent);

    void Pause();

    void Resume();
}

internal sealed class NullOperationProgress : IOperationProgress
{
    public static NullOperationProgress Instance { get; } = new();

    private NullOperationProgress()
    {
    }

    public void Report(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
    }

    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

Create `ProgressModeSelector.cs`:

```csharp
using RepoGlean.Cli;

namespace RepoGlean.Progress;

internal static class ProgressModeSelector
{
    public static ProgressMode Select(
        bool isErrorInteractive,
        OutputFormat format,
        bool quiet,
        bool verbose,
        bool noProgress)
    {
        if (quiet) return ProgressMode.None;
        if (verbose) return ProgressMode.Verbose;
        if (format == OutputFormat.Json || noProgress || !isErrorInteractive) return ProgressMode.None;
        return ProgressMode.Interactive;
    }
}
```

Create `RecordingProgress.cs` as a reusable test observer:

```csharp
using RepoGlean.Progress;

namespace RepoGlean.Tests.Support;

internal sealed class RecordingProgress : IOperationProgress
{
    private readonly List<OperationProgressEvent> events = [];

    public IReadOnlyList<OperationProgressEvent> Events => events;

    public int PauseCount { get; private set; }

    public int ResumeCount { get; private set; }

    public void Report(OperationProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        events.Add(progressEvent);
    }

    public void Pause() => PauseCount++;

    public void Resume() => ResumeCount++;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Task 1 test command again.

Expected: PASS, with all eight mode-selection cases succeeding.

- [ ] **Step 5: Commit the contract**

```bash
git add src/RepoGlean/Progress tests/RepoGlean.Tests/Progress tests/RepoGlean.Tests/Support/RecordingProgress.cs
git commit -m "feat: define progress reporting contract"
```

---

### Task 2: Append-Only Verbose Renderer

**Files:**
- Create: `src/RepoGlean/Progress/ProgressText.cs`
- Create: `src/RepoGlean/Progress/VerboseProgressRenderer.cs`
- Create: `tests/RepoGlean.Tests/Progress/VerboseProgressRendererTests.cs`

**Interfaces:**
- Consumes: `OperationProgressEvent`, `IOperationProgress`, and `HumanReportWriter.FormatBytes(long)`.
- Produces: `ProgressText.FormatBytes(long)`,
  `ProgressText.FormatRoots(IReadOnlyList<string>?)`,
  `ProgressText.DisplayPath(string?)`, and `VerboseProgressRenderer`.

- [ ] **Step 1: Write failing verbose-format and write-failure tests**

Create tests that report this fixed event sequence:

```csharp
var events = new[]
{
    new OperationProgressEvent(
        ProgressEventKind.DiscoveryStarted,
        ProgressOperation.Scan,
        Roots: ["/work"]),
    new OperationProgressEvent(
        ProgressEventKind.DiscoveryCompleted,
        ProgressOperation.Scan,
        RepositoryCount: 18),
    new OperationProgressEvent(
        ProgressEventKind.RepositoryScanStarted,
        ProgressOperation.Scan,
        Path: "/work/my-api",
        Current: 7,
        Total: 18),
    new OperationProgressEvent(
        ProgressEventKind.RepositoryScanCompleted,
        ProgressOperation.Scan,
        Path: "/work/my-api",
        Current: 7,
        Total: 18,
        CurrentCandidateCount: 3,
        CurrentEstimatedBytes: 448790528,
        CandidateCount: 23,
        EstimatedBytes: 1503238553),
    new OperationProgressEvent(
        ProgressEventKind.Warning,
        ProgressOperation.Scan,
        Path: "/work/unreadable",
        Message: "Unable to inspect path.",
        WarningCount: 1),
    new OperationProgressEvent(
        ProgressEventKind.Completed,
        ProgressOperation.Scan,
        RepositoryCount: 18,
        CandidateCount: 23,
        EstimatedBytes: 1503238553,
        WarningCount: 1),
};
```

Assert the writer contains, in order:

```text
Discovering repositories under /work...
Found 18 repositories.
Scanning [7/18] /work/my-api...
Found 3 candidates in my-api (428 MiB estimated).
Warning: /work/unreadable: Unable to inspect path.
Scan complete: 18 repositories, 23 candidates, 1 warning.
```

Add a cleanup sequence asserting `Validating [2/6]`, `Deleted`, `Validated`,
`Skipped`, `Failed`, and an interrupted aggregate. Assert verbose output
contains neither `\r` nor `\u001b`.

Add a `ThrowingTextWriter : StringWriter` whose `WriteLine(string?)` throws
`IOException`; reporting two events must not throw either time.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter FullyQualifiedName~VerboseProgressRendererTests
```

Expected: FAIL because `VerboseProgressRenderer` does not exist.

- [ ] **Step 3: Implement shared text and verbose event formatting**

In `ProgressText.cs`, delegate byte wording to the existing report formatter
and make path formatting deterministic:

```csharp
using RepoGlean.Output;

namespace RepoGlean.Progress;

internal static class ProgressText
{
    public static string FormatBytes(long bytes) => HumanReportWriter.FormatBytes(bytes);

    public static string FormatRoots(IReadOnlyList<string>? roots) =>
        roots is null || roots.Count == 0 ? "(default root)" : string.Join(", ", roots);

    public static string DisplayPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    public static string Plural(long count, string singular, string plural) =>
        count == 1 ? singular : plural;
}
```

Implement `VerboseProgressRenderer` as a sealed `IOperationProgress`. Its
`Report` method maps every `ProgressEventKind` to either one stable line or no
line, writes under a private lock, and catches only `IOException` and
`ObjectDisposedException`. Once a write fails, set a private `disabled` flag.
`Pause` and `Resume` are no-ops and `DisposeAsync` returns a completed value
task.

Use this outcome mapping for `CandidateCompleted`:

```csharp
var verb = progressEvent.Outcome switch
{
    ProgressCandidateOutcome.Deleted => "Deleted",
    ProgressCandidateOutcome.Validated => "Validated",
    ProgressCandidateOutcome.Skipped => "Skipped",
    ProgressCandidateOutcome.Failed => "Failed",
    _ => "Processed",
};
```

Only `RepositoryScanCompleted` events with `CurrentCandidateCount > 0` produce
a repository-result line. Use `ProgressText.DisplayPath` for the repository
label and `CurrentEstimatedBytes` for that repository's size. `Completed`,
`Interrupted`, and `Failed` wording must use `Operation` to say `Scan` or
`Cleanup`.

- [ ] **Step 4: Run focused and output tests**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~VerboseProgressRendererTests|FullyQualifiedName~ReportWriterTests|FullyQualifiedName~HumanCleanupReportTests"
```

Expected: PASS. Existing final report formatting remains unchanged.

- [ ] **Step 5: Commit verbose rendering**

```bash
git add src/RepoGlean/Progress tests/RepoGlean.Tests/Progress/VerboseProgressRendererTests.cs
git commit -m "feat: render verbose progress milestones"
```

---

### Task 3: Compact Interactive Renderer and Factory

**Files:**
- Create: `src/RepoGlean/Progress/ProgressSnapshot.cs`
- Create: `src/RepoGlean/Progress/InteractiveProgressRenderer.cs`
- Create: `src/RepoGlean/Progress/ProgressReporterFactory.cs`
- Create: `tests/RepoGlean.Tests/Progress/ProgressSnapshotTests.cs`
- Create: `tests/RepoGlean.Tests/Progress/InteractiveProgressRendererTests.cs`
- Create: `tests/RepoGlean.Tests/Progress/ProgressReporterFactoryTests.cs`

**Interfaces:**
- Consumes: Task 1 modes/events and Task 2 text helpers/renderers.
- Produces: `ProgressSnapshot.Apply(OperationProgressEvent)`,
  `ProgressSnapshot.Format()`, `IProgressTicker`, `PeriodicProgressTicker`,
  `InteractiveProgressRenderer`, and the exact
  `ProgressReporterFactory.Create` signature defined in Step 3.

- [ ] **Step 1: Write failing snapshot, renderer, and factory tests**

In `ProgressSnapshotTests`, apply discovery events and assert:

```csharp
Assert.Equal(
    "Discovering repositories • 12 found",
    snapshot.Format());
```

Apply repository start/completion events and assert:

```csharp
Assert.Equal(
    "Scanning repositories 7/18 • 23 candidates • 1.4 GiB estimated",
    snapshot.Format());
```

Apply cleanup candidate events and assert permanent cleanup uses `Cleaning`,
dry-run uses `Validating`, deleted bytes advance only with deleted outcomes,
and warning events increment only the warning count without replacing the
current stage.

In `InteractiveProgressRendererTests`, use a manual ticker:

```csharp
internal sealed class ManualProgressTicker : IProgressTicker
{
    private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>();

    public void Tick() => ticks.Writer.TryWrite(true);

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        await ticks.Reader.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        ticks.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
```

Add `using System.Threading.Channels;` to the test file containing this manual
ticker.

Assert:

- the first event renders synchronously with a leading `\r`;
- later events update state without writing before the next tick;
- one manual tick advances the spinner once and renders the latest state;
- `Pause()` pads over the previous line and returns the cursor to column zero;
- events while paused update state without writing;
- `Resume()` renders the newest state;
- a width of 40 truncates the optional path while retaining stage and counters;
- a null or throwing width provider omits the optional path;
- a throwing writer disables later writes without throwing;
- `DisposeAsync()` cancels the loop, clears once, and produces no later output.

In `ProgressReporterFactoryTests`, assert the returned runtime types for every
`ProgressModeSelectorTests` row.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~ProgressSnapshotTests|FullyQualifiedName~InteractiveProgressRendererTests|FullyQualifiedName~ProgressReporterFactoryTests"
```

Expected: FAIL because the snapshot, ticker, renderer, and factory do not exist.

- [ ] **Step 3: Implement cumulative state and bounded rendering**

Define `ProgressSnapshot` as an immutable record containing phase, optional
path, current/total, repository/candidate/outcome/warning counters, estimated
bytes, and dry-run state. `Apply(OperationProgressEvent)` must preserve the
current phase for warning events and increment its warning count once per
warning event. This prevents discovery-local and scanner-local warning lists
from resetting the operation-wide count. Other cumulative fields change only
from authoritative stage/result events; final completion may replace all
totals with the authoritative report totals.

Use these exact phase strings in `Format()`:

```csharp
return Phase switch
{
    ProgressEventKind.DiscoveryStarted or ProgressEventKind.RepositoryFound =>
        $"Discovering repositories • {RepositoryCount} found",
    ProgressEventKind.RepositoryScanStarted or ProgressEventKind.RepositoryScanCompleted =>
        $"Scanning repositories {Current}/{Total} • {CandidateCount} candidates • {ProgressText.FormatBytes(EstimatedBytes)}",
    ProgressEventKind.CandidateStarted or ProgressEventKind.CandidateCompleted when DryRun =>
        $"Validating artifacts {Current}/{Total} • {ValidatedCount} validated • {ProgressText.FormatBytes(EstimatedBytes)}",
    ProgressEventKind.CandidateStarted or ProgressEventKind.CandidateCompleted =>
        $"Cleaning artifacts {Current}/{Total} • {DeletedCount} deleted • {ProgressText.FormatBytes(EstimatedBytes)}",
    _ => string.Empty,
};
```

Append ` • N warnings` only when `WarningCount > 0`. Treat the repository or
candidate path as optional suffix content that may be removed before the fixed
counters.

Define the production ticker:

```csharp
internal interface IProgressTicker : IAsyncDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}

internal sealed class PeriodicProgressTicker : IProgressTicker
{
    private readonly PeriodicTimer timer = new(TimeSpan.FromMilliseconds(125));

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        timer.WaitForNextTickAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

`InteractiveProgressRenderer` starts one background refresh task, keeps its
snapshot and writer operations under one private lock, and cycles through:

```csharp
private static readonly string[] SpinnerFrames =
[
    "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏",
];
```

Write each frame as `\r` plus content padded to the previous rendered width.
Clear using `\r`, spaces equal to the prior width, then `\r`; do not use ANSI
escape sequences. `Report` applies state and renders only the initial frame
synchronously; later events wait for the next 125 ms timer tick, which renders
the newest state and advances the spinner. `Pause` suppresses timer writes and
clears; `Resume` writes the latest non-empty state once before returning to the
bounded ticker cadence. Catch `IOException`, `ObjectDisposedException`, and
terminal-width exceptions inside rendering and disable only progress.
`DisposeAsync` cancels the refresh token, treats the resulting
`OperationCanceledException` as normal shutdown, awaits the loop, clears the
line, and disposes the ticker exactly once.

Create `ProgressReporterFactory.Create` with this exact signature:

```csharp
internal static IOperationProgress Create(
    bool isErrorInteractive,
    OutputFormat format,
    bool quiet,
    bool verbose,
    bool noProgress,
    TextWriter stderr,
    Func<int?> terminalWidthProvider,
    IProgressTicker? ticker = null)
```

Select with `ProgressModeSelector`. Return the singleton no-op, a verbose
renderer, or an interactive renderer using `ticker ?? new
PeriodicProgressTicker()`.

- [ ] **Step 4: Run focused progress tests and verify GREEN**

Run the Task 3 test command again.

Expected: PASS, including deterministic manual-ticker lifecycle and factory
precedence.

- [ ] **Step 5: Commit compact progress**

```bash
git add src/RepoGlean/Progress tests/RepoGlean.Tests/Progress
git commit -m "feat: render compact interactive progress"
```

---

### Task 4: Repository Discovery and Scan Events

**Files:**
- Modify: `src/RepoGlean/Scanning/RepositoryDiscovery.cs`
- Modify: `src/RepoGlean/Scanning/RepositoryScanner.cs`
- Modify: `tests/RepoGlean.Tests/Scanning/RepositoryDiscoveryTests.cs`
- Modify: `tests/RepoGlean.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:**
- Consumes: `IOperationProgress`, `NullOperationProgress.Instance`,
  `ProgressOperation`, and `OperationProgressEvent`.
- Produces: internal progress-aware constructors while preserving every
  existing public constructor/method signature.

- [ ] **Step 1: Write failing discovery and scan event tests**

Add a discovery test that creates two real Git repositories, injects
`RecordingProgress`, and asserts this semantic sequence:

```csharp
Assert.Equal(ProgressEventKind.DiscoveryStarted, events.First().Kind);
Assert.Equal(2, events.Count(item => item.Kind == ProgressEventKind.RepositoryFound));
Assert.Equal(
    [1L, 2L],
    events
        .Where(item => item.Kind == ProgressEventKind.RepositoryFound)
        .Select(item => item.RepositoryCount));
Assert.Equal(ProgressEventKind.DiscoveryCompleted, events.Last().Kind);
Assert.Equal(2, events.Last().RepositoryCount);
Assert.DoesNotContain(events, item =>
    item.Path is not null &&
    item.Path.Contains("ordinary-directory", StringComparison.Ordinal));
```

Add a missing-root discovery assertion that the warning event has the same
path/message as the warning retained in `RepositoryDiscoveryResult`.

Add a scanner test over one repository with candidates and one without. Assert
one start and completion event per selected repository, real `Current/Total`
values `1/2` then `2/2`, cumulative candidate/byte counts that never decrease,
and repository-local `CurrentCandidateCount`/`CurrentEstimatedBytes`. Add a
warning fixture and assert the event exactly matches the warning retained in
`ScanResult`.

- [ ] **Step 2: Run scanning tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~RepositoryDiscoveryTests|FullyQualifiedName~RepositoryScannerTests"
```

Expected: FAIL because the progress-aware internal constructors do not exist.

- [ ] **Step 3: Inject observational progress without changing public APIs**

Keep public constructors forwarding to `NullOperationProgress.Instance`.
Add internal constructor paths carrying `IOperationProgress` and
`ProgressOperation`.

For discovery, publish `DiscoveryStarted` after roots/all-drives are resolved,
`RepositoryFound` only when `HashSet.Add` returns true, each warning at the same
point it enters the warning list, and `DiscoveryCompleted` immediately before
returning. Do not publish events for ordinary directories.

Centralize warning insertion so result and event cannot diverge:

```csharp
private void AddWarning(
    List<OperationWarning> warnings,
    OperationWarning warning)
{
    warnings.Add(warning);
    progress.Report(new OperationProgressEvent(
        ProgressEventKind.Warning,
        operation,
        Path: warning.Path,
        Message: warning.Message));
}
```

For scanning, materialize the distinct, repository-filtered roots before the
loop so `Total` is truthful. Publish `RepositoryScanStarted` before Git
inspection and `RepositoryScanCompleted` after that repository's authoritative
result or warning outcome is known. Maintain cumulative candidate count,
estimated bytes, and warning count separately from the final size-sorted
result list; publish those cumulative values without changing final sorting.

Do not publish candidate-level filesystem traversal events.

- [ ] **Step 4: Run focused scanning tests and existing scan application tests**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~RepositoryDiscoveryTests|FullyQualifiedName~RepositoryScannerTests|FullyQualifiedName~ReadOnlyCommandTests"
```

Expected: PASS. Existing app behavior is unchanged because it still constructs
services through no-op public paths at this task boundary.

- [ ] **Step 5: Commit scan instrumentation**

```bash
git add src/RepoGlean/Scanning tests/RepoGlean.Tests/Scanning
git commit -m "feat: publish repository scan progress"
```

---

### Task 5: Cleanup Candidate Events

**Files:**
- Modify: `src/RepoGlean/Cleaning/CleanupService.cs`
- Modify: `tests/RepoGlean.Tests/Cleaning/CleanupServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 progress contract and existing `CleanupCandidateResult`.
- Produces: an optional final internal constructor argument
  `IOperationProgress? progress = null`; existing public and internal call
  sites remain valid.

- [ ] **Step 1: Write failing cleanup progress tests**

Extend the existing real-Git cleanup fixture to inject `RecordingProgress`.
Cover:

- permanent success emits `CandidateStarted` then `CandidateCompleted` with
  `Deleted`, `DeletedCount = 1`, and deleted estimated bytes;
- dry-run success emits `Validated`, `ValidatedCount = 1`, no deleted count,
  and validated estimated bytes;
- a safety validation rejection emits `Skipped` and advances only
  `SkippedCount`;
- an injected cleanup failure emits `Failed` and advances only `FailedCount`;
- cancellation after one completed candidate emits no start event for later
  unscheduled candidates;
- all `Current` values are one-based and `Total` always equals the original
  selected count.

Use assertions against the existing `CleanupResult` to prove each progress
outcome and count agrees with the authoritative result.

- [ ] **Step 2: Run focused cleanup tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter FullyQualifiedName~CleanupServiceTests
```

Expected: FAIL because `CleanupService` cannot accept the progress reporter.

- [ ] **Step 3: Publish candidate-boundary events from one result helper**

Store `progress ?? NullOperationProgress.Instance`. Before validating each
scheduled candidate, report:

```csharp
progress.Report(new OperationProgressEvent(
    ProgressEventKind.CandidateStarted,
    ProgressOperation.Clean,
    Path: candidate.AbsolutePath,
    Current: results.Count + 1,
    Total: request.Candidates.Count,
    DeletedCount: deletedCount,
    ValidatedCount: validatedCount,
    SkippedCount: skippedCount,
    FailedCount: failedCount,
    EstimatedBytes: processedEstimatedBytes,
    DryRun: request.DryRun));
```

Replace every direct insertion into `results` with one local `RecordResult`
helper. The helper adds the exact `CleanupCandidateResult`, derives the
progress outcome, updates cumulative counters, and publishes
`CandidateCompleted`.

Derive the outcome by authority, not message parsing:

- the explicit dry-run branch records `Validated`;
- `DeletionCompleted` records `Deleted` even when the overall cleanup item is
  failed by later quarantine cleanup;
- `CleanupOutcome.Skipped` records `Skipped`;
- all other failed outcomes record `Failed`.

Add `candidate.EstimatedBytes` to the progress byte total only for `Validated`
or `DeletionCompleted`. Preserve the current cancellation catches and
selected/processed accounting exactly.

- [ ] **Step 4: Run cleanup and safety suites**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~CleanupServiceTests|FullyQualifiedName~BoundaryAwareDeleterTests|FullyQualifiedName~CleanCommandTests"
```

Expected: PASS with all existing destructive safety regressions unchanged.

- [ ] **Step 5: Commit cleanup instrumentation**

```bash
git add src/RepoGlean/Cleaning/CleanupService.cs tests/RepoGlean.Tests/Cleaning/CleanupServiceTests.cs
git commit -m "feat: publish cleanup progress outcomes"
```

---

### Task 6: Application Lifecycle, Prompts, and Public Output Matrix

**Files:**
- Modify: `src/RepoGlean/RepoGleanApp.cs`
- Modify: `tests/RepoGlean.Tests/Application/ReadOnlyCommandTests.cs`
- Modify: `tests/RepoGlean.Tests/Application/CleanCommandTests.cs`

**Interfaces:**
- Consumes: `ProgressReporterFactory`, progress-aware service constructors,
  and existing `AppRuntime`.
- Produces: the complete public selection matrix and clean renderer lifecycle.

- [ ] **Step 1: Write failing end-to-end application contract tests**

Replace the old two-line progress test with a matrix that asserts:

```csharp
var interactive = await RunAsync(
    ["scan", repository.Path],
    isErrorInteractive: true);
var redirected = await RunAsync(
    ["scan", repository.Path],
    isErrorInteractive: false);
var verboseRedirected = await RunAsync(
    ["scan", repository.Path, "--verbose"],
    isErrorInteractive: false);
var verboseJson = await RunAsync(
    ["scan", repository.Path, "--verbose", "--format", "json"],
    isErrorInteractive: false);
var verboseNoProgress = await RunAsync(
    ["scan", repository.Path, "--verbose", "--no-progress"],
    isErrorInteractive: false);
var quietVerbose = await RunAsync(
    ["scan", repository.Path, "--quiet", "--verbose"],
    isErrorInteractive: true);
```

Assert:

- interactive stderr contains a compact progress stage and `\r`, while stdout
  contains only the final report; exact discovery/scanning stage transitions
  remain deterministic renderer-unit assertions rather than timing-sensitive
  application assertions;
- redirected stderr is empty;
- verbose redirected stderr contains discovery, repository, and completion
  lines but neither `\r` nor `\u001b`;
- verbose JSON stdout parses as one document and stderr contains milestones;
- verbose plus `--no-progress` still emits milestones;
- quiet plus verbose emits no progress diagnostics and retains the quiet final
  summary.

For interactive clean, add a writer that records each stdout/stderr write in
order. Assert the temporary status is cleared before `Repositories:`,
`Artifacts:`, and `Type delete`, resumes only after successful confirmation,
and is not resumed after confirmation cancellation. Add dry-run assertions for
`Validating artifacts`/`Validated` and the absence of the word `Deleted` in
progress diagnostics.

Add verbose cleanup assertions for per-candidate outcomes, including safety
skip/failure, and for factual interrupted totals.

- [ ] **Step 2: Run application tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~ReadOnlyCommandTests|FullyQualifiedName~CleanCommandTests"
```

Expected: FAIL because `RepoGleanApp` still emits only `Scanning...` and
`complete` messages and does not inject the progress reporter.

- [ ] **Step 3: Make `RepoGleanApp` own one renderer per operation**

Add a safe terminal-width provider to `AppRuntime` as its final optional
constructor member so existing tests remain source-compatible:

```csharp
Func<int?>? ErrorWidthProvider = null
```

Production `AppRuntime.Create` supplies a function that returns
`Console.WindowWidth` and catches `IOException`, `PlatformNotSupportedException`,
and `ArgumentOutOfRangeException`, returning null.

At the start of `RunScanAsync` and `RunCleanAsync`, create:

```csharp
await using var progress = ProgressReporterFactory.Create(
    runtime.IsErrorInteractive,
    options.OutputFormat,
    options.Quiet,
    options.Verbose,
    options.NoProgress,
    stderr,
    runtime.ErrorWidthProvider ?? (() => null));
```

Pass the same reporter and the correct `ProgressOperation` to discovery and
scanner internal constructors. Pass it to the cleanup service. Remove the old
`showProgress`, `Scanning ...`, `Scan complete.`, and `Cleanup complete.`
branches.

Before every interactive clean selection or confirmation write, call
`progress.Pause()`. Call `progress.Resume()` only after successful confirmation
and immediately before cleanup begins. Compact mode then resumes with the
first candidate event; verbose/no-op pause and resume remain harmless.

Before writing a final report, publish `Completed` with authoritative totals,
then pause. When `CleanupResult.IsInterrupted` is true, publish `Interrupted`
instead, using `SelectedCount` and `Items.Count`, then pause and preserve the
existing cleanup report and exit code 130. When scan throws
`OperationCanceledException`, publish `Interrupted`, pause, and rethrow so the
existing outer handler preserves exit code 130. On an existing fatal
operational exception, publish `Failed` with the exception message, pause, and
rethrow so existing JSON/human error behavior and exit code remain unchanged.

Do not publish completion when the user declines cleanup confirmation; pause
and keep the existing cancellation message/exit code 0.

- [ ] **Step 4: Run application, output, and CLI tests**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~Application|FullyQualifiedName~Output|FullyQualifiedName~Cli"
```

Expected: PASS. JSON stdout parses, verbose stderr is append-only, interactive
stderr is temporary, and final report semantics are unchanged.

- [ ] **Step 5: Commit application wiring**

```bash
git add src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests/Application
git commit -m "feat: integrate scan and clean progress"
```

---

### Task 7: Documentation, Executable Acceptance, and Full Verification

**Files:**
- Modify: `src/RepoGlean/RepoGleanApp.cs`
- Modify: `README.md`
- Modify: `tests/RepoGlean.Tests/Acceptance/EndToEndTests.cs`

**Interfaces:**
- Consumes: completed interactive and verbose behavior.
- Produces: public help/README contract and packaged executable evidence.

- [ ] **Step 1: Write failing help, README, and executable tests**

Extend `Release_surface_documents_help_schema_and_all_native_targets` to assert
help and README distinguish:

```csharp
Assert.Contains("--details", executableHelp.Stdout, StringComparison.Ordinal);
Assert.Contains("--verbose", executableHelp.Stdout, StringComparison.Ordinal);
Assert.Contains("--no-progress", executableHelp.Stdout, StringComparison.Ordinal);
Assert.Contains("narrat", readme, StringComparison.OrdinalIgnoreCase);
Assert.Contains("stderr", readme, StringComparison.OrdinalIgnoreCase);
Assert.Contains("--verbose --format json", readme, StringComparison.Ordinal);
```

Add an executable acceptance test that runs:

```csharp
var verboseJson = await RunExecutableAsync(
    ["scan", repository.Path, "--verbose", "--format", "json"]);
```

Assert exit code 0, stdout parses as the unchanged scan JSON envelope, stderr
contains discovery/repository/completion milestones, and stderr has neither
carriage returns nor ANSI escape sequences. Add a verbose dry-run executable
case asserting `Validated` and no progress line claiming `Deleted`.

- [ ] **Step 2: Run acceptance tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter FullyQualifiedName~EndToEndTests
```

Expected: FAIL because help and README still describe the old verbose/progress
semantics.

- [ ] **Step 3: Update help and README**

Change CLI help so the console section states:

```text
Console:      --quiet --no-color
Progress:     --verbose (milestones) --no-progress (disable animation)
```

Update the option matrix:

- `--details`: “Include candidate rows in the final scan report.”
- `--verbose`: “Narrate meaningful operation stages on stderr and include
  detailed final diagnostics.”
- `--no-progress`: “Disable automatic interactive animation; explicit verbose
  milestones remain enabled.”
- `--quiet`: mention that it suppresses progress and narration while retaining
  summary and genuine errors.

Update “Output and automation contract” with the exact renderer matrix. Add
examples for an interactive scan, verbose redirected stderr, piped stdout with
terminal progress, and:

```console
repoglean scan ~/src --verbose --format json > report.json 2> scan.log
```

State explicitly that JSON stdout remains one document and that `scan.log`
contains plain append-only diagnostics.

- [ ] **Step 4: Run the complete acceptance gate**

Run in this order:

```bash
dotnet build RepoGlean.slnx --configuration Release --warnaserror
dotnet test RepoGlean.slnx --configuration Release --no-build
dotnet format RepoGlean.slnx --verify-no-changes --no-restore
git diff --check
dotnet publish src/RepoGlean/RepoGlean.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained \
  -p:PublishAot=true \
  --output artifacts/native/osx-arm64
mkdir -p artifacts/package/repoglean-osx-arm64
cp artifacts/native/osx-arm64/RepoGlean artifacts/package/repoglean-osx-arm64/repoglean
pwsh -NoProfile -File eng/native-smoke.ps1 \
  -ExecutablePath artifacts/package/repoglean-osx-arm64/repoglean
```

Expected:

- Release build succeeds with zero warnings/errors.
- The complete test suite reports zero failures.
- Format and diff checks are clean.
- Native AOT publish succeeds for the current arm64 macOS host.
- Packaged executable smoke reports
  `Native packaged-executable smoke PASS`.

If `pwsh` is unavailable, do not claim the Native AOT acceptance gate passed:
report that exact missing prerequisite, retain the successful publish
evidence, and either install/provide PowerShell with user authorization or run
the same committed smoke script in a verified PowerShell environment.

- [ ] **Step 5: Inspect the whole feature diff and commit**

Compare the branch to design commit `8668fa0` and verify every design section
has implementation/tests:

```bash
git diff --stat 8668fa0..HEAD
git diff --check 8668fa0..HEAD
git status --short
```

Commit the documentation and final acceptance tests:

```bash
git add README.md src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests/Acceptance/EndToEndTests.cs
git commit -m "docs: explain interactive progress"
```

Re-run `git status --short` and require a clean worktree before handing the
branch to `superpowers:finishing-a-development-branch`.
