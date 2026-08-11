# Portable Audit Traversal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace audit-specific native filesystem traversal with one unbounded iterative .NET traversal and add an audit-only `--cross-mounts` opt-in.

**Architecture:** A small `IAuditFileSystem` returns immutable BCL-derived entry snapshots without handles or stable identities. `RepositoryAuditor` uses explicit enter/complete work frames for deterministic post-order aggregation and reuses the existing `IVolumeBoundary` only when `CrossMounts` is false.

**Tech Stack:** .NET 10, C#, `System.IO`, existing `GitClient`, xUnit, Native AOT, GitHub Actions six-platform matrix.

## Global Constraints

- Audit remains strictly read-only; findings never flow into planning or cleanup.
- Git remains the sole authority for ignore status and provenance.
- Default audit prunes foreign and mount-unknown branches; `--cross-mounts` bypasses mount checks but never follows links.
- Traversal uses no recursive method call, `SearchOption.AllDirectories`, or `EnumerationOptions.MaxRecursionDepth`.
- Audit code contains no direct `DllImport`, native structure, architecture switch, native handle, stable identity, reopen operation, or `PlatformNotSupportedException`.
- The audit JSON schema, finding model, threshold semantics, ordering, progress, exclusions, active-rule carving, nested-repository behavior, and saturating arithmetic remain compatible.
- Existing scan, plan, clean, quarantine, recovery, and shared `FileSystemIdentityProvider` safety behavior is unchanged.
- Filesystem races are best-effort: observed path-local failures warn and prune; stale metadata that changes after observation is permitted.
- No second traversal engine, configuration property, or traversal-depth option is added.

---

### Task 1: Add the audit-only cross-mount CLI contract

**Files:**
- Modify: `src/RepoGlean/Cli/CliOptions.cs`
- Modify: `src/RepoGlean/Cli/CliParser.cs`
- Modify: `src/RepoGlean/Auditing/AuditModels.cs`
- Modify: `src/RepoGlean/RepoGleanApp.cs`
- Test: `tests/RepoGlean.Tests/Cli/CliParserTests.cs`
- Test: `tests/RepoGlean.Tests/Application/AuditCommandTests.cs`

**Interfaces:**
- Produces: `CliOptions.CrossMounts : bool`.
- Produces: `AuditOptions.CrossMounts : bool`, defaulting to `false` for existing callers.
- Consumes: existing audit CLI parsing and `RepoGleanApp.RunAuditAsync` option construction.

- [ ] **Step 1: Write failing parser tests**

Extend the accepted audit-options test with `--cross-mounts` and assert the value. Add a rejection theory for non-audit commands:

```csharp
Assert.True(result.Value!.CrossMounts);

[Theory]
[InlineData("scan")]
[InlineData("plan", "--free", "1B")]
[InlineData("clean", "--dry-run")]
public void Parse_cross_mounts_is_audit_only(params string[] command)
{
    var result = CliParser.Parse([.. command, "--cross-mounts"]);
    Assert.False(result.IsSuccess);
}
```

- [ ] **Step 2: Run the parser tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter FullyQualifiedName~CliParserTests
```

Expected: compilation fails because `CliOptions.CrossMounts` does not exist or parsing rejects the option.

- [ ] **Step 3: Add option plumbing**

Add a parser local, switch case, audit allow-list entry, constructor parameter, and property:

```csharp
var crossMounts = false;
// parser switch
case "--cross-mounts": crossMounts = true; break;

public bool CrossMounts { get; }
```

Extend the audit model without breaking existing tests:

```csharp
public sealed record AuditOptions(
    IReadOnlyList<string> RepositoryFilters,
    IReadOnlyList<string> Exclusions,
    long MinimumBytes,
    bool CrossMounts = false)
{
    public const long DefaultMinimumBytes = 100L * 1024 * 1024;
}
```

Pass `options.CrossMounts` from `RepoGleanApp.RunAuditAsync` into `AuditOptions`.

- [ ] **Step 4: Add an application propagation test**

Use a repository fixture and invoke:

```csharp
var result = await RunAsync([
    "audit", repository.Path, "--cross-mounts", "--min-size", "0", "--format", "json",
]);
Assert.NotEqual(RepoGleanApp.UsageExitCode, result.ExitCode);
```

This test proves the application accepts and routes the flag; mount behavior is covered with injected providers in Task 3.

- [ ] **Step 5: Run focused tests and commit**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter "FullyQualifiedName~CliParserTests|FullyQualifiedName~AuditCommandTests"
git diff --check
```

Commit:

```bash
git add src/RepoGlean/Cli/CliOptions.cs src/RepoGlean/Cli/CliParser.cs src/RepoGlean/Auditing/AuditModels.cs src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests/Cli/CliParserTests.cs tests/RepoGlean.Tests/Application/AuditCommandTests.cs
git commit -m "feat: add audit cross-mount option"
```

---

### Task 2: Introduce the portable audit filesystem reader

**Files:**
- Create: `src/RepoGlean/Auditing/AuditFileSystem.cs`
- Create: `tests/RepoGlean.Tests/Auditing/AuditFileSystemTests.cs`

**Interfaces:**
- Produces: `AuditFileSystemEntry` immutable snapshot.
- Produces: `IAuditFileSystem.TryInspect` and `IAuditFileSystem.TryEnumerate`.
- Produces: `AuditFileSystem.NormalizeRootPath`.
- Consumes: `FileSystemEntryKind` and optional `IFileTimestampProvider` from `RepoGlean.Scanning`.

- [ ] **Step 1: Write failing BCL reader tests**

Cover root normalization, immediate-only enumeration, regular-file metadata, directory metadata, link classification, missing entries, and cancellation:

```csharp
var fileSystem = new AuditFileSystem();
Assert.True(fileSystem.TryEnumerate(root, CancellationToken.None, out var entries, out var error), error);
Assert.Contains(entries, entry =>
    entry.Name == "payload.bin" &&
    entry.Kind == FileSystemEntryKind.RegularFile &&
    entry.Length == 17 &&
    entry.InspectionError is null);
Assert.DoesNotContain(entries, entry => entry.Name == "nested.bin");
```

For a link created by the existing platform-aware test helper:

```csharp
var link = Assert.Single(entries, entry => entry.Name == "external-link");
Assert.Equal(FileSystemEntryKind.Link, link.Kind);
```

- [ ] **Step 2: Run the reader tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter FullyQualifiedName~AuditFileSystemTests
```

Expected: compilation fails because `AuditFileSystem` and its entry contract do not exist.

- [ ] **Step 3: Implement the minimal portable contract**

Create these production types:

```csharp
internal sealed record AuditFileSystemEntry(
    string Name,
    string AbsolutePath,
    FileSystemEntryKind Kind,
    long Length,
    DateTimeOffset? LastWriteTimeUtc,
    string? InspectionError);

internal interface IAuditFileSystem
{
    bool TryInspect(string absolutePath, out AuditFileSystemEntry? entry, out string? error);

    bool TryEnumerate(
        string absolutePath,
        CancellationToken cancellationToken,
        out IReadOnlyList<AuditFileSystemEntry> entries,
        out string? error);
}
```

`AuditFileSystem` uses only `Directory.EnumerateFileSystemEntries`, `File.GetAttributes`, `FileInfo`, and `DirectoryInfo`. `TryEnumerate` materializes exactly one directory level inside a guarded loop, checks cancellation around enumeration and inspection, and emits an entry with `InspectionError` for a child that vanishes or becomes inaccessible. A failure opening/enumerating the requested directory returns `false` with an empty collection.

Classify links before file/directory handling:

```csharp
var attributes = File.GetAttributes(path);
FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
    ? new DirectoryInfo(path)
    : new FileInfo(path);
var isLink = (attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
```

Catch only expected path-local exceptions:

```csharp
exception is UnauthorizedAccessException or IOException
```

Preserve the existing optional timestamp provider solely for deterministic tests; production reads `LastWriteTimeUtc` from `FileSystemInfo`.

- [ ] **Step 4: Run focused reader tests and format checks**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter FullyQualifiedName~AuditFileSystemTests
dotnet format RepoGlean.slnx --verify-no-changes --no-restore
git diff --check
```

Expected: all portable reader tests pass with no formatting changes.

- [ ] **Step 5: Commit the reader**

```bash
git add src/RepoGlean/Auditing/AuditFileSystem.cs tests/RepoGlean.Tests/Auditing/AuditFileSystemTests.cs
git commit -m "refactor: add portable audit filesystem reader"
```

---

### Task 3: Migrate audit behavior and remove native traversal

**Files:**
- Modify: `src/RepoGlean/Auditing/RepositoryAuditor.cs`
- Delete: `src/RepoGlean/Auditing/SecureAuditFileSystem.cs`
- Delete: `tests/RepoGlean.Tests/Auditing/SecureAuditFileSystemTests.cs`
- Modify: `tests/RepoGlean.Tests/Auditing/RepositoryAuditorTests.cs`

**Interfaces:**
- Consumes: `IAuditFileSystem`, `AuditFileSystemEntry`, `AuditOptions.CrossMounts`, and `IVolumeBoundary`.
- Produces: recursive-path implementation temporarily preserving external audit behavior; Task 4 replaces recursion without changing its tests.
- Produces: `AuditCheckpoint` with `BeforeDirectoryEnumeration` and `BeforeFileMeasurement` values for deterministic race/cancellation tests.

- [ ] **Step 1: Add failing mount-policy tests**

Use the existing `StubVolumeBoundary` and add a provider that always fails. Verify default pruning and cross-mount bypass:

```csharp
var conservative = await auditor.AuditAsync(
    [repository.Path], catalog, new AuditOptions([], [], 0, CrossMounts: false));
Assert.DoesNotContain(conservative.Repositories.Single().Findings, finding =>
    finding.RelativePath.StartsWith("foreign", StringComparison.Ordinal));
Assert.Contains(conservative.Warnings, warning => warning.Path == foreignRoot);

var permissive = await auditor.AuditAsync(
    [repository.Path], catalog, new AuditOptions([], [], 0, CrossMounts: true));
Assert.Contains(permissive.Repositories.Single().Findings, finding =>
    finding.RelativePath == "foreign");
```

The permissive assertion must also verify the failing provider's call count remains zero.

- [ ] **Step 2: Add failing best-effort change tests**

Retain checkpoint-driven tests but change their expected contract:

```csharp
checkpoint = (stage, path) =>
{
    if (stage == AuditCheckpoint.BeforeFileMeasurement && path == swappedPath)
    {
        File.Delete(swappedPath);
    }
};
```

Assert the vanished entry is omitted with a warning and its valid sibling remains. Remove expectations that require stable identity, held handles, or link-swap revalidation.

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter FullyQualifiedName~RepositoryAuditorTests
```

Expected: tests fail because `CrossMounts` is not enforced and audit still uses secure identities.

- [ ] **Step 4: Migrate `RepositoryAuditor` to snapshots and a separate mount provider**

Replace fields and constructors with:

```csharp
private readonly IAuditFileSystem fileSystem;
private readonly IVolumeBoundary volumeBoundary;
private readonly Action<AuditCheckpoint, string>? checkpoint;

internal RepositoryAuditor(
    GitClient git,
    IAuditFileSystem fileSystem,
    IVolumeBoundary volumeBoundary,
    IOperationProgress progress,
    Action<AuditCheckpoint, string>? checkpoint = null)
```

Default constructors use `new AuditFileSystem()` and `new FileSystemIdentityProvider()`. The existing timestamp-provider test constructor creates `new AuditFileSystem(timestampProvider)` and retains the supplied volume boundary.

Normalize roots through `AuditFileSystem.NormalizeRootPath`. Inspect the root through `TryInspect`; reject a root that is missing, not a directory, a link, or has an inspection error.

When `CrossMounts` is false, resolve the root mount once. Before descending into each directory, call `TryGetMountIdentity`; warn and prune when the identity is missing, unavailable, or differs. When `CrossMounts` is true, do not call the provider.

Refactor `AuditEntry` to hold `AuditFileSystemEntry` and remove all `TryReopen`, identity equality, handle disposal, and `TryConfirmUnchanged` paths. At the file checkpoint, re-inspect the path once so an observed deletion becomes a warning; accept ordinary metadata changes as a best-effort snapshot.

- [ ] **Step 5: Delete audit-native code and obsolete tests**

Delete `SecureAuditFileSystem.cs` and its native-contract test file. Preserve shared identity-provider code and tests under `Scanning` and `Cleaning`. Confirm no audit source references remain:

```bash
rg -n "ISecureAuditEntry|SecureAuditIdentity|UnixSecureAuditEntry|WindowsSecureAuditEntry|SecureAuditFileSystem|SecureAuditCheckpoint" src/RepoGlean tests/RepoGlean.Tests
```

Expected: no matches.

- [ ] **Step 6: Run audit-focused acceptance and commit**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter "FullyQualifiedName~RepositoryAuditorTests|FullyQualifiedName~AuditCommandTests|FullyQualifiedName~EndToEndTests"
dotnet build RepoGlean.slnx --no-restore -warnaserror
git diff --check
```

Commit:

```bash
git add src/RepoGlean/Auditing tests/RepoGlean.Tests/Auditing
git commit -m "refactor: replace native audit traversal"
```

---

### Task 4: Replace recursive aggregation with explicit work frames

**Files:**
- Create: `src/RepoGlean/Auditing/IterativeAuditTraversal.cs`
- Modify: `src/RepoGlean/Auditing/RepositoryAuditor.cs`
- Create: `tests/RepoGlean.Tests/Auditing/IterativeAuditTraversalTests.cs`
- Modify: `tests/RepoGlean.Tests/Auditing/RepositoryAuditorTests.cs`

**Interfaces:**
- Consumes: portable snapshot traversal established in Task 3.
- Produces: one small internal iterative scheduler with enter and complete work items; `RepositoryAuditor` supplies the audit-specific enter and complete operations.
- Preserves: `AuditResult`, `RepositoryAuditResult`, `AuditFinding`, progress, warnings, Git batching, and deterministic ordering.

- [ ] **Step 1: Write a failing deep-chain test**

Test the production scheduler with a 10,000-node single-child chain represented by integers, without creating OS paths or involving Git. Record enter and completion order and assert all nodes complete in post-order:

```csharp
var completed = new List<int>();
await IterativeAuditTraversal.TraverseAsync(
    root: 0,
    enterAsync: (depth, _) => ValueTask.FromResult<IReadOnlyList<int>>(
        depth == 9_999 ? [] : [depth + 1]),
    complete: completed.Add,
    CancellationToken.None);

Assert.Equal(10_000, completed.Count);
Assert.Equal(9_999, completed[0]);
Assert.Equal(0, completed[^1]);
```

This test catches replacing the scheduler with recursive calls. Existing `RepositoryAuditorTests` continue to exercise the real Git and filesystem integration rather than a synthetic Git mock.

The fake returns immutable snapshots and does not recurse internally.

- [ ] **Step 2: Run the deep test and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter FullyQualifiedName~Audit_traversal_has_no_recursion_depth_limit
```

Expected: compilation fails because `IterativeAuditTraversal` does not exist.

- [ ] **Step 3: Implement enter/complete work frames**

Create a small generic internal scheduler whose only responsibility is stack ordering:

```csharp
internal static class IterativeAuditTraversal
{
    public static async Task TraverseAsync<TFrame>(
        TFrame root,
        Func<TFrame, CancellationToken, ValueTask<IReadOnlyList<TFrame>>> enterAsync,
        Action<TFrame> complete,
        CancellationToken cancellationToken)
    {
        var work = new Stack<WorkItem<TFrame>>();
        work.Push(new EnterWork<TFrame>(root));
        while (work.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (item)
            {
                case EnterWork<TFrame> enter:
                    var children = await enterAsync(enter.Frame, cancellationToken)
                        .ConfigureAwait(false);
                    work.Push(new CompleteWork<TFrame>(enter.Frame));
                    for (var index = children.Count - 1; index >= 0; index--)
                    {
                        work.Push(new EnterWork<TFrame>(children[index]));
                    }
                    break;
                case CompleteWork<TFrame> finished:
                    complete(finished.Frame);
                    break;
            }
        }
    }
}
```

Keep audit state in explicit frames:

```csharp

private sealed class DirectoryFrame(
    string absolutePath,
    string relativePath,
    GitIgnoreMatch? ignore,
    DateTimeOffset? lastWriteTimeUtc,
    DirectoryFrame? parent)
{
    public string AbsolutePath { get; } = absolutePath;
    public string RelativePath { get; } = relativePath;
    public GitIgnoreMatch? Ignore { get; } = ignore;
    public DateTimeOffset? LastWriteTimeUtc { get; } = lastWriteTimeUtc;
    public DirectoryFrame? Parent { get; } = parent;
    public AuditAggregate Aggregate { get; } = new();
}
```

`RepositoryAuditor` calls the scheduler:

```csharp
await IterativeAuditTraversal.TraverseAsync(
    rootFrame,
    (frame, token) => EnterDirectoryAsync(frame, repositoryRoot, token),
    frame => CompleteDirectory(frame, repositoryRoot),
    cancellationToken);
```

`EnterDirectoryAsync` returns eligible child directory frames in sorted order and absorbs regular-file aggregates directly into the current frame. The scheduler supplies reverse stack insertion and post-order completion. `CompleteDirectory` observes the directory timestamp, collapses an ignored non-empty directory to its highest honest finding, and absorbs the result into its parent. The root completion returns the repository aggregate.

Remove recursive `AuditDirectoryContentsAsync` and `AuditDirectoryAsync` calls. Do not use BCL recursive enumeration.

- [ ] **Step 4: Run deep, focused, and full tests**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter FullyQualifiedName~Audit_traversal_has_no_recursion_depth_limit
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter "FullyQualifiedName~RepositoryAuditorTests|FullyQualifiedName~AuditCommandTests|FullyQualifiedName~EndToEndTests"
dotnet test RepoGlean.slnx --no-build
```

Expected: the deep test and all existing tests pass.

- [ ] **Step 5: Commit iterative traversal**

```bash
git add src/RepoGlean/Auditing/IterativeAuditTraversal.cs src/RepoGlean/Auditing/RepositoryAuditor.cs tests/RepoGlean.Tests/Auditing/IterativeAuditTraversalTests.cs tests/RepoGlean.Tests/Auditing/RepositoryAuditorTests.cs
git commit -m "refactor: make audit traversal iterative"
```

---

### Task 5: Document, audit the simplification, and run release acceptance

**Files:**
- Modify: `src/RepoGlean/RepoGleanApp.cs`
- Modify: `README.md`
- Modify: `tests/RepoGlean.Tests/Acceptance/EndToEndTests.cs`
- Modify: `tests/RepoGlean.Tests/Application/AuditCommandTests.cs`

**Interfaces:**
- Consumes: final CLI and traversal behavior.
- Produces: user-facing help and README contract for default mount pruning, `--cross-mounts`, and best-effort snapshots.

- [ ] **Step 1: Write failing help and end-to-end assertions**

Assert help contains the option and an end-to-end audit accepts it:

```csharp
Assert.Contains("--cross-mounts", result.StandardOutput, StringComparison.Ordinal);

var audit = await RunBuiltAsync(
    "audit", repository.Path, "--cross-mounts", "--min-size", "0", "--format", "json", "--no-progress");
Assert.Equal(0, audit.ExitCode);
```

- [ ] **Step 2: Run the acceptance tests and verify RED**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj --filter "FullyQualifiedName~EndToEndTests|FullyQualifiedName~AuditCommandTests"
```

Expected: help assertion fails until user-facing text is updated.

- [ ] **Step 3: Update user-facing documentation**

Add `--cross-mounts` to audit help and the README option matrix. Replace cleanup-grade secure-traversal claims with this exact behavioral boundary:

```text
Audit uses an iterative, best-effort .NET filesystem snapshot. It never follows
links or reparse points. By default it prunes foreign or unidentified mounts;
--cross-mounts opts into traversing those directories. Findings remain evidence
only and never authorize cleanup.
```

State that traversal has no configured depth limit and that observed concurrent changes warn or are omitted.

- [ ] **Step 4: Run structural simplification checks**

Run:

```bash
test ! -e src/RepoGlean/Auditing/SecureAuditFileSystem.cs
! rg -n "DllImport|PlatformNotSupportedException|RuntimeInformation|SafeFileHandle|Architecture" src/RepoGlean/Auditing
! rg -n "TryReopen|SecureAuditIdentity|ISecureAuditEntry" src/RepoGlean tests/RepoGlean.Tests
rg -n "--cross-mounts" README.md src/RepoGlean tests/RepoGlean.Tests
git diff --check
```

Expected: removed-native searches return no matches and the option appears in code, tests, help, and README.

- [ ] **Step 5: Run full local verification**

Run:

```bash
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx --no-restore -warnaserror
dotnet test RepoGlean.slnx --no-build
dotnet format RepoGlean.slnx --verify-no-changes --no-restore
git diff --check
```

Expected: restore, warning-as-error build, all tests, formatting, and whitespace checks pass.

- [ ] **Step 6: Run Native AOT acceptance on the available host**

Run with the host RID (`osx-arm64` in the current workspace):

```bash
dotnet restore src/RepoGlean/RepoGlean.csproj -r osx-arm64
dotnet publish src/RepoGlean/RepoGlean.csproj -c Release -r osx-arm64 --self-contained true --no-restore -p:PublishAot=true -o artifacts/portable-audit/osx-arm64
artifacts/portable-audit/osx-arm64/repoglean audit . --cross-mounts --min-size 0 --format json --no-progress
```

Expected: Native AOT publish succeeds and the packaged executable produces one valid audit JSON document without an unhandled exception. Qualify the six-platform result as unverified until the branch is pushed and the GitHub Actions matrix completes.

- [ ] **Step 7: Commit documentation and acceptance**

```bash
git add README.md src/RepoGlean/RepoGleanApp.cs tests/RepoGlean.Tests/Acceptance/EndToEndTests.cs tests/RepoGlean.Tests/Application/AuditCommandTests.cs
git commit -m "docs: describe portable audit traversal"
```

- [ ] **Step 8: Final review checkpoint**

Review the complete branch against `docs/superpowers/specs/2026-08-11-audit-portable-traversal-design.md`. Confirm every global constraint is represented by implementation or test evidence, no unrelated files changed, and the worktree is clean after commits.
