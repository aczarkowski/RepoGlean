# Interactive Progress Design

## Goal

Make `scan` and `clean` visibly active without overwhelming the user or
changing RepoGlean's reporting, JSON, exit-code, or cleanup-safety contracts.
Normal terminal use receives one compact updating status line. `--verbose`
instead produces a stable, append-only account of meaningful stages and
outcomes.

`--details` continues to control candidate rows in the final scan report.
`--verbose` narrates the operation and continues to include the additional
final-report diagnostics it exposes today.

## Output contract

Reports and machine-readable data remain on stdout. Progress and verbose
diagnostics use stderr exclusively.

RepoGlean selects one progress renderer for the complete operation:

| Conditions | Renderer |
| --- | --- |
| `--quiet` | No progress |
| `--verbose` without `--quiet` | Append-only verbose milestones |
| Table output with interactive stderr and neither `--quiet`, `--verbose`, nor `--no-progress` | Compact animated status |
| Redirected or captured stderr without `--verbose` | No progress |
| JSON without `--verbose` | No progress |

The interactivity decision depends on stderr, not stdout. Piping stdout while
stderr remains attached to a terminal therefore keeps compact progress visible
without contaminating the pipe.

Flag precedence is explicit:

- `--quiet` wins over `--verbose` and `--no-progress`;
- `--verbose --no-progress` retains verbose milestone diagnostics because
  `--no-progress` disables only automatic interactive animation;
- `--verbose --format json` writes milestones to stderr while stdout contains
  exactly one JSON document;
- `--no-color` does not affect progress because progress uses no colour.

Plain redirected execution is silent on stderr unless an operational warning
or error already belongs there. Explicit `--verbose` is the opt-in mechanism
for a stable redirected execution log. This expansion of `--verbose` is the
only intentional output-compatibility change.

## Compact interactive status

The compact renderer owns one temporary stderr line. It refreshes no more than
eight times per second, animates independently of incoming events, and displays
the most recent immutable progress state. Independent
animation ensures that a long repository scan or deletion still looks active.

Representative scan states are:

```text
⠋ Discovering repositories • 12 found
⠙ Scanning repositories 7/18 • 23 candidates • 1.4 GiB estimated
```

Discovery does not display a percentage or directory counter. Its total work
is unknowable without traversing the tree twice, and individual directory
events would add unnecessary overhead and noise. Once discovery completes,
repository scanning uses a real current/total count. Candidate count, estimated
bytes, and warning count are monotonic.

The current repository name may be included when space permits. Optional paths
are shortened to the available terminal width. If width cannot be determined,
the renderer omits the optional path rather than failing or wrapping
aggressively.

Cleanup reuses the discovery and repository-scan states. The status line is
cleared before repository selection, artifact selection, and confirmation
prompts. After confirmation, permanent cleanup displays:

```text
⠋ Cleaning artifacts 3/8 • 2 deleted • 620 MiB estimated
```

A dry run uses non-destructive terminology:

```text
⠋ Validating artifacts 3/8 • 2 validated • 620 MiB estimated
```

The temporary line is cleared before a final report, prompt, fatal error, or
cancellation message. Compact mode leaves no separate progress transcript;
the existing final report is the durable completion record.

## Verbose milestones

Verbose mode replaces animation with plain newline-terminated stderr lines.
It emits:

- one discovery-start line;
- one discovery-complete line with the repository count;
- one line when each repository starts scanning;
- one result line for a repository that produces candidates;
- one warning line when a warning is recorded;
- for cleanup, one validation-start line and one factual outcome line for each
  selected artifact;
- one completion or interruption line with aggregate counts.

Representative output is:

```text
Discovering repositories under /Users/me...
Found 18 repositories.
Scanning [7/18] /Users/me/src/my-api...
Found 3 candidates in my-api (428 MiB estimated).
Validating [2/6] my-api/obj...
Deleted my-api/obj (126 MiB estimated).
```

Verbose mode does not report individual directories or files visited, repeat
unchanged counters, add timestamps, print internal Git commands, or expose
quarantine implementation details. The final human or JSON report remains the
authoritative result even when some information also appeared as live
diagnostics.

## Architecture and data flow

Repository discovery, repository scanning, and cleanup emit structured
progress events rather than writing to a console. Events cover operation and
stage starts, repository discovery, repository scan start and result,
candidate validation, candidate outcome, warnings, completion, and
interruption.

An application-owned progress controller consumes those events and selects one
of three renderers:

- an interactive renderer with a bounded refresh loop;
- a synchronous verbose renderer;
- a no-op renderer.

The services depend only on the progress-event abstraction. They do not depend
on `TextWriter`, terminal capabilities, flag parsing, formatting, or renderer
lifecycle. The controller is created and disposed around the complete scan or
clean command, and it can be cleared and resumed around interactive cleanup
prompts.

Progress is observational. Event publication cannot authorize a mutation,
alter candidate selection, change operation ordering, or change a result.
Renderer write, width-detection, or refresh failures disable that renderer and
do not fail or cancel the underlying operation. The application still handles
authoritative report and error writes through its existing paths.

The implementation remains BCL-only and compatible with Native AOT. It does
not introduce a terminal UI dependency.

## Accuracy, failures, and cancellation

Progress never anticipates an outcome:

- repository, candidate, and byte counts update only after their scan results
  are known;
- `deleted` and deleted-byte totals update only after cleanup records a
  successful deletion;
- dry-run success increments `validated`, never `deleted`;
- skips and failures retain their recorded cleanup outcomes;
- warning counts increase only when warnings are recorded;
- interruption reports completed and originally selected counts without
  treating unscheduled candidates as processed.

An interactive fatal error or cancellation first clears the temporary line.
Verbose mode emits a factual failure or interruption milestone before the
existing application-level diagnostic or report. Progress does not add stack
traces or replace existing error messages.

## Documentation

CLI help and the README describe the distinction directly:

- `--details`: include candidate rows in the final scan report;
- `--verbose`: narrate meaningful operation stages and include detailed final
  diagnostics;
- `--no-progress`: disable automatic interactive animation;
- `--quiet`: suppress progress, verbose narration, and detailed report
  sections while retaining the summary and genuine errors.

Examples cover an interactive scan, verbose redirected diagnostics, piped
stdout with terminal progress, and JSON with verbose stderr.

## Tests and acceptance

Tests cover:

- every renderer-selection and flag-precedence combination in the output
  contract;
- piped stdout with interactive stderr;
- redirected stderr with and without `--verbose`;
- JSON remaining exactly parseable with and without verbose diagnostics;
- deterministic compact rendering through controllable refresh timing and
  terminal width;
- refresh throttling and clean renderer shutdown;
- clearing before prompts, final reports, failures, and cancellation;
- absence of terminal control sequences in verbose and redirected output;
- monotonic repository, candidate, artifact, warning, and byte counters;
- omission of misleading discovery percentages;
- dry-run terminology and accounting;
- safety skips, partial failures, and interrupted cleanup accounting;
- progress-rendering failures leaving scan and cleanup results unchanged;
- real temporary Git repository integration for scan and clean event
  sequences.

Acceptance requires the focused CLI, application, scan, cleanup, and output
tests; the full warning-as-error Release build and test suite; formatting and
diff checks; and the repository's Native AOT scan and scoped-clean smoke tests.
