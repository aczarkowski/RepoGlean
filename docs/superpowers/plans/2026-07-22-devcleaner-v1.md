# DevCleaner v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a stateless, cross-platform `devcleaner` CLI that finds Git working trees, reports ignored regenerable artifacts, and deletes only revalidated selections.

**Architecture:** A dependency-free .NET 10 console application separates CLI parsing, configuration/rules, Git-backed discovery and scanning, presentation, and guarded cleanup. Git is the source of truth for working-tree and ignore state; filesystem deletion is behind a revalidation service and every public workflow is exercised through `DevCleanerApp` with real temporary Git repositories.

**Tech Stack:** C# 14, .NET 10, `System.Text.Json` source generation, xUnit, Git CLI, GitHub Actions, Native AOT.

## Global Constraints

- Target `net10.0`; production code uses only the .NET BCL and remains Native AOT/trimming compatible.
- Require Git on `PATH`; never implement fallback ignore parsing.
- A candidate must be both Git-ignored and matched by an active built-in or custom rule.
- Never delete tracked content, Git metadata, nested repositories, directory symlinks/junctions, paths outside the working tree, or paths outside requested roots.
- Dependency artifacts are visible but not preselected; unknown ignored files are never eligible.
- Reports use stdout, progress uses stderr, JSON output is versioned, and logical sizes are estimates.
- Exit codes are `0` success/canceled/no candidates, `1` fatal operation, `2` invalid invocation/configuration, `3` partial completion, and `130` interruption.
- Follow red-green-refactor for each behavior and commit each completed task.

---

### Task 1: Solution, command model, and primitive parsing

**Files:**
- Create: `DevCleaner.slnx`
- Create: `src/DevCleaner/DevCleaner.csproj`
- Create: `src/DevCleaner/Cli/CliOptions.cs`
- Create: `src/DevCleaner/Cli/CliParser.cs`
- Create: `src/DevCleaner/Cli/ByteSizeParser.cs`
- Create: `src/DevCleaner/Properties/AssemblyInfo.cs`
- Create: `tests/DevCleaner.Tests/DevCleaner.Tests.csproj`
- Create: `tests/DevCleaner.Tests/Cli/CliParserTests.cs`
- Create: `tests/DevCleaner.Tests/Cli/ByteSizeParserTests.cs`

**Interfaces:**
- Produces: `CommandKind`, `OutputFormat`, immutable `CliOptions`, `ParseResult<CliOptions>`, `CliParser.Parse(string[])`, and `ByteSizeParser.TryParse(string, out long)`.
- `CliOptions` exposes command/subcommand, roots, repeated repo/category/exclusion filters, minimum bytes, all-drives/details/dry-run/yes/all flags, output and console flags, optional config path, help, and version.

- [ ] **Step 1: Scaffold the solution and test project without production behavior**

Run `dotnet new sln --format slnx -n DevCleaner`, `dotnet new console -n DevCleaner -o src/DevCleaner -f net10.0 --use-program-main`, `dotnet new xunit -n DevCleaner.Tests -o tests/DevCleaner.Tests -f net10.0`, add both projects to the solution, and add the project reference. Configure the app with `PublishAot`, `InvariantGlobalization`, nullable, implicit usings, deterministic builds, and warnings as errors.

- [ ] **Step 2: Write failing parser tests**

Cover `scan` and `clean` roots/options, `rules list`, every `config` subcommand, global flags before or after the command, repeated filters, unknown options, missing values, invalid categories, `clean --yes` scope validation, and JSON cleanup validation. Assert `--yes` is valid only with `--all`, `--repo`, or `--category`, and `--format json clean` is valid only with `--yes` or `--dry-run`.

- [ ] **Step 3: Run parser tests and verify RED**

Run `dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter FullyQualifiedName~Cli --no-restore`; expect compilation failure because the CLI types do not exist.

- [ ] **Step 4: Implement the immutable option model and hand-written parser**

Use exact enums `CommandKind { Scan, Clean, RulesList, ConfigPath, ConfigShow, ConfigValidate, Help, Version }`, `OutputFormat { Table, Json }`, and `ArtifactCategory { Build, Cache, Test, Ide, Dependency }`. Parse options without reflection or a third-party command library. Return usage errors instead of throwing for user input.

- [ ] **Step 5: Implement byte-size parsing**

Accept positive integer or decimal values followed by optional `B`, decimal `KB|MB|GB|TB`, or binary `KiB|MiB|GiB|TiB`, case-insensitively. Reject negative, non-finite, fractional-byte, overflow, and unknown-unit input.

- [ ] **Step 6: Run tests and commit**

Run `dotnet test DevCleaner.slnx`; expect all parser and size tests to pass. Commit as `feat: establish CLI command model`.

### Task 2: Configuration, glob matching, and artifact catalog

**Files:**
- Create: `src/DevCleaner/Configuration/DevCleanerConfig.cs`
- Create: `src/DevCleaner/Configuration/ConfigLoader.cs`
- Create: `src/DevCleaner/Configuration/DevCleanerJsonContext.cs`
- Create: `src/DevCleaner/Rules/ArtifactRule.cs`
- Create: `src/DevCleaner/Rules/GlobMatcher.cs`
- Create: `src/DevCleaner/Rules/BuiltInRules.cs`
- Create: `src/DevCleaner/Rules/RuleCatalog.cs`
- Create: `tests/DevCleaner.Tests/Configuration/ConfigLoaderTests.cs`
- Create: `tests/DevCleaner.Tests/Rules/GlobMatcherTests.cs`
- Create: `tests/DevCleaner.Tests/Rules/RuleCatalogTests.cs`

**Interfaces:**
- Produces: `ConfigLoader.GetDefaultPath()`, `ConfigLoader.Load(string?)`, `ConfigLoadResult`, `ArtifactRule`, `RuleCatalog.Create(DevCleanerConfig)`, and `GlobMatcher.IsMatch(string pattern, string repositoryRelativePath)`.
- Configuration schema: `schemaVersion`, `roots`, `excludes`, `disabledRules`, and `customRules`; custom rules contain `id`, standard `category`, `patterns`, optional `markers`, and `preselected=false`.

- [ ] **Step 1: Write failing configuration and rule tests**

Cover comments/trailing commas, missing-file defaults, platform config path, schema version 1, invalid categories/patterns/IDs, duplicate IDs, disabled built-ins, exclusion precedence inputs, forward-slash path normalization, `*`, `?`, and `**` glob semantics, marker activation, and custom rules defaulting to unselected.

- [ ] **Step 2: Run focused tests and verify RED**

Run `dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~Rules"`; expect compilation failure for missing configuration/rule types.

- [ ] **Step 3: Implement source-generated JSON configuration loading and validation**

Use `JsonSerializerOptions` with comments skipped, trailing commas allowed, case-insensitive properties, and `JsonStringEnumConverter<ArtifactCategory>`. Missing configuration yields schema version 1 with empty collections. Invalid JSON, schema, duplicate rule IDs, empty patterns, invalid categories, and collisions with built-in IDs produce explicit validation errors before scanning.

- [ ] **Step 4: Implement glob matching and the exact built-in catalog**

Normalize separators to `/`; anchor all patterns to the repository root; translate `**` to zero-or-more path segments, `*` to non-separator characters, and `?` to one non-separator character. Add stable rule IDs for the agreed .NET, Node/web, JVM, Rust, Python, Go, C/C++, and Apple candidates. Generic `bin`, `build`, `dist`, `out`, and `target` rules require the corresponding project marker.

- [ ] **Step 5: Run tests and commit**

Run `dotnet test DevCleaner.slnx`; expect all tests to pass. Commit as `feat: add cleanup configuration and rules`.

### Task 3: Git authority, repository discovery, and candidate analysis

**Files:**
- Create: `src/DevCleaner/Git/ProcessRunner.cs`
- Create: `src/DevCleaner/Git/GitClient.cs`
- Create: `src/DevCleaner/Scanning/ScanModels.cs`
- Create: `src/DevCleaner/Scanning/RepositoryDiscovery.cs`
- Create: `src/DevCleaner/Scanning/RepositoryScanner.cs`
- Create: `src/DevCleaner/Scanning/FileTreeAnalyzer.cs`
- Create: `tests/DevCleaner.Tests/Support/TemporaryDirectory.cs`
- Create: `tests/DevCleaner.Tests/Support/GitTestRepository.cs`
- Create: `tests/DevCleaner.Tests/Scanning/RepositoryDiscoveryTests.cs`
- Create: `tests/DevCleaner.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:**
- Produces: `GitClient.GetVersionAsync`, `IsWorkingTreeAsync`, `ListVisibleFilesAsync`, `IsIgnoredAsync`; `RepositoryDiscovery.DiscoverAsync`; `RepositoryScanner.ScanAsync`; records `ArtifactCandidate`, `RepositoryScanResult`, `ScanResult`, and `OperationWarning`.
- `ArtifactCandidate` contains repository root, absolute and relative path, rule ID, category, preselection, file count, estimated bytes, and filesystem identity captured for revalidation.

- [ ] **Step 1: Write failing real-Git integration tests**

Create temporary repositories with local user identity. Cover `.git` directories/files, nested repositories, submodules, ignored versus tracked artifacts, nested `.gitignore`, `.git/info/exclude`, global excludes via an isolated `GIT_CONFIG_GLOBAL`, ecosystem marker activation, unknown ignored files, dependency preselection, candidate collapsing, logical sizes, symlinks/junctions where supported, exclusions, inaccessible paths where supported, and cancellation.

- [ ] **Step 2: Run scanning tests and verify RED**

Run `dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter FullyQualifiedName~Scanning`; expect compilation failure because discovery/scanning types do not exist.

- [ ] **Step 3: Implement safe process and Git clients**

Use `ProcessStartInfo.ArgumentList`, redirected stdout/stderr, no shell, cancellation that kills the process tree, and explicit exit-code handling. Use `git rev-parse --is-inside-work-tree`, `git ls-files -co --exclude-standard -z` for marker inputs, and `git check-ignore -q -- <relative-path>` for candidate authority. A missing Git executable is a fatal operational error with a clear message.

- [ ] **Step 4: Implement repository discovery**

Discover fixed roots recursively, recognize `.git` files and directories, skip `.git` internals, directory links/reparse points, configured exclusions, and platform application-data/cache/trash trees unless explicitly rooted. Continue after per-path access failures and retain warnings. Do not elevate privileges or traverse network/removable volumes through `--all-drives`.

- [ ] **Step 5: Implement candidate scanning and size analysis**

Activate rules from visible project markers, walk without following links, check Git ignore status before accepting a match, reject nested repository boundaries, collapse nested matches beneath an accepted directory, and stream file count/logical length with overflow-safe totals. Apply repository/category/exclusion/minimum-size filters in the agreed order and sort repositories/candidates by bytes descending.

- [ ] **Step 6: Run tests and commit**

Run `dotnet test DevCleaner.slnx`; expect all tests to pass. Commit as `feat: discover repositories and analyze artifacts`.

### Task 4: Reports and read-only application commands

**Files:**
- Create: `src/DevCleaner/Output/ReportModels.cs`
- Create: `src/DevCleaner/Output/ReportJsonContext.cs`
- Create: `src/DevCleaner/Output/HumanReportWriter.cs`
- Create: `src/DevCleaner/Output/JsonReportWriter.cs`
- Create: `src/DevCleaner/DevCleanerApp.cs`
- Create: `src/DevCleaner/Program.cs`
- Create: `tests/DevCleaner.Tests/Output/ReportWriterTests.cs`
- Create: `tests/DevCleaner.Tests/Application/ReadOnlyCommandTests.cs`

**Interfaces:**
- Produces: `DevCleanerApp.RunAsync(string[], TextReader, TextWriter stdout, TextWriter stderr, CancellationToken)`, report schema version 1, and handlers for help/version, scan, rules list, and config path/show/validate.

- [ ] **Step 1: Write failing reporting and command tests**

Cover size-first table summaries, details rows, estimated labels, quiet/verbose/no-color/no-progress behavior, stderr progress isolation, versioned JSON fields and integer bytes, rules list metadata, config commands, missing Git, no candidates, partial warnings, and exact exit codes.

- [ ] **Step 2: Run focused tests and verify RED**

Run `dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Output|FullyQualifiedName~Application"`; expect compilation failure for missing app/report types.

- [ ] **Step 3: Implement source-generated JSON and human presentation**

Emit `schemaVersion`, `operation`, `status`, effective roots, repositories, candidate records, totals, warnings, and errors. Keep progress on stderr only for an interactive terminal and never emit ANSI color when disabled, redirected, or producing JSON.

- [ ] **Step 4: Implement read-only command orchestration and process entry point**

Resolve CLI roots over configured roots over home, add CLI exclusions to configured exclusions, validate before Git access, wire Ctrl+C to cancellation, map fatal/usage/partial/interrupted results to the specified exit codes, and make help the no-argument behavior.

- [ ] **Step 5: Run tests and commit**

Run `dotnet test DevCleaner.slnx`; expect all tests to pass. Commit as `feat: add scan reports and read-only commands`.

### Task 5: Interactive and unattended guarded cleanup

**Files:**
- Create: `src/DevCleaner/Cleaning/SelectionParser.cs`
- Create: `src/DevCleaner/Cleaning/CleanupService.cs`
- Create: `src/DevCleaner/Cleaning/CleanupModels.cs`
- Modify: `src/DevCleaner/DevCleanerApp.cs`
- Modify: `src/DevCleaner/Output/HumanReportWriter.cs`
- Modify: `src/DevCleaner/Output/ReportModels.cs`
- Create: `tests/DevCleaner.Tests/Cleaning/SelectionParserTests.cs`
- Create: `tests/DevCleaner.Tests/Cleaning/CleanupServiceTests.cs`
- Create: `tests/DevCleaner.Tests/Application/CleanCommandTests.cs`

**Interfaces:**
- Produces: `SelectionParser.Parse`, `CleanupService.ExecuteAsync`, `CleanupResult`, and the `clean` handler in `DevCleanerApp`.
- Revalidation consumes the candidate identity plus current Git/filesystem state and returns `Deleted`, `Skipped`, or `Failed` without throwing away other results.

- [ ] **Step 1: Write failing selection and cleanup tests**

Cover number/range/all selection, default repository and preselected artifact choices, dependency opt-in, literal lowercase `delete`, confirmation decline, dry-run immutability, unattended scope, `--all`, category/repository filters, tracked-after-scan, no-longer-ignored, replaced path identity, nested repository introduced after scan, symlink replacement, outside-root rejection, partial deletion failure, JSON cleanup, and cancellation after some candidates.

- [ ] **Step 2: Run cleanup tests and verify RED**

Run `dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Cleaning|FullyQualifiedName~CleanCommand"`; expect compilation failure for missing cleanup types.

- [ ] **Step 3: Implement selection and revalidation**

Interactive Enter selects all repositories and only `preselected=true` artifacts; explicit `all` includes dependencies. Before deletion, resolve full paths again, verify requested-root and repository containment, reject links/reparse points and nested `.git`, verify captured path identity, confirm Git still reports ignored, and confirm the active rule still matches.

- [ ] **Step 4: Implement guarded deletion and clean orchestration**

Delete files directly and directories recursively without following links. Continue across candidates, record exact outcomes and estimated bytes, stop scheduling further candidates on cancellation, and preserve completed outcomes. Dry-run and unattended selection use the same filter function as interactive selection.

- [ ] **Step 5: Run tests and commit**

Run `dotnet test DevCleaner.slnx`; expect all tests to pass. Commit as `feat: add guarded artifact cleanup`.

### Task 6: Cross-platform acceptance, documentation, and Native AOT releases

**Files:**
- Create: `tests/DevCleaner.Tests/Acceptance/EndToEndTests.cs`
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/release.yml`
- Create: `README.md`
- Create: `docs/configuration.schema.json`
- Modify: `.gitignore`

**Interfaces:**
- Produces: documented v1 CLI/config/JSON contract, CI tests on Windows/macOS/Linux, and release archives for `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and `linux-arm64`.

- [ ] **Step 1: Write failing end-to-end acceptance tests**

Launch the built executable against temporary real Git repositories and assert scan table/JSON parity, dry-run parity, permanent cleanup boundaries, config precedence, exact exit codes, clean stdout JSON, missing-Git diagnostics, and interruption behavior that can be tested portably.

- [ ] **Step 2: Run acceptance tests and verify RED**

Run `dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter FullyQualifiedName~Acceptance`; expect failures for missing final packaging/help/schema behavior.

- [ ] **Step 3: Complete user documentation and JSON schema**

Document installation, Git prerequisite, commands/options, examples, safety guarantees, artifact catalog, config locations/schema, automation, exit codes, estimate semantics, deferred features, and recovery implications of permanent deletion. Add `.worktrees/`, build, test, and publish outputs to `.gitignore`.

- [ ] **Step 4: Add CI and release workflows**

CI restores once, builds warnings-as-errors, runs all tests on current Windows/macOS/Linux hosted runners, and publishes/smoke-tests the host Native AOT executable. Tagged releases build each RID on a matching architecture runner, archive the single executable plus license/README, compute SHA-256 checksums, and upload artifacts.

- [ ] **Step 5: Verify all acceptance gates**

Run `dotnet restore DevCleaner.slnx`, `dotnet build DevCleaner.slnx --no-restore -warnaserror`, `dotnet test DevCleaner.slnx --no-build`, `dotnet publish src/DevCleaner/DevCleaner.csproj -c Release -r osx-arm64 --self-contained -p:PublishAot=true`, and smoke-test the published binary with `--version`, `scan --format json`, and a temporary cleanup fixture.

- [ ] **Step 6: Commit**

Commit as `docs: complete DevCleaner v1 release surface`.

## Final Review

- [ ] Generate a whole-branch diff from the merge base and complete spec-compliance and code-quality review.
- [ ] Re-run the full build, test, local Native AOT publish, CLI smoke tests, `git diff --check`, and `git status --short` before claiming completion.
