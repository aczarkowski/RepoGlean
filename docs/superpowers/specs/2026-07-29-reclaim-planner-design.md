# Reclaim Planner Design

## Goal

Add a deterministic Reclaim Planner that recommends the least disruptive set
of currently eligible RepoGlean artifacts estimated to satisfy a requested
space target.

The feature has two entry points:

```text
repoglean plan [root ...] --free size [options]
repoglean clean [root ...] --free size [options]
```

`plan` is read-only. `clean --free` displays or reports the same recommendation
and passes only its selected candidates into the existing guarded cleanup
pipeline. Planning never grants deletion authority: Git-ignore status, active
rules, repository boundaries, stable identities, mounts, tracked or visible
content, links, quarantine ownership, and descendant snapshots retain their
existing roles.

The requested and reported byte values remain logical-size estimates. They are
not physical disk-allocation measurements or guarantees of reclaimed capacity.

## Scope

The first release provides one built-in, deterministic `balanced` policy. It
does not expose named strategies, configurable category priorities, learned
regeneration costs, package-manager execution, saved-plan replay, or automatic
replacement of candidates that fail during cleanup.

The feature remains within RepoGlean's current product boundary: it considers
only artifacts found by active rules in Git working trees and authorized by
the existing scan contract. It does not become an arbitrary disk cleaner.

## Command-line contract

### `plan`

`plan` requires `--free` with a strictly positive byte size accepted by the
existing byte-size syntax, for example:

```console
repoglean plan ~/src --free 20GiB
repoglean plan ~/src --free 5GB --repo api --category build
repoglean plan ~/src --free 40GiB --all --format json
```

It accepts the discovery, filtering, configuration, reporting, and progress
options that apply to a read-only scan:

- repeatable positional roots;
- `--repo`;
- `--category`;
- `--exclude`;
- `--min-size`;
- `--all-drives`;
- `--format`;
- `--config`;
- `--quiet`;
- `--verbose`;
- `--no-color`;
- `--no-progress`;
- `--all`, solely to opt dependency artifacts into the planning pool.

`--details`, `--dry-run`, and `--yes` are invalid with `plan`. Planned
candidate rows are always part of the human plan because the recommendation is
the command's primary result.

The effective roots, repository and category filters, exclusions, minimum
size, rule catalog, and Git authorization are resolved exactly as they are for
`scan`. A category filter narrows the pool to the requested categories.
Dependency artifacts enter the pool only when the user supplies `--all` or an
explicit `--category dependency`.

### `clean --free`

`--free` is an optional, strictly positive target on `clean`. Without it,
existing cleanup behavior is unchanged.

With it, the Reclaim Planner replaces ordinary default candidate selection:

```console
repoglean clean ~/src --free 20GiB
repoglean clean ~/src --free 20GiB --dry-run
repoglean clean ~/src --free 20GiB --yes --format json
```

Existing filters narrow the planning pool. `--all` and an explicit dependency
category retain their dependency opt-in meaning. `--yes --free size` is an
explicit unattended scope and therefore satisfies the existing requirement
that `--yes` be combined with `--all`, `--repo`, or `--category`.

Interactive `clean --free` does not show the ordinary repository and artifact
selection prompts. It shows the complete recommendation, including any
shortfall, and then requires the existing exact lowercase `delete`
confirmation. Declining confirmation changes nothing and retains the existing
successful user-cancellation behavior.

`clean --free --dry-run` plans, safety-validates, and reports the selected
candidates without confirmation or deletion. JSON cleanup remains
non-interactive by contract and continues to require `--dry-run` or `--yes`.

`--free` is invalid with every command other than `plan` and `clean`.

## Candidate planning facts

The scan result for an artifact candidate gains a nullable
`NewestWriteTimeUtc` planning fact. It is the newest observed last-write time
among the candidate root and its discovered descendants.

The scan captures one UTC reference time for the operation. Filesystem times
later than that reference, unavailable times, and otherwise uncertain times
produce the conservative `recent-or-unknown` recency band. Timestamp
information influences recommendation order only. It cannot make a candidate
eligible, authorize mutation, weaken a safety rejection, or replace cleanup
revalidation.

Existing size traversal should collect the newest write time without a second
tree walk. Failure to collect a timestamp must not convert an otherwise
well-defined candidate into an older band. Existing scan failures that make
the artifact boundary or contents uncertain remain failures; the nullable
timestamp rule is not a fallback for safety-relevant filesystem uncertainty.

## Balanced planning policy

The planner receives the filtered, authorized candidate pool, a positive
target, and the scan's UTC reference time. It orders candidates
lexicographically by the following keys.

### 1. Disruption tier

From least to most disruptive:

1. `test`;
2. `build`;
3. `cache`;
4. `ide`;
5. `dependency`.

Dependency candidates are absent unless explicitly opted in. Custom rules use
their declared standard category and therefore require no new configuration
surface.

### 2. Recency band

Within a disruption tier:

1. `dormant`: newest observed write time is at least 30 days before the scan
   reference time;
2. `stale`: newest observed write time is at least 7 days but less than 30
   days before the reference time;
3. `recent-or-unknown`: newer than 7 days, in the future, or unavailable.

The thresholds are fixed product behavior in the first release rather than
configuration.

### 3. Estimated size

Within a tier and recency band, larger candidates come first. This reaches the
target with fewer candidates without pretending to solve for a globally
minimal overshoot.

### 4. Stable tie-breaker

Equal candidates are ordered by normalized repository path and then normalized
candidate path using the same platform-aware path comparison semantics already
used by RepoGlean. The result must be stable for identical scan inputs.

The planner takes candidates in this order until their saturating cumulative
logical size meets or exceeds the target. All remaining pool candidates are
preserved. If the entire pool is smaller than the target, every candidate in
the pool is recommended and the difference is reported as a shortfall.

The planner is intentionally greedy and explainable. The first release does
not use subset-sum optimization, hidden weights, or a synthetic regeneration
cost.

## Architecture and data flow

### `ReclaimPlanner`

`ReclaimPlanner` is a pure planning service. It performs no filesystem access,
Git calls, console output, configuration loading, or cleanup. Given immutable
candidate facts and a target, it returns an immutable `ReclaimPlan`.

### `ReclaimPlan`

The plan records:

- requested bytes;
- planned bytes using saturating arithmetic;
- overshoot bytes when the target is met;
- shortfall bytes when the target is not met;
- whether the target is met;
- selected candidates in deletion order;
- eligible but preserved candidates;
- each candidate's disruption tier, recency band, estimated bytes, and
  ordering explanation.

Overshoot and shortfall are mutually exclusive and never negative.

### Application orchestration

Both entry points perform discovery and scanning once and call the same
planner:

```text
roots and configuration
  -> repository discovery
  -> authorized candidate scan with planning facts
  -> existing filters and dependency opt-in
  -> ReclaimPlanner
     -> plan: report and stop
     -> clean --free: display/report plan, confirm when interactive,
        then pass the fixed selected list to CleanupService
```

`CleanupService` remains unaware of ranking policy. It receives the selected
candidates and applies its existing revalidation, quarantine, ownership,
recovery, and boundary-aware deletion behavior.

The selected list is fixed before confirmation or unattended mutation begins.
If a candidate is later skipped or fails, RepoGlean does not add another
candidate. Substitution would mutate a plan the user confirmed and could
silently move into a more disruptive tier.

The first release does not serialize a plan for later execution. Safe replay
would require a separate design for expiry, repository and candidate identity
binding, changed Git state, and user-visible drift.

## Human output

Human `plan` output includes:

- effective roots and scan warnings;
- the requested target;
- estimated bytes in the eligible pool;
- a row for every recommended candidate in deletion order;
- category, recency band, estimated size, repository, and relative path;
- the planned total;
- overshoot or shortfall;
- the number and estimated bytes of eligible candidates preserved after the
  target was reached;
- a reminder that values are logical-size estimates.

Representative summary:

```text
Reclaim plan
Target:                    20.0 GiB estimated
Planned:                   21.3 GiB estimated
Overshoot:                  1.3 GiB estimated
Selected artifacts:              14
Eligible artifacts preserved:    27
```

A shortfall is prominent:

```text
Target:                    20.0 GiB estimated
Available and planned:     12.4 GiB estimated
Shortfall:                  7.6 GiB estimated
Target met:                         no
```

Interactive `clean --free` prints the recommendation before confirmation. Its
final cleanup report remains authoritative and distinguishes planned bytes
from estimated bytes whose payload deletion completed.

Progress uses planning terminology. It may report eligible and planned
candidate counts and bytes, but it never describes bytes as reclaimed before
payload deletion completes.

## JSON contract

`plan --format json` emits exactly one document on stdout with
`schemaVersion: 1`, `operation: "plan"`, the existing common report fields,
and a top-level `plan` object. The object contains:

- `requestedBytes`;
- `eligibleBytes`;
- `plannedBytes`;
- `overshootBytes`;
- `shortfallBytes`;
- `targetMet`;
- `selectedCandidateCount`;
- `preservedCandidateCount`;
- `selectedCandidates`;
- `preservedCandidates`.

Candidate entries contain their existing identity and reporting fields plus
`planningOrder` when selected, `disruptionTier`, `recencyBand`,
`newestWriteTimeUtc`, and `planningReason`. Nullable or uncertain timestamps
serialize as `null`.

Adding a new operation and its operation-specific object does not change
existing scan, rules, or cleanup document shapes, so the report schema remains
version 1.

When `clean --free` uses JSON, stdout still contains exactly one cleanup
document. Its `cleanup` object adds an optional reclaim-target section
containing requested, planned, completed-deletion, overshoot or shortfall, and
target-met values. That section is absent for ordinary cleanup, preserving its
existing shape.

Verbose narration remains on stderr. Compact progress remains disabled for
JSON unless the user explicitly requests verbose narration under the existing
renderer-selection contract.

## Status and exit codes

`plan` and `clean --free` use the existing status vocabulary and exit-code
contract:

- exit `0` and `status: "success"` when the target is met and no existing
  warning or partial-result condition applies;
- exit `2` for a missing, zero, negative, malformed, or incompatible
  `--free` value, or invalid configuration;
- exit `3` and `status: "partial"` when a valid plan cannot meet the target,
  or completed cleanup finishes below the target;
- exit `1` for a fatal operational failure;
- exit `130` for interruption.

Existing warnings, safety skips, and per-candidate cleanup failures continue to
produce partial status even if enough other selected payloads were deleted to
meet the target.

For cleanup, completed-deletion bytes include only candidates whose payload
deletion completed under the existing cleanup result contract. A later empty
quarantine cleanup failure may still leave `deletionCompleted: true`; those
bytes count toward the target just as they count toward
`estimatedDeletedBytes`.

A positive target with no eligible candidates is a valid shortfall result and
therefore exits `3`, rather than using the ordinary no-candidates success
behavior.

## Failure and interruption behavior

- Planning or plan-report construction must complete before any cleanup
  mutation or confirmation prompt.
- A planning failure cannot fall back to ordinary cleanup selection.
- Future or unavailable timestamps rank conservatively as
  `recent-or-unknown`.
- Cumulative requested, eligible, planned, completed, overshoot, and shortfall
  arithmetic saturates at the maximum signed 64-bit integer.
- Cleanup revalidates every selected candidate exactly as it does today.
- A changed, unsafe, skipped, or failed candidate is reported but not replaced.
- Interruption reports the fixed originally planned count, processed outcomes,
  completed-deletion bytes, and current shortfall without treating unprocessed
  candidates as deleted.
- Renderer or progress-output failure retains the existing observational
  behavior and cannot alter planning, selection, cleanup, or status.

## Compatibility

Ordinary `scan`, `clean`, `rules`, and `config` behavior is unchanged.
Existing `clean` selection prompts are unchanged when `--free` is absent.
Existing configuration documents require no migration. The first release adds
no new configuration properties.

The implementation remains BCL-only, Native AOT-compatible, cross-platform,
and dependent on Git as the cleanup authority.

## Tests and acceptance

### Planner unit tests

Cover:

- every category-tier ordering;
- dependency exclusion and explicit opt-in;
- 30-day and 7-day boundary values;
- future and unavailable timestamps;
- size ordering within a tier and recency band;
- deterministic normalized-path ties;
- exact target, overshoot, and shortfall;
- a target larger than the whole pool;
- an empty pool;
- saturating arithmetic;
- immutable input and output collections;
- an explanation matching the actual ordering keys.

### Scan-fact tests

Cover candidate-root and descendant timestamps, newest-time aggregation,
files and directories, future times, unavailable times, and collection during
the existing size traversal. Prove timestamp uncertainty never makes a
candidate appear older and never weakens an existing scan rejection.

### CLI and application tests

Cover:

- `plan` requiring a positive `--free`;
- the complete option matrix;
- `--yes --free` satisfying unattended explicit scope;
- dependency opt-in through `--all` and category;
- filter composition;
- interactive plan display and exact confirmation;
- declined confirmation;
- dry-run validation;
- JSON non-interactivity;
- shortfall status and exit code;
- cleanup skips and failures reducing completed bytes without substitution;
- interruption before and during cleanup;
- unchanged ordinary scan and clean behavior.

### Output and progress tests

Cover human met-target and shortfall reports, deterministic row order, JSON
round trips and field names, a single JSON document on stdout, verbose stderr,
logical-size wording, and the distinction between planned and completed bytes.
Progress tests prove it does not claim reclamation before deletion.

### Integration and adversarial regression

Use real temporary Git repositories to prove that ignored status plus an active
rule still defines the planning pool and that tracked, visible, nested,
linked, cross-mount, replaced, or otherwise uncertain content remains
protected.

Re-run the existing adversarial cleanup coverage for ancestor and candidate
replacement, symlink and mount boundaries, Git/index changes, quarantine
identity and recovery races, descendant snapshots, and case collisions. The
planner must not introduce an alternate path around guarded cleanup.

### Final acceptance

Run:

```console
dotnet build RepoGlean.slnx --configuration Release --warnaserror
dotnet test RepoGlean.slnx --configuration Release --no-build
dotnet format RepoGlean.slnx --verify-no-changes --no-restore
git diff --check
```

Extend the packaged Native AOT smoke to exercise JSON `plan`, scoped
`clean --free --dry-run`, and scoped `clean --free --yes`, proving protected
content survives and completed-deletion accounting matches the resulting
filesystem.

## Documentation

Update CLI help and the README with:

- the new command and examples;
- `--free` and `--all` option applicability;
- the balanced ordering policy and fixed recency bands;
- dependency opt-in;
- best-effort shortfall behavior;
- interactive and unattended cleanup behavior;
- JSON plan and cleanup target fields;
- exit code `3` for unmet targets;
- the distinction between logical-size estimates and physical reclaimed
  capacity;
- the absence of saved-plan replay and mid-cleanup substitution.
