# Portable Audit Traversal Design

## Goal

Replace the audit command's custom native filesystem traversal with one
portable, iterative .NET implementation while preserving the audit feature's
read-only product boundary, Git-derived classification, mount-boundary default,
deterministic output, and cancellation behavior.

The change is a simplification of `repoglean audit`, not a new cleanup
capability. Audit findings remain advisory evidence about ignored but
unclassified storage. They never become cleanup candidates and never authorize
planning or deletion.

## Motivation

The current `SecureAuditFileSystem` implements handle-relative traversal,
stable identity capture, no-follow opens, and post-inspection revalidation with
separate Windows, Linux, Intel macOS, and Apple Silicon macOS native ABIs. That
is cleanup-grade machinery applied to a read-only report. It has accumulated a
large set of `DllImport` declarations, native structures, architecture-specific
constants, explicit handle lifetimes, and unsupported-platform branches.

The audit does not mutate files and its size and timestamp measurements are
already point-in-time estimates. If an entry changes during traversal, the
honest result is a best-effort snapshot with a warning or omission, not a
cleanup-grade identity guarantee. The implementation should reflect that lower
risk and simpler contract.

## Selected approach

Use one pure-.NET directory traversal implementation on every platform and
retain a narrow OS-specific dependency only for mount identity.

The alternatives were rejected as follows:

- Keeping both the native traversal and a portable traversal mode would retain
  nearly all current complexity and double the behavioral and CI matrix.
- Replacing traversal entirely with `git ls-files` would simplify discovery,
  but it would substantially change the existing tree-carving and aggregation
  model and make filesystem error reporting less direct.
- Removing mount checks entirely would make mounted directory trees part of a
  default repository audit, contrary to the current conservative boundary.

The selected hybrid keeps one traversal engine and reuses the existing
`IVolumeBoundary` abstraction from `FileSystemIdentityProvider`. Audit-specific
code contains no native ABI handling.

## Command-line contract

Add one audit-only option:

```text
--cross-mounts
```

Without the option, audit captures the repository root mount identity and
checks each directory before scheduling it for traversal. A directory on a
different mount or volume is pruned with a warning. If the root mount identity
cannot be determined, that repository is omitted with a warning. If a child
mount identity cannot be determined, that child branch is pruned with a
warning.

With `--cross-mounts`, audit performs no root or child mount identity checks.
Mounted and mount-unknown directories are traversed as ordinary directories.
Links, junctions, and reparse points remain boundaries in both modes.

`--cross-mounts` is accepted only by `audit`; every other command rejects it.
It is command-line-only in this increment and does not add a configuration
property. Help text and the README describe both the conservative default and
the explicit opt-in behavior.

`AuditOptions` gains a `CrossMounts` Boolean with a default of `false`. Existing
callers that do not request the option retain the conservative mount-boundary
behavior.

## Portable filesystem boundary

Replace `SecureAuditFileSystem` with a small BCL-backed directory reader. Its
contract returns immutable snapshots and has no resource ownership semantics.
An entry snapshot contains only the information audit consumes:

- name;
- normalized absolute path;
- entry kind: regular file, directory, link, or other;
- logical length for regular files;
- nullable last-write timestamp;
- nullable inspection error.

The production reader uses `Directory.EnumerateFileSystemEntries`,
`File.GetAttributes`, `FileInfo`, and `DirectoryInfo`. It enumerates only the
requested directory; it never enables framework recursion. An entry is treated
as a link boundary when .NET reports `FileAttributes.ReparsePoint` or a
non-null `FileSystemInfo.LinkTarget`.

The reader catches expected path-local filesystem failures such as access
denial, a missing entry, a directory disappearing, or an I/O error and returns
them as inspection failures. It does not catch unexpected programming or
runtime failures.

The replacement has no `IDisposable`, native handles, stable filesystem
identity, reopen operation, post-read identity comparison, native directory
record parsing, architecture switch, audit-specific `DllImport`, or
audit-specific `PlatformNotSupportedException`.

## Unbounded iterative traversal

Traversal uses an explicit stack of work frames. It contains no recursive
method call and does not set `EnumerationOptions.MaxRecursionDepth`.

Each directory is processed in two phases:

1. An enter frame enumerates and sorts its immediate children, applies early
   pruning, requests bounded Git classification, and schedules eligible child
   work.
2. A completion frame receives child aggregates, computes the directory's
   final file count, logical bytes, and newest timestamp, and decides whether
   to collapse the directory into a finding.

Child work is pushed in reverse deterministic order so depth-first processing
produces the existing stable order. The explicit stack grows with pending
work rather than the process call stack. There is no product-configured depth
limit; traversal ends only when the stack is empty, cancellation is requested,
or the filesystem refuses a path.

This post-order frame model preserves the current highest-honest-boundary
behavior. An ignored, unclassified directory can become one finding after its
classified and visible branches have been carved out. Every counted file still
belongs to at most one finding.

## Classification and aggregation flow

For each entered directory:

1. Check cancellation.
2. Enumerate only immediate children through the portable reader.
3. Sort entries with the existing platform-aware comparer.
4. Detect and prune nested repository boundaries.
5. Prune `.git`, command exclusions, and reserved RepoGlean quarantine trees.
6. Carve Git-visible paths before producing link or special-entry warnings, so
   ordinary tracked links remain silent.
7. Prune paths matched by active built-in or custom rules.
8. When `CrossMounts` is false, check a directory's mount identity before
   scheduling it.
9. Ask Git for ignored status and provenance in the existing bounded,
   NUL-delimited batches.
10. Schedule eligible directories and measure eligible regular-file snapshots.
11. Complete directory aggregates in post-order, using existing saturating size
    arithmetic and newest-timestamp rules.
12. Apply the effective minimum size after carving and aggregation.

Git remains the sole authority for ignored status and provenance. RepoGlean
does not parse ignore patterns. Existing negation, external ignore source,
source-line, and malformed-provenance behavior remains unchanged.

Repository and finding ordering remains deterministic. The table and JSON
reports retain their existing ordering and aggregation rules.

## Concurrent changes and errors

Audit explicitly becomes a best-effort filesystem snapshot:

- An entry deleted before inspection is skipped with a warning when the change
  is observed.
- A directory deleted before enumeration is pruned with a warning.
- An access-denied or unreadable branch is pruned with a warning while valid
  siblings continue.
- A file changed after its metadata was read may leave a stale size or
  timestamp in the report; audit does not claim atomicity.
- A rename may cause the old path to be omitted or the new path to appear,
  depending on when enumeration observes it.
- A link or reparse point observed by the reader is never traversed.
- An undetected path-replacement race is not elevated to a cleanup risk because
  audit reads metadata only and grants no mutation authority.

Warnings continue to make the operation status `partial`. RepoGlean reports
only failures it observes; it does not claim to detect every concurrent change.
Unexpected exceptions propagate rather than being relabeled as filesystem
warnings.

Cancellation is checked before and after directory enumeration, during entry
processing, before and after Git calls, and while completing aggregates. A
cancelled repository does not return an incomplete aggregate.

## Compatibility

The audit JSON schema and finding model do not change. Existing fields,
provenance values, ordering, threshold semantics, and summary totals remain
compatible. Human and JSON warning text may change to remove claims about
secure handles or identity revalidation.

The following behavior is intentionally relaxed:

- audit no longer proves that a path retained the same filesystem identity
  across classification and measurement;
- audit no longer holds directory handles as traversal anchors;
- concurrent changes may produce advisory stale measurements rather than
  identity-change omissions.

These changes apply only to `audit`. Scan candidate safety, planning, cleanup,
quarantine, and recovery retain their existing identity and mutation controls.
The shared `FileSystemIdentityProvider` remains available for those destructive
or authority-bearing workflows and for audit's default mount boundary.

## Migration boundary

The implementation removes the audit-specific native traversal rather than
wrapping it:

- delete `ISecureAuditEntry`, `SecureAuditIdentity`, the Unix and Windows secure
  audit entry implementations, unavailable handle entries, native structures,
  and audit-specific interop declarations;
- replace them with a portable directory-reader interface, BCL implementation,
  and immutable entry snapshot;
- rewrite `RepositoryAuditor` traversal as the explicit frame loop;
- retain existing Git batching, path policy, rule classification, aggregation,
  reporting, and progress boundaries where their contracts still apply;
- add `CrossMounts` option plumbing through CLI parsing and `RepoGleanApp`;
- update help and README language from cleanup-grade traversal guarantees to
  best-effort read-only snapshot semantics.

No second traversal engine or compatibility switch remains after migration.

## Testing strategy

Testing combines deterministic component tests with real cross-platform
filesystem acceptance:

- A test directory reader can model a chain thousands of directories deep and
  prove the frame loop completes without recursive calls or a configured depth
  limit.
- Stub `IVolumeBoundary` implementations verify that the default skips foreign
  and unidentified mounts and that `CrossMounts` bypasses mount queries.
- Real temporary-directory tests verify regular files, directories, Unicode,
  whitespace and control characters, links, junctions or reparse points, and
  files disappearing during inspection where the platform permits them.
- Failure injection verifies that one inaccessible or vanished branch does not
  suppress valid siblings and that observed failures produce warnings.
- Cancellation injection verifies prompt cancellation during enumeration, Git
  classification, file processing, and post-order aggregation without a
  partial repository result.
- Existing repository-auditor tests continue to cover visible branches, active
  and disabled rules, Git negation, provenance, malformed Git results, nested
  repositories, quarantine boundaries, exclusions, thresholds, deterministic
  ordering, and saturating arithmetic.
- CLI tests verify `--cross-mounts` parsing for audit, rejection by other
  commands, default `false`, help output, and option propagation.
- End-to-end tests verify unchanged table and JSON contracts plus the new flag.
- Native AOT publish and packaged-executable smoke tests run through the full
  six-platform CI matrix.

Tests tied only to audit's removed native directory layouts, open flags,
stable-identity records, handle lifetime, or reopen behavior are deleted. Tests
for the shared `FileSystemIdentityProvider` and destructive cleanup identity
rules remain.

## Success criteria

The migration is complete when:

- audit has no direct `DllImport` or native structure;
- audit has no architecture-specific traversal behavior or
  `PlatformNotSupportedException`;
- every platform uses the same BCL traversal implementation;
- traversal has no recursive method call and no configured depth limit;
- default audit prunes foreign and unidentified mounts;
- `--cross-mounts` traverses mounted and mount-unknown directories while still
  pruning links and nested repositories;
- existing audit output remains compatible apart from documented best-effort
  concurrent-change warnings;
- destructive workflows retain their current safety controls;
- the full repository verification suite and six-platform Native AOT CI matrix
  pass; and
- the replacement is materially smaller and easier to review than the current
  audit-specific native implementation.

## Out of scope

This change does not:

- modify scan, plan, clean, quarantine, or recovery safety semantics;
- add `crossMounts` to configuration;
- add a traversal depth option;
- follow symbolic links, junctions, or reparse points;
- make audit output atomic or transactionally consistent;
- change Git ignore authority, rule semantics, report schemas, or cleanup
  eligibility; or
- retain the old native audit traversal as an alternate mode.
