# Guarded Quarantine Cleanup Design

## Scope and threat model

DevCleaner v1 protects cleanup against accidental concurrent filesystem changes from normal developer tools, including changes made through retained directory handles at the guarded seams. It does not claim protection against a malicious same-user process that continuously discovers the GUID-private quarantine and races individual no-follow checks inside boundary-aware deletion.

Cleanup remains permanent and does not use the operating-system trash. Scanning captures stable identities for both the repository root and the candidate. A selected artifact must pass current root, repository, link, mount, stable-identity, Git-ignore, tracked-content, and active-rule checks before mutation and at every later ownership boundary.

## Ownership transition

Path validation alone does not authorize deletion because an ancestor or candidate can be replaced after validation. For each selected candidate, DevCleaner therefore creates a private unpredictable quarantine directory on the candidate's repository filesystem and atomically moves the candidate to a unique child path within it.

The move and the subsequent identity check form the ownership transition:

1. Capture the expected candidate identity from scanning and revalidate its current identity and type.
2. Create a GUID-named quarantine directory within the repository so the source and destination share a filesystem.
3. Invoke the testable pre-move boundary, then re-capture the source stable identity, type, and mount. A mismatch fails before the move primitive is invoked.
4. Atomically move the source path into quarantine with one no-copy native primitive: Linux `renameat2` with `RENAME_NOREPLACE`, macOS `renamex_np` with `RENAME_EXCL`, or Windows `MoveFileExW` without `MOVEFILE_COPY_ALLOWED` or replacement flags. Cross-volume and existing-destination moves fail rather than falling back to copy/delete or overwrite.
5. Re-capture the moved object's stable file, mount, and type identity.
6. Permanently delete only when the moved identity matches the scan identity.

Repository identity and exact Git authority are revalidated after the pre-move callback, after ownership against the now-absent original repository-relative path, after the final deletion callback, and immediately before permanent mutation. Absent-path ignore evaluation bypasses index suppression, while independent index queries still reject tracked or staged content at both the original path and current quarantine. Root-level `.devcleaner-quarantine-*` directories are reserved: Git marker-visible listings exclude that namespace, and scanners prune it with an exact-path warning so owned or stranded payloads cannot reactivate rules or become later cleanup candidates.

An ancestor or candidate swap before the move can cause a different object to move, but the post-move identity mismatch prevents its deletion. An ancestor swap that still resolves the original object cannot redirect subsequent deletion because deletion uses the independent quarantine path.

## Failure and recovery

If the moved identity does not match, DevCleaner attempts recovery only when all of the following hold:

- the quarantined object still has the expected validated payload identity, type, and mount;
- the original path is absent;
- the original root and repository boundary is link-free and contained;
- the reverse move succeeds atomically.

Otherwise the object is left at its exact quarantine path. The candidate outcome is `Failed` and includes whether recovery succeeded or the stranded path. Inspection, ACL, and I/O uncertainty inside recovery is contained as `Failed` and includes the exact payload path whenever payload presence cannot be excluded. Recovery uses the same no-copy native primitive as quarantine ownership. DevCleaner never deletes an identity-mismatched object.

An empty quarantine directory is removed after each candidate. Identity, mount, cancellation, and atomic-move failures before ownership all merge any quarantine-removal failure and exact retained path into their result. A non-empty or unremovable quarantine is retained and reported explicitly; it is never hidden as success.

## Descendant ownership and boundary-aware deletion

After identity ownership is established, DevCleaner captures an exact descendant snapshot containing each relative path, stable identity, entry type, and mount. It captures the tree again after the final mutation callback and requires exact equality before deletion, detecting inserted, replaced, and removed descendants.

Production deletion uses a focused no-follow, no-cross-mount walker rather than the runtime recursive-delete primitive. It revalidates every entry against the owned snapshot, refuses to enter links, reparse points, or a directory on another mount, deletes links only as leaves, and rechecks each directory identity and mount before removing it. An uncertain boundary stops with an explicit partial failure. Deterministic link, child-mount, and descendant-swap tests prove outside content survives. A malicious same-user process continuously racing individual no-follow checks remains outside the v1 threat model.

## Cancellation and accounting

Cancellation stops scheduling new candidates. A cancellation raised after a candidate has moved or after boundary-aware deletion has partially mutated it records that candidate as `Failed` with an interrupted message and preserves all earlier results. CleanupResult separately records the original selected count, so JSON and human reports print original selected and actually processed counts without undercounting candidates that were never scheduled after interruption.

Cancellation before ownership leaves the source untouched where possible. Cancellation after ownership triggers identity-checked recovery when the exact owned payload still exists; otherwise the report gives the exact stranded path or notes partial deletion. `DeletionCompleted` is tracked independently of the overall outcome so an empty-quarantine cleanup failure cannot erase irreversible deleted-count and byte accounting.

## Test seams and acceptance

The internal filesystem seam exposes deterministic callbacks at these boundaries:

- after validation but before the quarantine move;
- after the move but before post-move identity verification;
- after ownership verification but before boundary-aware deletion;
- during boundary-aware deletion for cancellation simulation;
- immediately before an identity-checked reverse recovery move.

Tests use real temporary Git repositories and real filesystem moves/links. They prove candidate and repository-root swaps, `git add -f`, staged absent paths, ignore and marker changes at mutation seams, native no-overwrite and injected move failure, child insertion/replacement/removal, link-as-leaf behavior, injected child-mount refusal, expected-payload recovery/stranding, recovery inspection uncertainty, repository-local quarantine placement, empty-quarantine failure merging and deletion accounting, mid-candidate cancellation accounting, selected-versus-processed human reporting, preservation of completed outcomes, and stopping later candidates.

Final acceptance requires focused cleanup tests, the full solution, Release warnings-as-errors, Native AOT publish, and a native cleanup smoke test against a disposable Git repository.
