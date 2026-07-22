# Guarded Quarantine Cleanup Design

## Scope and threat model

DevCleaner v1 protects cleanup against accidental concurrent filesystem changes from normal developer tools. It does not claim protection against a malicious process running as the same user that actively discovers and races a GUID-private quarantine path inside the runtime's recursive deletion implementation.

Cleanup remains permanent and does not use the operating-system trash. A selected artifact must pass current root, repository, link, mount, stable-identity, Git-ignore, tracked-content, and active-rule checks before mutation.

## Ownership transition

Path validation alone does not authorize deletion because an ancestor or candidate can be replaced after validation. For each selected candidate, DevCleaner therefore creates a private unpredictable quarantine directory on the candidate's repository filesystem and atomically moves the candidate to a unique child path within it.

The move and the subsequent identity check form the ownership transition:

1. Capture the expected candidate identity from scanning and revalidate its current identity and type.
2. Create a GUID-named quarantine directory within the repository so the source and destination share a filesystem.
3. Invoke the testable pre-move boundary, then atomically move the source path into quarantine.
4. Re-capture the moved object's stable file, mount, and type identity.
5. Permanently delete only when the moved identity matches the scan identity.

An ancestor or candidate swap before the move can cause a different object to move, but the post-move identity mismatch prevents its deletion. An ancestor swap that still resolves the original object cannot redirect subsequent deletion because deletion uses the independent quarantine path.

## Failure and recovery

If the moved identity does not match, DevCleaner attempts recovery only when all of the following hold:

- the quarantined object still has the post-move identity;
- the original path is absent;
- the original root and repository boundary is link-free and contained;
- the reverse move succeeds atomically.

Otherwise the object is left at its exact quarantine path. The candidate outcome is `Failed` and includes whether recovery succeeded or the stranded path. DevCleaner never deletes an identity-mismatched object.

An empty quarantine directory is removed after each candidate. A non-empty or unremovable quarantine is retained and reported explicitly; it is never hidden as success.

## Recursive deletion and links

After identity ownership is established, production deletion delegates the whole quarantined tree to `Directory.Delete(path, recursive: true)`. The .NET runtime implementations on Unix and Windows classify symbolic links, junctions, and other name-surrogate reparse points as leaf entries and remove the link rather than recursing into its target.

The injected deletion boundary deterministically replaces a child directory with a link immediately before the runtime primitive. Integration tests assert that the outside link target survives and the quarantined tree is removed. This covers the v1 accidental-concurrency boundary. The remaining malicious same-user race inside the runtime implementation is explicitly outside the v1 threat model.

## Cancellation and accounting

Cancellation stops scheduling new candidates. A cancellation raised after a candidate has moved or after recursive deletion has partially mutated it records that candidate as `Failed` with an interrupted message and preserves all earlier results. CleanupResult separately records the original selected count, so JSON and human reports do not undercount candidates that were never scheduled after interruption.

Cancellation before ownership leaves the source untouched where possible. Cancellation after ownership triggers identity-checked recovery when the quarantined object still exists; otherwise the report gives the exact stranded path or notes partial deletion.

## Test seams and acceptance

The internal filesystem seam exposes deterministic callbacks at these boundaries:

- after validation but before the quarantine move;
- after the move but before post-move identity verification;
- after ownership verification but before recursive deletion;
- during the runtime deletion operation for cancellation simulation.

Tests use real temporary Git repositories and real filesystem moves/links. They prove candidate swap, repository-ancestor symlink swap, child-directory-to-symlink swap, identity mismatch recovery/stranding, empty quarantine cleanup, mid-candidate cancellation accounting, preservation of completed outcomes, and stopping later candidates.

Final acceptance requires focused cleanup tests, the full solution, Release warnings-as-errors, Native AOT publish, and a native cleanup smoke test against a disposable Git repository.
