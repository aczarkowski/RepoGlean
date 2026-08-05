# Unclassified Storage Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only `repoglean audit` command that reports significant, non-overlapping Git-ignored storage which no active RepoGlean rule classifies.

**Architecture:** Add Git-backed ignore provenance and a separate `RepositoryAuditor` domain pipeline that shares discovery and narrow path-policy helpers with scanning but never constructs cleanup candidates. Map immutable audit results into dedicated human and source-generated JSON reports, then orchestrate the command through the existing progress and exit-code conventions.

**Tech Stack:** .NET 10, C# records, BCL-only production code, `System.Text.Json` source generation, xUnit 2.9.3, real-Git fixtures, PowerShell Native AOT smoke tests.

## Global Constraints

- Audit is read-only and reports only ignored-but-unclassified storage inside discovered Git working trees.
- Audit findings never become `ArtifactCandidate` instances and no audit type is accepted by `ReclaimPlanner`, `CleanupService`, or quarantine cleanup.
- Git is the sole authority for ignore status, source, line, and pattern; RepoGlean does not parse ignore files.
- Findings are the highest honest non-overlapping ignored trees; active-rule and visible branches are carved out before totals and thresholding.
- The default minimum finding size is exactly `100 MiB` (104,857,600 bytes); audit alone accepts the exact `--min-size 0` value to disable it.
- Active built-in and custom rules classify matching trees; a disabled built-in rule does not classify them.
- Audit never follows links or reparse points, crosses mounts, enters reserved RepoGlean quarantines, or absorbs nested repositories.
- Sizes are saturating logical-size estimates, not physical allocation or guaranteed reclaimable capacity.
- Inaccessible, changing, or unclassifiable branches are omitted with exact-path warnings rather than guessed.
- Production remains BCL-only, Native AOT-compatible, cross-platform, and free of package-manager integration.
- Existing scan, plan, clean, rules, configuration, cleanup authority, and JSON contracts remain unchanged.

---

## File structure

### New production files

- `src/RepoGlean/Git/GitIgnoreMatch.cs` — immutable verbose `git check-ignore` result.
- `src/RepoGlean/Auditing/AuditModels.cs` — audit options, findings, per-repository results, and operation totals.
- `src/RepoGlean/Auditing/RepositoryAuditor.cs` — Git classification, one-pass aggregation, thresholding, and warnings.
- `src/RepoGlean/Scanning/RepositoryPathPolicy.cs` — narrow shared exclusion, quarantine, nested-repository, and visible-path helpers.
- `src/RepoGlean/Output/AuditReportModels.cs` — dedicated versioned JSON document and report mapping.

### New test files

- `tests/RepoGlean.Tests/Auditing/RepositoryAuditorTests.cs` — real-Git classification and aggregation contract.
- `tests/RepoGlean.Tests/Output/AuditReportTests.cs` — deterministic human and JSON audit reports.
- `tests/RepoGlean.Tests/Application/AuditCommandTests.cs` — application orchestration, streams, options, status, and read-only behavior.

### Existing files modified

- `src/RepoGlean/Cli/CliOptions.cs` and `CliParser.cs` — add `Audit` and its exact option/size contract.
- `src/RepoGlean/Git/GitClient.cs` — add bounded verbose ignore matching.
- `src/RepoGlean/Scanning/FileSystemIdentityProvider.cs` — make Linux mount-only lookup independent of birth-time identity.
- `src/RepoGlean/Scanning/RepositoryScanner.cs` — consume extracted path-policy helpers without behavior changes.
- `src/RepoGlean/Output/HumanReportWriter.cs`, `JsonReportWriter.cs`, and `ReportJsonContext.cs` — write dedicated audit documents.
- `src/RepoGlean/Progress/ProgressModels.cs`, `ProgressSnapshot.cs`, `OperationProgressTracker.cs`, and `VerboseProgressRenderer.cs` — identify audit work and finding counts without calling them candidates.
- `src/RepoGlean/RepoGleanApp.cs` — dispatch, run, report, cancel, fail, and document audit.
- Existing CLI, Git, scanning, progress, application, and acceptance tests — protect compatibility at integration seams.
- `README.md` and `eng/native-smoke.ps1` — public command contract and packaged Native AOT journey.

---

### Task 1: Establish the `audit` CLI surface and threshold contract

**Files:**
- Modify: `src/RepoGlean/Cli/CliOptions.cs`
- Modify: `src/RepoGlean/Cli/CliParser.cs`
- Modify: `src/RepoGlean/RepoGleanApp.cs`
- Test: `tests/RepoGlean.Tests/Cli/CliParserTests.cs`
- Test: `tests/RepoGlean.Tests/Application/ReadOnlyCommandTests.cs`

**Interfaces:**
- Consumes: existing `CliOptions.MinimumBytes`, root/filter/reporting options, and `ByteSizeParser.TryParse` positive-size behavior.
- Produces: `CommandKind.Audit`, audit-only `--min-size 0`, and help text listing `repoglean audit [root ...] [options]`.

- [ ] **Step 1: Write failing parser and help tests**

Add parser coverage for the full allowed matrix and exact rejected matrix:

```csharp
[Fact]
public void Parse_audit_accepts_discovery_reporting_and_zero_minimum()
{
    var result = CliParser.Parse([
        "audit", "root", "--repo", "api", "--exclude", "tmp/**",
        "--min-size", "0", "--all-drives", "--format", "json",
        "--config", "config.json", "--quiet", "--verbose",
        "--no-color", "--no-progress",
    ]);

    Assert.True(result.IsSuccess, result.Error);
    Assert.Equal(CommandKind.Audit, result.Value!.Command);
    Assert.Equal(0, result.Value.MinimumBytes);
}

[Theory]
[InlineData("--category", "cache")]
[InlineData("--details")]
[InlineData("--free", "1GiB")]
[InlineData("--all")]
[InlineData("--dry-run")]
[InlineData("--yes")]
public void Parse_audit_rejects_non_audit_options(params string[] option)
{
    var result = CliParser.Parse(["audit", .. option]);
    Assert.False(result.IsSuccess);
}

[Theory]
[InlineData("scan")]
[InlineData("plan", "--free", "1B")]
[InlineData("clean", "--dry-run")]
public void Parse_zero_minimum_remains_invalid_outside_audit(params string[] command)
{
    var result = CliParser.Parse([.. command, "--min-size", "0"]);
    Assert.False(result.IsSuccess);
    Assert.Contains("positive", result.Error, StringComparison.OrdinalIgnoreCase);
}
```

Extend the no-arguments help test to assert `repoglean audit` and an
`Audit options:` line while stderr remains empty.

- [ ] **Step 2: Run focused CLI tests and verify RED**

Run:

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~CliParserTests|FullyQualifiedName~ReadOnlyCommandTests.No_arguments"
```

Expected: FAIL because `CommandKind.Audit`, audit parsing, and help text do not exist.

- [ ] **Step 3: Implement the minimal command model and validation**

Add `Audit` to `CommandKind`, parse the command, and accept only:

```csharp
CommandKind.Audit => option is
    "--repo" or "--exclude" or "--min-size" or "--format" or "--config" or
    "--all-drives" or "--no-color" or "--quiet" or "--verbose" or "--no-progress",
```

Keep `ByteSizeParser.TryParse` strictly positive. In the `--min-size` case,
recognize only `minimumSize.Trim() == "0"` as zero; otherwise call the existing
parser. After the command is known, reject zero unless the command is audit:

```csharp
if (minimumBytes == 0 && command != CommandKind.Audit)
{
    return ParseResult<CliOptions>.Failure("--min-size must be positive outside audit.");
}
```

Add audit usage/help. Add a temporary audit dispatch which prints
`Error: audit is not implemented.` to stderr and returns `2`; Task 6 replaces it.

- [ ] **Step 4: Run CLI tests and verify GREEN**

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~CliParserTests|FullyQualifiedName~ByteSizeParserTests|FullyQualifiedName~ReadOnlyCommandTests.No_arguments"
```

Expected: PASS; existing positive byte-size behavior is unchanged.

- [ ] **Step 5: Commit the CLI slice**

```bash
git add src/RepoGlean/Cli src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests/Cli tests/RepoGlean.Tests/Application/ReadOnlyCommandTests.cs
git commit -m "feat: add audit command surface"
```

---

### Task 2: Add bounded Git ignore provenance

**Files:**
- Create: `src/RepoGlean/Git/GitIgnoreMatch.cs`
- Modify: `src/RepoGlean/Git/GitClient.cs`
- Test: `tests/RepoGlean.Tests/Git/GitClientTests.cs`

**Interfaces:**
- Consumes: `ProcessRunner`, `GitClient.MaximumCheckIgnoreBatchSize`, relative-path validation, and null-delimited process input.
- Produces: `GitIgnoreMatch` and `GitClient.GetIgnoreMatchesAsync(string repositoryRoot, IReadOnlyList<string> repositoryRelativePaths, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write failing real-Git provenance tests**

Add matching and non-matching coverage:

```csharp
[Fact]
public async Task Verbose_ignore_matches_preserve_source_line_pattern_and_path()
{
    using var temporary = new TemporaryDirectory();
    var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
    repository.Write(".gitignore", "visible/\ncache*/\n");
    repository.Write("visible/file.bin", "tracked");
    await repository.CommitAllAsync();
    repository.Write("cache space/payload.bin", "ignored");

    var matches = await new GitClient().GetIgnoreMatchesAsync(
        repository.Path, ["cache space", "visible/file.bin"]);

    var ignored = matches["cache space"];
    Assert.True(ignored.IsIgnored);
    Assert.Equal(".gitignore", ignored.Source);
    Assert.Equal(2, ignored.SourceLine);
    Assert.Equal("cache*/", ignored.Pattern);
    Assert.False(matches["visible/file.bin"].IsIgnored);
}
```

On non-Windows systems add `cache\nline` and prove it remains one exact key.
Add `.git/info/exclude` coverage. Add a configured `core.excludesFile` fixture
and assert its source is absolute. Add an ignore followed by `!keep/` and
assert `IsIgnored == false` while the negated pattern is preserved.

- [ ] **Step 2: Run Git tests and verify RED**

```bash
dotnet test RepoGlean.slnx --filter FullyQualifiedName~GitClientTests
```

Expected: FAIL because `GitIgnoreMatch` and `GetIgnoreMatchesAsync` do not exist.

- [ ] **Step 3: Implement the four-field null-delimited protocol**

Create:

```csharp
namespace RepoGlean.Git;

public sealed record GitIgnoreMatch(
    string Path,
    string? Source,
    int? SourceLine,
    string? Pattern)
{
    public bool IsIgnored =>
        !string.IsNullOrEmpty(Pattern) && !Pattern.StartsWith('!');
}
```

Run Git with:

```csharp
[
    "-C", Path.GetFullPath(repositoryRoot),
    "check-ignore", "--verbose", "--non-matching", "--stdin", "-z",
]
```

Validate and normalize inputs as `GetIgnoredPathsAsync` does, chunk at 128,
and send `path + '\0'`. Accept exits `0` and `1`. Parse every record as
`source NUL line NUL pattern NUL path NUL`. Empty evidence becomes `null`;
non-empty lines must be positive invariant integers. Throw `GitCommandException`
for malformed, duplicate, missing, unexpected, or truncated records.

- [ ] **Step 4: Run provenance and scanner regressions**

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~GitClientTests|FullyQualifiedName~RepositoryScannerTests"
```

Expected: PASS, including existing 128-path batching and newline tests.

- [ ] **Step 5: Commit the Git protocol slice**

```bash
git add src/RepoGlean/Git tests/RepoGlean.Tests/Git tests/RepoGlean.Tests/Scanning/RepositoryScannerTests.cs
git commit -m "feat: report Git ignore provenance"
```

---

### Task 3: Separate read-only mount checks from cleanup identity

**Files:**
- Modify: `src/RepoGlean/Scanning/FileSystemIdentityProvider.cs`
- Test: `tests/RepoGlean.Tests/Scanning/FileTreeAnalyzerTests.cs`
- Test: `tests/RepoGlean.Tests/Scanning/RepositoryDiscoveryTests.cs`

**Interfaces:**
- Consumes: `IVolumeBoundary.TryGetMountIdentity`, `FileSystemMountIdentity`, native Windows/macOS identity paths, and Linux `statx`.
- Produces: Linux mount-only lookup requiring `STATX_MNT_ID` but not inode or birth time; stable cleanup identity requirements remain unchanged.

- [ ] **Step 1: Write failing mount-mask separation tests**

```csharp
[Theory]
[InlineData(0x1000u, true)]
[InlineData(0x1800u, true)]
[InlineData(0x0800u, false)]
[InlineData(0u, false)]
public void Linux_mount_identity_requires_mount_id_but_not_birth_time(
    uint mask,
    bool expected)
{
    Assert.Equal(expected,
        FileSystemIdentityProvider.HasRequiredLinuxMountIdentity(mask));
}
```

Retain `Linux_identity_requires_inode_birth_time_and_mount_id_masks` unchanged.
Add a provider test asserting `TryGetMountIdentity` succeeds locally and returns
the same mount before and after a file rename.

- [ ] **Step 2: Run filesystem tests and verify RED**

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~FileTreeAnalyzerTests|FullyQualifiedName~RepositoryDiscoveryTests"
```

Expected: FAIL because `HasRequiredLinuxMountIdentity` does not exist and Linux
mount lookup still delegates to full stable identity.

- [ ] **Step 3: Implement Linux mount-only `statx` lookup**

Keep Windows and macOS mount lookup based on their existing successful identity
calls. On Linux call:

```csharp
private static bool TryGetLinuxMountIdentity(
    string path,
    out FileSystemMountIdentity? identity,
    out string? error)
```

Use `AT_SYMLINK_NOFOLLOW`, request `LinuxStatxMountId`, and require only:

```csharp
internal static bool HasRequiredLinuxMountIdentity(uint mask) =>
    (mask & LinuxStatxMountId) == LinuxStatxMountId;
```

Build the mount identity from device major/minor and mount ID. Do not weaken
`TryGetLinuxIdentity` or `HasRequiredLinuxIdentity`; cleanup scanning still
requires inode, birth time, and mount ID.

- [ ] **Step 4: Run mount and cleanup-identity regressions**

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~FileTreeAnalyzerTests|FullyQualifiedName~RepositoryDiscoveryTests|FullyQualifiedName~CleanupIdentityTests|FullyQualifiedName~BoundaryAwareDeleterTests"
```

Expected: PASS; read-only mount consumers no longer depend on birth time and
cleanup identity remains fail-closed.

- [ ] **Step 5: Commit the mount-boundary slice**

```bash
git add src/RepoGlean/Scanning/FileSystemIdentityProvider.cs tests/RepoGlean.Tests/Scanning/FileTreeAnalyzerTests.cs tests/RepoGlean.Tests/Scanning/RepositoryDiscoveryTests.cs
git commit -m "refactor: separate mount and cleanup identity"
```

---

### Task 4: Implement non-overlapping unclassified storage auditing

**Files:**
- Create: `src/RepoGlean/Auditing/AuditModels.cs`
- Create: `src/RepoGlean/Auditing/RepositoryAuditor.cs`
- Create: `src/RepoGlean/Scanning/RepositoryPathPolicy.cs`
- Modify: `src/RepoGlean/Scanning/RepositoryScanner.cs`
- Test: `tests/RepoGlean.Tests/Auditing/RepositoryAuditorTests.cs`
- Test: `tests/RepoGlean.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:**
- Consumes: `GitClient.GetIgnoreMatchesAsync`, `GitClient.ListVisibleFilesAsync`, `RuleCatalog`, `ArtifactRule.IsActiveFor`, `IVolumeBoundary`, `IFileTimestampProvider`, exclusions, and `OperationWarning`.
- Produces: `AuditOptions`, `AuditFinding`, `RepositoryAuditResult`, `AuditResult`, and `RepositoryAuditor.AuditAsync(IReadOnlyList<string>, RuleCatalog, AuditOptions?, CancellationToken)`.

`RepositoryAuditor` has a public production constructor and one injectable
test/progress constructor:

```csharp
public RepositoryAuditor(GitClient git);

internal RepositoryAuditor(
    GitClient git,
    IVolumeBoundary volumeBoundary,
    IFileTimestampProvider timestampProvider,
    IOperationProgress progress);
```

- [ ] **Step 1: Write failing real-Git aggregation tests**

Create a helper using `MinimumBytes: 0` and this mixed fixture:

```csharp
[Fact]
public async Task Audit_collapses_unknown_storage_and_carves_classified_and_visible_branches()
{
    using var temporary = new TemporaryDirectory();
    var repository = await GitTestRepository.CreateAsync(temporary.GetPath("repo"));
    repository.Write("project.csproj", "<Project />");
    repository.Write(".gitignore", "unknown/\nobj/\n");
    repository.Write("unknown/tracked.txt", "keep");
    await repository.GitAsync("add", "-f", "unknown/tracked.txt");
    await repository.CommitAllAsync();
    repository.WriteBytes("unknown/a.bin", 11);
    repository.WriteBytes("unknown/nested/b.bin", 13);
    repository.WriteBytes("unknown/obj/classified.bin", 17);

    var result = await new RepositoryAuditor(new GitClient()).AuditAsync(
        [repository.Path],
        RuleCatalog.Create(RepoGleanConfig.Default),
        new AuditOptions([], [], 0));

    var finding = Assert.Single(Assert.Single(result.Repositories).Findings);
    Assert.Equal("unknown", finding.RelativePath);
    Assert.Equal(2, finding.FileCount);
    Assert.Equal(24, finding.EstimatedBytes);
    Assert.Equal("unknown/", finding.Ignore.Pattern);
}
```

Add focused tests proving sibling ignored roots remain separate; active custom
and built-in matches are carved out; disabling `dotnet.obj` makes `obj`
unclassified; a visible parent can yield ignored child findings; thresholding
happens after carving; empty ignored directories yield nothing; successfully
audited empty repositories remain in results; broken repositories warn without
discarding valid ones; filters/exclusions retain scan semantics; and
cancellation is observed.

- [ ] **Step 2: Run auditor tests and verify RED**

```bash
dotnet test RepoGlean.slnx --filter FullyQualifiedName~RepositoryAuditorTests
```

Expected: FAIL because the auditing namespace and service do not exist.

- [ ] **Step 3: Define immutable models and extract narrow shared policy**

Create:

```csharp
public sealed record AuditOptions(
    IReadOnlyList<string> RepositoryFilters,
    IReadOnlyList<string> Exclusions,
    long MinimumBytes)
{
    public const long DefaultMinimumBytes = 100L * 1024 * 1024;
}

public sealed record AuditFinding(
    string RepositoryRoot,
    string AbsolutePath,
    string RelativePath,
    long FileCount,
    long EstimatedBytes,
    DateTimeOffset? NewestWriteTimeUtc,
    GitIgnoreMatch Ignore);

public sealed record RepositoryAuditResult(
    string RepositoryRoot,
    IReadOnlyList<AuditFinding> Findings,
    long FileCount,
    long EstimatedBytes,
    IReadOnlyList<OperationWarning> Warnings);

public sealed record AuditResult(
    IReadOnlyList<RepositoryAuditResult> Repositories,
    long FileCount,
    long EstimatedBytes,
    IReadOnlyList<OperationWarning> Warnings);
```

Extract only platform path comparison, relative normalization, exclusions,
reserved quarantine detection, nested-repository detection, and visible-path
containment into `RepositoryPathPolicy`. Replace scanner private calls with
the helper and run scanner tests before adding audit traversal; scanner reports
must not change.

- [ ] **Step 4: Implement one classification and measurement walk**

Implement:

```csharp
public Task<AuditResult> AuditAsync(
    IReadOnlyList<string> repositoryRoots,
    RuleCatalog ruleCatalog,
    AuditOptions? options = null,
    CancellationToken cancellationToken = default)
```

For each filtered repository:

1. verify the working tree and list visible paths;
2. activate every enabled rule whose markers match;
3. capture the repository mount through `IVolumeBoundary`;
4. enumerate direct children in deterministic order;
5. prune exclusions, quarantines, links, nested repositories, foreign mounts,
   and active-rule matches before counting;
6. classify remaining siblings in chunks of at most 128 through
   `GetIgnoreMatchesAsync`;
7. recurse once through directories, exclude exact visible files, and count
   only ignored unmatched regular files;
8. when the current directory is ignored and has countable bytes, emit it as
   the highest finding and absorb descendant aggregates; otherwise bubble
   non-overlapping child findings upward;
9. apply `MinimumBytes` after carving and use saturating totals.

Use an internal aggregate containing counts, bytes, newest timestamp,
timestamp-unavailable state, and child findings. One failed timestamp makes
that aggregate timestamp `null` but does not discard observed bytes. Links,
inaccessible/changing branches, and mount boundaries are omitted with exact
warnings.

When a multi-path Git call fails, split recursively. A failing single path is
warned and omitted; never interpret Git failure as “not ignored.” Add every
successfully audited repository even with zero findings, but omit a repository
whose initial Git queries fail.

- [ ] **Step 5: Add boundary and hostile-name tests, then verify GREEN**

Add tests for quarantines, nested repositories, symlinks, an injected foreign
mount, spaces, Unicode, tabs, and platform-supported newlines. Add timestamp
tests for newest/null behavior. Add a Git wrapper proving 129 siblings use two
calls and a single-path failure does not suppress its sibling.

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~RepositoryAuditorTests|FullyQualifiedName~RepositoryScannerTests|FullyQualifiedName~GitClientTests"
```

Expected: PASS with exact non-overlapping counts, sizes, paths, provenance,
warnings, and deterministic ordering.

- [ ] **Step 6: Commit the audit-domain slice**

```bash
git add src/RepoGlean/Auditing src/RepoGlean/Scanning tests/RepoGlean.Tests/Auditing tests/RepoGlean.Tests/Scanning
git commit -m "feat: audit unclassified ignored storage"
```

---

### Task 5: Add dedicated audit reports

**Files:**
- Create: `src/RepoGlean/Output/AuditReportModels.cs`
- Modify: `src/RepoGlean/Output/HumanReportWriter.cs`
- Modify: `src/RepoGlean/Output/JsonReportWriter.cs`
- Modify: `src/RepoGlean/Output/ReportJsonContext.cs`
- Test: `tests/RepoGlean.Tests/Output/AuditReportTests.cs`
- Test: `tests/RepoGlean.Tests/Output/ReportWriterTests.cs`

**Interfaces:**
- Consumes: `AuditResult`, effective roots, `HumanReportOptions`, byte formatting, and source-generated JSON conventions.
- Produces: `AuditFindingReport`, `AuditRepositoryReport`, `AuditTotalsReport`, `AuditReportDocument.FromAudit`, `HumanReportWriter.WriteAudit`, and `JsonReportWriter.WriteAsync(AuditReportDocument, ...)`.

- [ ] **Step 1: Write failing human and JSON contract tests**

Create a fixture with two repositories, descending sizes, equal-size path ties,
one warning, a repository-relative ignore source, and an external source.
Assert the dedicated envelope:

```csharp
Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
Assert.Equal("audit", root.GetProperty("operation").GetString());
Assert.Equal("partial", root.GetProperty("status").GetString());
Assert.Equal(3, root.GetProperty("totals").GetProperty("findingCount").GetInt64());
Assert.Equal(JsonValueKind.Number,
    root.GetProperty("repositories")[0]
        .GetProperty("findings")[0]
        .GetProperty("estimatedBytes").ValueKind);
Assert.Equal(JsonValueKind.Null,
    root.GetProperty("repositories")[0]
        .GetProperty("findings")[0]
        .GetProperty("newestWriteTimeUtc").ValueKind);
```

For human output assert the four summary values, grouped stable ordering,
`.gitignore:42  /unknown/` provenance, quiet summary behavior, sanitized control
characters, and absence of `safe`, `eligible`, `reclaimable`, or `deletable`.

- [ ] **Step 2: Run output tests and verify RED**

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~AuditReportTests|FullyQualifiedName~ReportWriterTests"
```

Expected: FAIL because audit report types and writers do not exist.

- [ ] **Step 3: Implement the dedicated audit document**

Create this envelope:

```csharp
public sealed record AuditReportDocument(
    int SchemaVersion,
    string Operation,
    string Status,
    IReadOnlyList<string> EffectiveRoots,
    IReadOnlyList<AuditRepositoryReport> Repositories,
    AuditTotalsReport Totals,
    IReadOnlyList<ReportMessage> Warnings,
    IReadOnlyList<ReportMessage> Errors);
```

Repository reports contain `Root`, `Findings`, `FileCount`, and
`EstimatedBytes`. Finding reports contain paths, counts, bytes, an explicitly
present nullable timestamp, and explicitly present nullable `IgnoreSource`,
`IgnoreSourceLine`, and `IgnorePattern`.

`FromAudit` sorts repositories by bytes then platform path and findings by bytes
then relative path. Normalize an ignore source inside the repository to a
repository-relative path, preserve already-relative Git sources, and normalize
external sources to absolute paths. Use `partial` for warnings and count every
successfully audited repository, including empty ones.

Add `Interrupted()` and `Failure(string message)` factories with empty audit
results. Register every type for source generation and overload the JSON writer.
Use per-property `JsonIgnore(Condition = JsonIgnoreCondition.Never)` so nullable
timestamp and provenance fields serialize as JSON `null`.

- [ ] **Step 4: Implement deterministic human output and verify GREEN**

Add:

```csharp
public static void WriteAudit(
    AuditReportDocument report,
    long minimumBytes,
    TextWriter output,
    HumanReportOptions options)
```

Always write repository count, finding count, estimated unclassified storage,
and effective minimum. Unless quiet, write roots, repository groups, finding
and provenance rows, and warnings under existing verbose detail rules. Sanitize
human paths/patterns with `ProgressText.Sanitize`; JSON retains exact raw values.

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~AuditReportTests|FullyQualifiedName~ReportWriterTests|FullyQualifiedName~ReclaimReportTests"
```

Expected: PASS and existing report contract tests remain unchanged.

- [ ] **Step 5: Commit the reporting slice**

```bash
git add src/RepoGlean/Output tests/RepoGlean.Tests/Output
git commit -m "feat: report unclassified storage audits"
```

---

### Task 6: Orchestrate audit with progress, status, and stream isolation

**Files:**
- Modify: `src/RepoGlean/Progress/ProgressModels.cs`
- Modify: `src/RepoGlean/Progress/ProgressSnapshot.cs`
- Modify: `src/RepoGlean/Progress/OperationProgressTracker.cs`
- Modify: `src/RepoGlean/Progress/VerboseProgressRenderer.cs`
- Modify: `src/RepoGlean/Auditing/RepositoryAuditor.cs`
- Modify: `src/RepoGlean/RepoGleanApp.cs`
- Create: `tests/RepoGlean.Tests/Application/AuditCommandTests.cs`
- Modify: `tests/RepoGlean.Tests/Progress/OperationProgressTrackerTests.cs`
- Modify: `tests/RepoGlean.Tests/Progress/ProgressSnapshotTests.cs`
- Modify: `tests/RepoGlean.Tests/Progress/VerboseProgressRendererTests.cs`
- Modify: `tests/RepoGlean.Tests/Progress/InteractiveProgressRendererTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 4, and 5 plus existing root/discovery and renderer selection behavior.
- Produces: `ProgressOperation.Audit`, `OperationProgressEvent.CurrentFindingCount`, `OperationProgressEvent.FindingCount`, and `RepoGleanApp.RunAuditAsync`.

- [ ] **Step 1: Write failing command and progress tests**

Create application tests proving CLI roots override configured roots over home;
configuration and CLI exclusions combine; omitted minimum is `104_857_600`;
positive and zero overrides work; JSON stdout is one document with ordinary
stderr empty; verbose JSON uses append-only stderr; quiet retains the summary;
no findings returns `0`; warnings `3`; missing Git `1`; bad options `2`;
pre-cancellation `130`; and tracked, untracked, ignored, and Git metadata remain
unchanged.

Add progress assertions:

```csharp
Assert.Contains("Auditing repositories 1/2", snapshot.Format());
Assert.Contains("3 findings", snapshot.Format());
Assert.Equal(
    "Audit complete: 2 repositories, 3 findings, 1 warning.",
    renderedLine);
```

- [ ] **Step 2: Run application and progress tests and verify RED**

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~AuditCommandTests|FullyQualifiedName~ProgressSnapshotTests|FullyQualifiedName~OperationProgressTrackerTests|FullyQualifiedName~VerboseProgressRendererTests|FullyQualifiedName~InteractiveProgressRendererTests"
```

Expected: FAIL because audit orchestration and progress vocabulary do not exist.

- [ ] **Step 3: Add audit-specific progress vocabulary**

Add `Audit` to `ProgressOperation`; add `CurrentFindingCount` and `FindingCount`
to events and snapshots rather than reusing candidate fields. Store the current
operation in the snapshot so repository events render scan or audit wording.

Include audit in read-only interruption tracking. Render exact verbose forms:

```text
Auditing [1/2] /repos/example...
Found 3 findings in example (18 GiB estimated).
Audit complete: 2 repositories, 3 findings, 1 warning.
Audit interrupted: 1 repository, 2 findings, 18 GiB estimated, 0 warnings.
Audit failed: <message>
```

Inject `IOperationProgress` into `RepositoryAuditor` and emit repository
start/completion, warning, cumulative finding count, and cumulative bytes.
Catch renderer exceptions exactly as scanner does.

- [ ] **Step 4: Implement `RunAuditAsync` and audit failure documents**

Replace temporary dispatch with:

```csharp
case CommandKind.Audit:
    return await RunAuditAsync(
        options, config, runtime, stdout, stderr, cancellationToken)
        .ConfigureAwait(false);
```

`RunAuditAsync` creates tracked progress, resolves roots/exclusions like scan,
verifies Git, discovers with `ProgressOperation.Audit`, resolves
`options.MinimumBytes ?? AuditOptions.DefaultMinimumBytes`, audits with
repository filters, prepends discovery warnings, writes the selected report,
and returns `0` or `3`.

In outer cancellation/operational catches, use `AuditReportDocument.Interrupted`
or `.Failure` for JSON audit requests; other commands keep `ReportDocument`.
Table failures remain on stderr. Pause progress before final output.

- [ ] **Step 5: Run command, progress, and read-only regressions**

```bash
dotnet test RepoGlean.slnx --filter "FullyQualifiedName~AuditCommandTests|FullyQualifiedName~ReadOnlyCommandTests|FullyQualifiedName~Progress|FullyQualifiedName~PlanCommandTests|FullyQualifiedName~CleanCommandTests"
```

Expected: PASS with audit vocabulary and unchanged scan/plan/clean contracts.

- [ ] **Step 6: Commit the orchestration slice**

```bash
git add src/RepoGlean/Auditing src/RepoGlean/Progress src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests/Application tests/RepoGlean.Tests/Progress
git commit -m "feat: expose read-only storage audit"
```

---

### Task 7: Document and acceptance-verify the packaged command

**Files:**
- Modify: `README.md`
- Modify: `eng/native-smoke.ps1`
- Modify: `tests/RepoGlean.Tests/Acceptance/EndToEndTests.cs`

**Interfaces:**
- Consumes: completed audit command, dedicated audit JSON, public exit codes, and packaged executable helper.
- Produces: public user contract and Native AOT evidence that audit is read-only and excludes recognized candidates.

- [ ] **Step 1: Write failing built-executable and release-surface tests**

Create a published-executable fixture containing tracked source and ignore
rules, recognized ignored `obj`, ignored unclassified `local-state`, a
below-threshold sibling, and a Unicode or space-bearing ignored path.

Capture recursive relative paths, regular-file bytes, and Git status. Run:

```text
audit <repository> --min-size 1B --format json --no-progress
```

Assert only unclassified paths appear, totals exclude `obj`, provenance is
present, stdout is one JSON document, stderr is empty, exit is `0`, and the
post-run snapshot and Git status exactly equal their pre-run values.

Extend release-surface assertions to require `repoglean audit`, `100 MiB`,
`ignored-but-unclassified`, and the statement that findings are not cleanup
candidates in help/README as appropriate.

- [ ] **Step 2: Run acceptance tests and verify RED**

```bash
dotnet test RepoGlean.slnx --filter FullyQualifiedName~EndToEndTests
```

Expected: FAIL because public and packaged audit coverage is incomplete.

- [ ] **Step 3: Update README and Native AOT smoke**

Document command syntax and option matrix, default/zero thresholds, highest
non-overlapping behavior, rule/visible carve-outs, provenance, logical-size
limits, read-only authority, JSON envelope, and exit codes. State explicitly
that findings are unclassified evidence, not safe or eligible candidates.

In `eng/native-smoke.ps1`, add `audit-state/` to `.gitignore`, create a small
payload, snapshot its bytes, and invoke audit with `--min-size 1B`:

```powershell
if ($audit.operation -ne "audit" -or
    $audit.status -ne "success" -or
    $audit.totals.findingCount -ne 1 -or
    $audit.repositories[0].findings[0].relativePath -ne "audit-state") {
    throw "Native audit returned an unexpected JSON result."
}
```

Assert `obj` and `node_modules` are absent from findings and all fixtures still
exist immediately after audit.

- [ ] **Step 4: Run focused acceptance and full Release gates**

```bash
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx --configuration Release --no-restore --warnaserror
dotnet test RepoGlean.slnx --configuration Release --no-build
```

Expected: warning-as-error build and complete suite pass.

Publish and smoke the local host architecture:

```bash
dotnet publish src/RepoGlean/RepoGlean.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained \
  -p:PublishAot=true
pwsh -NoProfile -File eng/native-smoke.ps1 \
  -ExecutablePath src/RepoGlean/bin/Release/net10.0/osx-arm64/publish/repoglean
```

Expected: Native AOT publish succeeds and smoke prints its PASS line. On another
host architecture substitute its exact RID and executable suffix; do not claim
unrun platforms.

- [ ] **Step 5: Review the complete diff and commit**

Check every section of
`docs/superpowers/specs/2026-08-05-unclassified-storage-audit-design.md`, then:

```bash
git diff --check
git status --short
git diff --stat
```

Confirm no mutation path accepts audit models, no audit language claims safety,
existing JSON shapes are unchanged, and no unfinished markers or unrelated files
remain.

```bash
git add README.md eng/native-smoke.ps1 tests/RepoGlean.Tests/Acceptance/EndToEndTests.cs
git commit -m "docs: publish unclassified storage audit"
```

---

## Final acceptance gate

After all task commits, rerun fresh evidence from the implementation branch:

```bash
git diff --check master...HEAD
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx --configuration Release --no-restore --warnaserror
dotnet test RepoGlean.slnx --configuration Release --no-build
dotnet publish src/RepoGlean/RepoGlean.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained \
  -p:PublishAot=true
pwsh -NoProfile -File eng/native-smoke.ps1 \
  -ExecutablePath src/RepoGlean/bin/Release/net10.0/osx-arm64/publish/repoglean
```

Expected results:

- clean diff check;
- warning-as-error Release build succeeds;
- full suite passes with no skipped audit contract tests;
- Native AOT publish succeeds for the local host RID;
- packaged smoke proves scan, plan, guarded cleanup, and audit;
- audit JSON contains one document and normal stderr stays empty;
- audit reports only ignored-but-unclassified storage;
- recognized candidates, visible content, links, nested repositories, foreign
  mounts, quarantine payloads, and below-threshold storage are not counted;
- the audit journey leaves repository bytes and Git state unchanged.

Do not describe local macOS AOT evidence as Windows/Linux CI evidence. Remote
cross-platform verification remains unverified until its CI run is inspected.
