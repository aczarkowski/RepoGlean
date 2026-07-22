# Guarded Quarantine Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Couple Task 5 validation to permanent mutation through an atomic quarantine ownership transition, race-safe failure handling, and complete cancellation accounting.

**Architecture:** Revalidate the candidate, atomically rename it to a GUID-private directory on its requested-root filesystem, and verify the moved object's stable identity/type/mount before any deletion. Delegate the owned tree to the .NET recursive-delete primitive under the approved accidental-concurrency threat model, with deterministic hooks around move, identity verification, and deletion.

**Tech Stack:** C# 14, .NET 10 BCL `System.IO`, existing native stable identity provider, xUnit, real temporary Git repositories, Native AOT.

## Global Constraints

- Production remains BCL-only and cross-platform for Windows, macOS, and Linux.
- Permanent deletion is allowed only for a post-move object whose stable identity, mount, and type match the scan capture.
- Identity mismatch must recover safely or return `Failed` with the exact stranded path.
- Runtime recursive deletion treats symlinks and junctions as leaf entries; malicious same-user races inside runtime internals are outside the v1 threat model.
- Cancellation after mutation records the current candidate as failed/interrupted, preserves prior results, stops later work, and retains the original selected count.

---

### Task 1: Atomic ownership and race-safe recovery

**Files:**
- Modify: `src/DevCleaner/Cleaning/CleanupService.cs`
- Modify: `src/DevCleaner/Cleaning/CleanupModels.cs`
- Modify: `tests/DevCleaner.Tests/Cleaning/CleanupServiceTests.cs`

**Interfaces:**
- Consumes: `ArtifactCandidate.Identity`, `FileSystemIdentityProvider`, existing Git/rule/root revalidation.
- Produces: quarantine-backed `CleanupService.ExecuteAsync`, deterministic `ICleanupMutationObserver` boundaries, and `CleanupResult.SelectedCount`.

- [x] **Step 1: Write failing candidate and ancestor swap tests**

Add real-filesystem tests whose observer replaces the candidate or repository ancestor in `BeforeQuarantineMove`. Assert the outside/replacement object survives, no mismatched object is deleted, and the result is `Failed` with restored or stranded location text.

- [x] **Step 2: Verify RED**

Run:

```text
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Candidate_swap_at_quarantine_boundary|FullyQualifiedName~Ancestor_swap_at_quarantine_boundary"
```

Expected: compile failure for the missing mutation observer/quarantine boundary.

- [x] **Step 3: Implement the minimal quarantine ownership transition**

Introduce these internal operations and keep all permanent deletion behind successful moved-identity verification:

```csharp
internal interface ICleanupMutationObserver
{
    void BeforeQuarantineMove(ArtifactCandidate candidate, string quarantineRoot, string destinationPath);
    void BeforeMovedIdentityCheck(ArtifactCandidate candidate, string quarantineRoot, string destinationPath);
    void BeforeRecursiveDelete(ArtifactCandidate candidate, string quarantineRoot, string destinationPath);
}

// ExecuteAsync flow
// validate -> create GUID quarantine on requested root -> capture quarantine identity
// -> observer -> atomic move -> observer -> verify quarantine and moved identity/type
// -> observer -> reverify ownership -> permanent delete
```

Recovery must identity-check the moved object, require an absent original leaf and a link-free original parent boundary, atomically move back, and verify the restored identity. Otherwise retain the quarantine and report its exact path.

- [x] **Step 4: Run focused tests GREEN**

```text
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Cleaning"
```

Expected: all cleanup tests pass.

### Task 2: Link-as-leaf deletion and cancellation accounting

**Files:**
- Modify: `src/DevCleaner/Cleaning/CleanupService.cs`
- Modify: `src/DevCleaner/Cleaning/CleanupModels.cs`
- Modify: `src/DevCleaner/Output/ReportModels.cs`
- Modify: `tests/DevCleaner.Tests/Cleaning/CleanupServiceTests.cs`
- Modify: `tests/DevCleaner.Tests/Application/CleanCommandTests.cs`

**Interfaces:**
- Consumes: established quarantine ownership from Task 1.
- Produces: child-swap-safe runtime deletion at the approved boundary, explicit interrupted candidate outcome, and selected-count reporting independent of completed items.

- [x] **Step 1: Write failing child-swap and mid-recursion cancellation tests**

Use `BeforeRecursiveDelete` to replace a quarantined child directory with a symlink and assert `Directory.Delete(recursive: true)` removes only the link while the outside target survives. Inject a deletion seam that removes one quarantined child, cancels, and throws `OperationCanceledException`; assert the current candidate is `Failed`, later candidates remain untouched, prior results remain, `IsInterrupted` is true, and `SelectedCount` equals all requested candidates.

- [x] **Step 2: Verify RED**

```text
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Child_swap_before_recursive_delete|FullyQualifiedName~Mid_candidate_cancellation"
```

Expected: behavioral failure because current deletion walks paths itself and cancellation drops the in-progress candidate.

- [x] **Step 3: Implement minimal deletion and accounting changes**

Production filesystem behavior becomes:

```csharp
public void DeleteOwnedObject(string path, bool isDirectory, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    if (isDirectory) Directory.Delete(path, recursive: true);
    else File.Delete(path);
}
```

Catch cancellation after ownership, attempt identity-checked recovery when the quarantined object remains, append a `Failed` interrupted outcome for the current candidate, set `IsInterrupted`, and break. Construct `CleanupResult` with `SelectedCount = request.Candidates.Count`; use that value in `CleanupSummaryReport.SelectedCount`.

- [x] **Step 4: Run final gates**

```text
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Cleaning|FullyQualifiedName~CleanCommand"
dotnet test DevCleaner.slnx
dotnet build DevCleaner.slnx -c Release --no-restore -warnaserror
dotnet publish src/DevCleaner/DevCleaner.csproj -c Release -r osx-arm64 --self-contained -p:PublishAot=true
```

Expected: zero failures, zero warnings, successful Native AOT generation, and a native disposable-repository cleanup smoke test preserving an outside symlink target.

- [x] **Step 5: Commit and append evidence**

Commit the implementation and tests, then append exact RED/GREEN/full/Release/AOT/native-smoke evidence and commit hashes to `.superpowers/sdd/task-5-report.md`.

### Task 3: Final review hardening

**Files:**
- Create: `src/DevCleaner/Cleaning/AtomicFileMover.cs`
- Modify: `src/DevCleaner/Cleaning/CleanupFileSystem.cs`
- Modify: `src/DevCleaner/Cleaning/QuarantineCleanup.cs`
- Modify: `src/DevCleaner/Output/HumanReportWriter.cs`
- Modify: `tests/DevCleaner.Tests/Cleaning/CleanupServiceTests.cs`
- Modify: `tests/DevCleaner.Tests/Output/ReportWriterTests.cs`

**Interfaces:**
- Consumes: stable filesystem identities, the GUID-private quarantine, and existing mutation observers.
- Produces: `IAtomicFileMover.MoveNoCopy` backed by Linux `renameat2`, macOS `renamex_np`, or Windows `MoveFileExW` without `MOVEFILE_COPY_ALLOWED`; failure messages which retain the exact possible payload path; cleanup-failure merging on every early exit; and a human summary containing original selected and processed counts.

- [x] **Step 1: Write failing native-move and post-observer identity tests**

Add injected mover tests proving that an ancestor swap to a different mount/identity fails before the native move is invoked, the outside file remains byte-for-byte intact, and a mover failure leaves the source present while producing `Failed`. Verify RED because the current observer boundary does not recheck identity and `File.Move`/`Directory.Move` provide no explicit no-copy contract.

- [x] **Step 2: Implement one no-copy atomic move primitive**

Replace the file/directory split move with one native primitive. On Linux call `renameat2(..., RENAME_NOREPLACE)`, on macOS call `renamex_np(..., RENAME_EXCL)`, and surface `errno`, including `EXDEV`, without a copy fallback. On Windows call `MoveFileExW(source, destination, MOVEFILE_WRITE_THROUGH)` without `MOVEFILE_COPY_ALLOWED` or replacement flags. Require a nonexistent destination and fail safely if it already exists. Recheck source identity, type, and mount immediately after `BeforeQuarantineMove`; retain the mandatory post-move stable identity check. Use this same primitive for identity-checked recovery.

- [x] **Step 3: Write failing recovery-inspection and quarantine-cleanup tests**

Inject boundary inspection failure after ownership transfer and assert the candidate result is `Failed` and contains the exact `destinationPath`. Inject empty-quarantine removal failures on representative early identity, mount, cancellation, and move-failure branches and assert the final message contains the quarantine path and cleanup error. Verify RED because recovery inspection exceptions currently escape and early branches discard `TryRemoveEmptyQuarantine` failures.

- [x] **Step 4: Make recovery and early-return accounting total**

Catch identity, ACL, and I/O uncertainty inside `RecoverOrStrand` and always return `Failed` with the exact `destinationPath` whenever payload presence cannot be excluded. Route every pre-ownership early result and exception through one helper which attempts empty-quarantine removal and appends the exact quarantine path/error when removal is not confirmed.

- [x] **Step 5: Write and pass the human-summary regression**

Assert an interrupted cleanup with more selected than processed prints both counts. Update `WriteCleanup` to include `SelectedCount` and processed item count while retaining deleted/skipped/failed and estimated-byte totals.

- [x] **Step 6: Run all acceptance gates, append report, and commit**

Run focused cleanup/command tests, `dotnet test DevCleaner.slnx`, the warning-as-error Release build, arm64 Native AOT publish and version invocation, and native disposable-repository cleanup smoke including outside-target preservation and quarantine removal. Append exact RED/GREEN and acceptance evidence to `.superpowers/sdd/task-5-report.md`, then commit all review fixes.
