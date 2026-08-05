# Unclassified Storage Audit Design

## Goal

Add a read-only audit that reports significant Git-ignored storage inside
discovered working trees when no active RepoGlean rule classifies that
storage.

The feature has one entry point:

```text
repoglean audit [root ...] [options]
```

The audit addresses a deliberate visibility gap in the existing product.
`scan` reports only artifacts that an active rule classifies and the existing
Git and filesystem checks authorize as cleanup candidates. Unknown ignored
content remains ineligible for cleanup and is currently absent from reports.
`audit` makes that content visible without expanding cleanup authority.

An audit finding is evidence for human review, not a claim that content is
regenerable or safe to delete. Findings never become cleanup candidates and
cannot flow into planning or cleanup. RepoGlean does not generate, install, or
enable rules from audit results.

## Scope

The first release reports only ignored-but-unclassified storage. It does not:

- diagnose why a path matched by an active rule failed candidate safety
  checks;
- report ordinary tracked, visible, or untracked content;
- suggest or generate custom rules;
- modify `.gitignore`, RepoGlean configuration, or repository content;
- inspect package-manager or other user-level caches outside Git working
  trees;
- add cleanup, planning, confirmation, or unattended-mutation behavior;
- measure physical allocation or guarantee reclaimable capacity.

A future cleanup-coverage doctor may explain rejected recognized artifacts,
but that is a separate data model and command contract.

## Command-line contract

`audit` accepts the same root and repository discovery inputs as `scan`:

- repeatable positional roots;
- repeatable `--repo name-or-path` filters;
- repeatable `--exclude path-or-glob` exclusions;
- `--all-drives`;
- `--config path`.

It also accepts the existing reporting and progress controls:

- `--format table|json`;
- `--quiet`;
- `--verbose`;
- `--no-color`;
- `--no-progress`.

`--min-size size` controls the minimum aggregated logical size of a reported
finding. When omitted, `audit` uses `100 MiB` (104,857,600 bytes). Positive
values use the existing byte-size syntax. `audit` alone also accepts the exact
value `0`, meaning no minimum. Existing commands retain their strictly
positive `--min-size` validation.

The following options are invalid with `audit`:

- `--category`, because findings have no artifact category;
- `--details`, because findings are always the command's primary detailed
  result;
- `--free` and `--all`, because audit does not plan cleanup;
- `--dry-run` and `--yes`, because audit has no mutating mode.

With no command-line roots, configured roots and then the existing user-home
default retain their current precedence. Configuration affects audit through
roots, exclusions, disabled built-in rules, and custom rules. An otherwise
matching disabled built-in rule is not active and therefore does not classify
storage for audit purposes.

## Finding definition

An audit finding is a non-overlapping filesystem tree whose reported root:

1. is inside a discovered Git working tree and an effective requested root;
2. is ignored according to Git's effective ignore configuration;
3. does not match any active built-in or custom RepoGlean rule;
4. is not excluded or inside RepoGlean's reserved quarantine namespace;
5. is not a filesystem link, nested repository, or mount crossing;
6. contains at least one countable ignored, unclassified file; and
7. meets the effective minimum size after classified and visible branches are
   removed from its totals.

Every counted file belongs to exactly one finding. No counted byte overlaps a
normal scan candidate or another audit finding.

### Highest honest finding boundary

The auditor collapses a wholly ignored, unclassified tree into its highest
self-contained ignored root. It does not emit every ignored descendant.

When an ignored parent contains a branch matched by an active rule, that
classified branch is pruned from the audit finding. When it contains tracked
or otherwise Git-visible content, that visible content is not counted. The
auditor may retain the ignored parent as the finding root only while its totals
and file count describe the remaining ignored, unclassified content exactly.
If it cannot do so without ambiguity, it reports the highest non-overlapping
ignored child trees instead.

Thresholding happens only after this carving and aggregation. For example, an
ignored 150 MiB tree containing a 90 MiB active-rule artifact has 60 MiB of
unclassified storage and is omitted under the default threshold.

### Classification order

Each encountered path is assigned to the first applicable class:

1. excluded or reserved quarantine: prune;
2. filesystem link, reparse point, mount crossing, or nested repository: do
   not traverse;
3. matched by an active rule: classified, so prune the complete artifact
   subtree from audit totals;
4. tracked or otherwise Git-visible: not audit material, but continue through
   directories to find ignored descendants;
5. Git-ignored and unmatched by every active rule: unclassified audit
   material;
6. neither ignored nor classified: continue through directories to find
   ignored descendants.

This order makes active rule classification independent of cleanup
eligibility. A recognized artifact rejected by scan remains outside audit;
explaining such rejection belongs to the deferred cleanup-coverage doctor.

## Git ignore authority and provenance

Git is the sole authority for ignore status and ignore provenance. RepoGlean
does not parse `.gitignore` syntax or attempt to reproduce Git's precedence
rules.

Classification requests use bounded, null-delimited Git input and output so
spaces, tabs, newlines, Unicode, and platform path characters do not corrupt
record boundaries. The implementation may batch requests, but splitting or
retrying a batch must not change classification results.

For each finding, RepoGlean captures the verbose Git ignore match for the
finding root:

- ignore source as supplied by Git;
- nullable one-based source line number when Git supplies one;
- the matching ignore pattern.

Repository-contained ignore sources are displayed repository-relative in the
human report. External sources, including configured global ignore files, are
displayed as normalized absolute paths. JSON preserves these values in
separate fields. If Git establishes ignored status but cannot supply an
individual provenance component, that component is `null`; RepoGlean does not
invent it.

## Filesystem traversal and measurement

`RepositoryAuditor` is separate from `RepositoryScanner`. It may share narrow
path, exclusion, batching, and formatting helpers, but it does not return or
construct `ArtifactCandidate` instances.

A dedicated read-only analyzer calculates:

- logical estimated bytes using the existing saturating size arithmetic;
- regular-file count;
- newest observed last-write time across the finding root and counted
  descendants, when available.

The audit does not require cleanup-grade stable identity or Linux birth-time
support because it grants no mutation authority. It never follows symbolic
links, junctions, reparse points, or other name-surrogate links and never
crosses the repository's starting mount. Link targets are not counted.
Nested repositories are boundaries and are considered only through normal
repository discovery, never as descendants of a parent finding.

Filesystem and Git state can change during a read-only audit. Measurements are
therefore advisory snapshots. RepoGlean reports only bytes and files it
actually observed; it does not infer sizes for inaccessible, vanished, or
uncertain branches.

The traversal should classify and measure each included filesystem entry once
per audit. It must not first measure an entire ignored tree and then repeat the
walk to carve recognized or visible branches. Git classification remains
bounded in batches consistent with the repository's existing process and
memory constraints.

## Data model

An immutable audit finding records:

- repository root;
- absolute path;
- repository-relative path;
- file count;
- estimated logical bytes;
- nullable newest write time in UTC;
- nullable ignore source;
- nullable ignore source line;
- nullable ignore pattern.

Repository audit results group findings and warnings by repository. The
operation result records effective roots, repository results, aggregate file
count and estimated bytes, and warnings.

The result includes one repository entry for every selected repository that
completed audit classification, including repositories with zero findings
after thresholding. `repositoryCount` is the number of those successfully
audited repositories. A selected repository omitted because its Git queries
failed contributes a warning but not a repository entry; failure in one
repository does not discard successful results from others.

Audit JSON uses a dedicated document contract rather than populating cleanup
candidate models. Its stable top-level fields are:

- `schemaVersion`, initially `1` for the audit document contract;
- `operation`, always `"audit"`;
- `status`;
- `effectiveRoots`;
- `repositories`;
- `totals`;
- `warnings`;
- `errors`.

Each audit repository has `root`, `findings`, `fileCount`, and
`estimatedBytes`. Audit totals have `repositoryCount`, `findingCount`,
`fileCount`, and `estimatedBytes`. Existing scan, plan, clean, rules, and
configuration JSON shapes and their schema version remain unchanged.

JSON field names use the existing camel-case convention. Nullable provenance
and timestamp fields are present as JSON `null` when unavailable so automation
can distinguish missing evidence from a producer schema that omitted the
field.

## Ordering and human output

Repositories are ordered by descending unclassified estimated bytes and then
stable normalized repository path. Within a repository, findings are ordered
by descending estimated bytes and then stable normalized relative path. The
same deterministic ordering is used in table and JSON output.

The human report begins with:

- repository count;
- finding count;
- total estimated unclassified bytes;
- effective minimum finding size.

It then groups findings by repository and shows size, file count, relative
path, and ignore provenance. Audit language consistently says
`unclassified`, not `safe`, `eligible`, `reclaimable`, or `deletable`.

An illustrative report is:

```text
Audit summary
Repositories: 12
Unclassified findings: 3
Estimated unclassified storage: 28.5 GiB
Minimum finding size: 100 MiB

billing-service
  18.0 GiB  42,180 files  .local-emulator
             ignored by .gitignore:42  /.local-emulator/
```

No findings is a successful result. The summary still reports zero findings
and zero estimated bytes.

## Progress and stream separation

Audit uses the existing operation-wide progress selection contract:

- human and JSON reports use stdout;
- interactive status and verbose narration use stderr;
- `--quiet` retains only the summary and genuine errors;
- `--verbose` emits append-only repository discovery, audit start/completion,
  and warning milestones;
- automatic interactive progress is restrained to one compact status line;
- JSON without `--verbose` emits no progress;
- `--no-progress` disables automatic animation but not explicit verbose
  milestones.

Progress events carry audit finding counts and estimated unclassified bytes.
Auditor and analyzer services never write directly to console streams.
Renderer failure cannot alter classification, reporting status, or exit code.

## Warnings, cancellation, and exit codes

Audit fails closed for classification and open-file uncertainty: affected
paths are omitted rather than guessed.

- An inaccessible entry or enumeration failure omits the affected branch and
  emits an exact-path warning.
- Failed Git classification omits the affected path or batch. A failed batch
  may be divided to isolate individual failures without reclassifying
  successful paths.
- A filesystem link or mount boundary is never followed. A link encountered
  at a potential finding boundary emits a warning; ordinary links within an
  otherwise counted tree are omitted with an exact-path warning.
- A disappearing or observably changing path is omitted or measured only to
  the last coherent boundary and emits a warning.
- Cancellation stops discovery, classification, and traversal promptly.

Exit behavior is:

- `0`: completed audit, including no findings;
- `1`: fatal operation-wide failure such as unavailable Git or inability to
  begin repository discovery;
- `2`: invalid invocation or configuration;
- `3`: completed with one or more recoverable warnings;
- `130`: interrupted.

When possible, cancellation emits an audit document with
`status: "interrupted"`. Recoverable warnings produce `status: "partial"`.
Fatal errors produce `status: "failed"` when a report can be written.

## Architecture and data flow

The application orchestration is:

```text
CLI + configuration
  -> effective roots and exclusions
  -> existing repository discovery
  -> active rule catalog per operation
  -> RepositoryAuditor
       -> Git-backed ignored-path classification and provenance
       -> non-overlapping unclassified tree aggregation
       -> read-only size, file-count, and timestamp measurement
       -> effective minimum threshold
  -> AuditReportDocument
  -> human or JSON writer
```

The key isolation boundary is structural: no audit model is accepted by
`ReclaimPlanner`, `CleanupService`, or quarantine cleanup. Adding an audit
finding to custom configuration in a later, explicit user action causes a
future scan to evaluate it from scratch under the normal active-rule and
cleanup-authority contract. An audit result itself conveys no authority.

## Verification strategy

### CLI and application tests

Tests cover:

- parsing `audit` with every allowed option;
- rejecting category, details, planning, and cleanup options;
- the `100 MiB` default, positive overrides, and audit-only zero;
- configured-root and default-root precedence;
- repository filters and combined exclusions;
- table, quiet, verbose, no-progress, and JSON stream separation;
- no-findings success, partial warnings, fatal errors, and cancellation;
- stable human and JSON ordering;
- the dedicated audit JSON schema and nullable evidence fields.

### Real-Git classification tests

Real repositories prove that audit:

- collapses a wholly ignored tree into one finding;
- keeps sibling findings non-overlapping;
- carves out active built-in and custom-rule artifact subtrees;
- excludes tracked and otherwise visible content within an ignored parent;
- treats a disabled built-in match as unclassified;
- respects root, repository, and configuration exclusions;
- obtains exact provenance from root and nested `.gitignore` files,
  `.git/info/exclude`, and a configured global ignore file;
- handles negated and overridden ignore patterns through Git's result rather
  than local parsing;
- preserves spaces, tabs, newlines, Unicode, and platform-valid unusual path
  characters through null-delimited classification;
- treats nested repositories as boundaries.

### Filesystem and aggregation tests

Focused fixtures prove that audit:

- does not follow symlinks, junctions, reparse points, or mount crossings;
- excludes link targets from counts and sizes;
- applies the threshold after classified and visible branches are carved out;
- counts every included file and byte exactly once;
- uses saturating arithmetic;
- records the newest observed timestamp without making it an eligibility fact;
- reports inaccessible, vanished, and changed branches conservatively;
- remains usable on Linux when cleanup-grade birth time is unavailable.

### Built-executable acceptance

A published executable audits a real mixed repository containing a known
artifact, an ignored unclassified tree, visible content, unusual names, and a
below-threshold tree. The test snapshots repository paths and content before
execution and proves they are byte-for-byte unchanged afterward. It validates
both human and JSON reports, exact stream separation, exit status, provenance,
non-overlapping totals, and deterministic ordering.

The existing full suite, warning-as-error build, schema checks, and Native AOT
smokes remain required acceptance gates.
