# RepoGlean

RepoGlean is a stateless, cross-platform command-line tool that finds regenerable development artifacts in Git working trees, reports their estimated logical size, and can permanently remove an explicitly selected set. Git is the authority: an item is eligible only when it is ignored by Git and matches an active built-in or custom rule. Unknown ignored content is never selected.

RepoGlean 2.0.0 is a .NET 10 Native AOT executable for Windows, macOS, and Linux. It does not require a .NET runtime, but it does require the `git` executable on `PATH`.

## Install

### Homebrew

On macOS or Linux:

```bash
brew install aczarkowski/tap/repoglean
```

Check for a newer stable release and upgrade with:

```bash
brew livecheck aczarkowski/tap/repoglean
brew update
brew upgrade repoglean
```

### Release archives

Download the archive for the machine that will run RepoGlean:

| Operating system | x64 | Arm64 |
| --- | --- | --- |
| Windows | `repoglean-win-x64.tar.gz` | `repoglean-win-arm64.tar.gz` |
| macOS | `repoglean-osx-x64.tar.gz` | `repoglean-osx-arm64.tar.gz` |
| Linux | `repoglean-linux-x64.tar.gz` | `repoglean-linux-arm64.tar.gz` |

Verify the archive against its adjacent `.sha256` file, extract it, and put `repoglean` (`repoglean.exe` on Windows) somewhere on `PATH`. On macOS or Linux, retain or restore its executable bit with `chmod +x repoglean` if your transfer tool discarded permissions.

Confirm both prerequisites:

```console
$ git --version
$ repoglean --version
repoglean 2.0.0
```

Release archives contain the single self-contained executable, this README, and the MIT `LICENSE`.

### Linux 2.0.0 baseline

The `linux-x64` and `linux-arm64` archives are glibc Native AOT builds produced on Ubuntu 24.04. They do not target musl; Alpine Linux and other musl-only environments are unsupported in 2.0.0. Because Native AOT binds to its build environment, use a glibc distribution compatible with the Ubuntu 24.04 build baseline.

RepoGlean also calls the C library's `statx(2)` entry point and requires the kernel and backing filesystem to return both `STATX_INO` and `STATX_MNT_ID`. `STATX_MNT_ID` is available from Linux 5.8, but RepoGlean has no fallback when either requested field is unavailable. The release gates exercise a deliberately narrower, reproducible combination: Ubuntu 24.04 x64 and Arm64 GitHub-hosted runners, a Linux 6.x-or-newer kernel, and an ext4 disposable smoke repository.

## Commands

```text
repoglean scan [root ...] [options]
repoglean clean [root ...] [options]
repoglean rules list [--format table|json] [--config path]
repoglean config path|show|validate [--config path]
repoglean help | --help | version | --version
```

With no arguments, RepoGlean prints help. `scan` never changes files. `clean --dry-run` performs the same discovery, filtering, selection, and safety validation as cleanup, but does not delete. Interactive cleanup selects repositories and artifacts, then requires the exact lowercase confirmation `delete`. Pressing Enter at the artifact prompt honors command-line opt-ins: `clean --all` includes dependency artifacts, and an explicit category such as `--category dependency` selects matching artifacts.

### Command-specific option matrix

Options outside the listed commands are usage errors with exit code 2.

| Option | Allowed commands | Meaning |
| --- | --- | --- |
| `root ...` | `scan`, `clean` | Search these roots; positional and repeatable. |
| `--repo name-or-path` | `scan`, `clean` | Include a repository by leaf name or full path; repeatable. |
| `--category value` | `scan`, `clean` | Include `build`, `cache`, `test`, `ide`, or `dependency`; repeatable. Selecting `dependency` explicitly opts in dependency artifacts. |
| `--exclude path-or-glob` | `scan`, `clean` | Add a root-relative path, absolute path, or repository-relative glob exclusion; repeatable. |
| `--min-size size` | `scan`, `clean` | Require an estimated minimum size such as `500MB` or `2GiB`. |
| `--all-drives` | `scan`, `clean` | Add accessible fixed-drive roots to the requested roots. |
| `--details` | `scan` | Print candidate rows. |
| `--dry-run` | `clean` | Validate and report selected candidates without deletion or prompts. |
| `--yes` | `clean` | Run unattended; requires an explicit `--all`, `--repo`, or `--category` scope. |
| `--all` | `clean` | Include dependency artifacts as well as normally preselected categories. |
| `--format value` | `scan`, `clean`, `rules list` | Select `table` or the versioned `json` document. |
| `--config path` | `scan`, `clean`, `rules list`, `config path`, `config show`, `config validate` | Resolve or load an explicit configuration path. |
| `--quiet` | `scan`, `clean` | Suppress detail and diagnostic sections while keeping the summary. |
| `--verbose` | `scan`, `clean` | Include candidate or diagnostic detail rows in human output. |
| `--no-color` | `scan`, `clean` | Disable ANSI styling; redirected and JSON output are never colored. |
| `--no-progress` | `scan`, `clean` | Suppress interactive stderr progress messages. |
| `--help` | Standalone, or with a complete command and no other options | Show global help instead of running the command. |
| `--version` | Standalone, or with a complete command and no other options | Show the version instead of running the command. |

JSON cleanup is non-interactive by contract: `clean --format json` requires `--dry-run` or `--yes`.

### Examples

```console
# Inspect all detected candidates below the current directory.
repoglean scan . --details

# Produce a JSON report for CI or another program.
repoglean scan ~/src --format json --no-progress

# Preview normally preselected build/cache/test/IDE artifacts.
repoglean clean ~/src --dry-run --format json

# Permanently delete build artifacts in one named repository without prompts.
repoglean clean ~/src --yes --repo example --category build --format json

# Explicitly include dependency directories such as node_modules and virtual environments.
repoglean clean ~/src --yes --category dependency
```

## Configuration

The default path is `%APPDATA%\repoglean\config.json` on Windows and `$XDG_CONFIG_HOME/repoglean/config.json` on macOS/Linux. If `XDG_CONFIG_HOME` is unset, macOS/Linux use `~/.config/repoglean/config.json`. A genuinely absent implicit default file produces the empty version 1 defaults. Supplying `--config` is explicit: commands that load configuration fail with exit code 2 when that path is missing, is a directory, is unreadable, or contains invalid configuration. `repoglean config path` only prints the resolved path, `config show` prints the effective document, and `config validate` validates it without requiring Git.

Command-line roots take precedence over configured `roots`; configured roots take precedence over the user home directory default. Command-line `--exclude` values are added to configured `excludes`. Other command-line filters narrow the discovered candidates.

The published [JSON Schema](docs/configuration.schema.json) describes schema version 1. The loader accepts comments, trailing commas, case-insensitive property names and named categories, and unknown properties for forward compatibility; generated output uses canonical camelCase names. If recognized properties are repeated with different casing, the last valid value wins; all occurrences must be valid. For strict JSON documents, schema-aware tooling applies the same property and value contract.

```json
{
  "schemaVersion": 1,
  "roots": ["/work"],
  "excludes": ["archive/**"],
  "disabledRules": ["node.next"],
  "customRules": [
    {
      "id": "example.generated",
      "category": "Build",
      "patterns": ["**/.generated", "**/.generated/**"],
      "markers": ["**/example.project"],
      "preselected": false
    }
  ]
}
```

Custom rule IDs are non-whitespace, stable lowercase dotted or hyphenated identifiers. Candidate patterns are non-whitespace repository-relative globs without `.` or `..` path segments; leading `/`, leading `\`, drive-rooted, and UNC forms are rejected. `*` and `?` do not cross a path separator, while `**` spans path segments. Markers accept any non-whitespace glob string and are matched only against repository-relative visible paths. A custom rule needs a standard category and at least one pattern, cannot reuse a built-in ID, defaults to `preselected=false`, and is rejected if it explicitly sets `preselected=true` in schema version 1.

## Built-in artifact catalog

Dependency rules are reported but are not preselected. Generic directory names activate only when the corresponding project marker is visible in the repository.

| Rule ID | Category | Candidate | Marker family | Preselected |
| --- | --- | --- | --- | --- |
| `dotnet.bin` | build | `bin` | solution or .NET project | yes |
| `dotnet.obj` | build | `obj` | solution or .NET project | yes |
| `dotnet.test-results` | test | `TestResults` | solution or .NET project | yes |
| `node.node-modules` | dependency | `node_modules` | `package.json` | no |
| `node.next` | build | `.next` | `package.json` | yes |
| `node.nuxt` | build | `.nuxt` | `package.json` | yes |
| `node.svelte-kit` | build | `.svelte-kit` | `package.json` | yes |
| `jvm.maven-target` | build | `target` | `pom.xml` | yes |
| `jvm.gradle-build` | build | `build` | Gradle build/settings file | yes |
| `jvm.gradle-cache` | cache | `.gradle` | Gradle build/settings file | yes |
| `rust.target` | build | `target` | `Cargo.toml` | yes |
| `python.pycache` | cache | `__pycache__` | Python project/requirements/setup | yes |
| `python.pytest-cache` | test | `.pytest_cache` | Python project/requirements/setup | yes |
| `python.venv` | dependency | `.venv` or `venv` | Python project/requirements/setup | no |
| `go.bin` | build | `bin` | `go.mod` | yes |
| `go.coverage` | test | `coverage.out` | `go.mod` | yes |
| `cpp.cmake-build` | build | `cmake-build-*` | `CMakeLists.txt` | yes |
| `cpp.build` | build | `build` or `out` | `CMakeLists.txt` or `Makefile` | yes |
| `apple.derived-data` | cache | `DerivedData` | Xcode project or `Package.swift` | yes |
| `apple.build` | build | `build` | Xcode project or `Package.swift` | yes |

## Safety and permanent deletion

Before listing a candidate, RepoGlean requires a Git working tree, an active matching rule, Git-ignored status, no tracked or otherwise visible content inside the candidate, a path below the repository and requested roots, a stable filesystem identity, and one filesystem mount. Discovery does not follow directory symlinks, junctions, or reparse points; it rejects candidate links, links inside candidates, nested repositories, Git metadata, mount crossings, and inaccessible or uncertain paths. Root-level `.repoglean-quarantine-*` directories are a reserved namespace: scans prune them, emit their exact path as a warning, and never rediscover stranded payloads as candidates.

Scanning binds every candidate to the stable identity and mount of its repository root. Cleanup revalidates that repository boundary, rule activation, ignore state, tracked/visible content, candidate identity/type/mount, and link-free ancestors after the pre-move callback. It creates a private GUID-named quarantine inside the validated repository and atomically moves the candidate to a unique payload name with a no-copy, no-overwrite platform primitive. The same authority is checked against the absent original path after ownership and again at the final deletion boundary; Git index queries catch newly staged paths and ignore evaluation bypasses index suppression for the absent path. Marker-visible Git listings exclude the reserved quarantine namespace so owned or previously stranded payloads cannot activate rules, while an independent index check still rejects tracked or staged content in the current quarantine.

After ownership, RepoGlean records an exact descendant snapshot of relative paths, stable identities, entry types, and mounts. It requires the same snapshot after the final callback, then uses a focused boundary-aware deleter: links and name-surrogate reparse points are removed only as leaves, directories on another mount are refused, and every directory identity and mount is rechecked before removal. Uncertain or changed state stops with an explicit failure rather than crossing the boundary.

The approved threat boundary is precise: RepoGlean protects against accidental concurrent changes from normal developer tools, including Git/index updates and changes made through retained directory handles at the guarded seams. It does **not** claim protection against a malicious same-user process that continuously discovers and races individual no-follow checks inside the GUID-private quarantine.

Deletion is permanent: RepoGlean does not use the recycle bin/trash and has no undo. If ownership was transferred but deletion has not succeeded, RepoGlean moves content back only when the quarantined object still has the exact expected payload identity, type, and mount, the original leaf is absent, the repository/parent boundary remains valid, and an atomic reverse move is safe. Otherwise it reports the exact retained or possibly retained quarantine payload path. Once boundary-aware deletion starts, interruption or I/O failure may leave a partially deleted artifact; the report identifies that candidate and separately preserves whether payload deletion completed. Back up anything that is not reproducible before cleaning.

## Output and automation contract

Human reports go to stdout; interactive progress goes to stderr. `--no-progress` suppresses progress, and JSON mode suppresses it automatically so stdout contains one JSON document and no banners. JSON integer fields are byte/file/count values, `schemaVersion` is currently `1`, and the stable top-level fields are:

- `schemaVersion`, `operation`, and `status` (`success`, `partial`, `failed`, or `interrupted`);
- `effectiveRoots`, `repositories`, `totals`, `warnings`, and `errors`;
- `rules` for `rules list`, and `cleanup` for clean reports.

Cleanup candidates add `outcome`, `message`, and `deletionCompleted`; the cleanup summary distinguishes originally selected candidates from processed, irreversibly deleted, skipped, and failed candidates. `deletionCompleted` can be true even when the overall item is failed because post-delete empty-quarantine cleanup failed. Automation should parse fields rather than table text and must check the process exit code:

| Exit code | Meaning |
| --- | --- |
| `0` | Success, user-canceled interactive cleanup, or no candidates. |
| `1` | Fatal operational failure, including unavailable Git. |
| `2` | Invalid invocation or configuration. |
| `3` | Partial result: warnings, safety skips, or per-candidate failures. |
| `130` | Interrupted operation. |

Reported sizes are estimates: RepoGlean sums logical file lengths, saturating at the maximum signed 64-bit integer. They are not filesystem block usage or promised reclaimed capacity, and may differ because of sparse files, compression, clones, hard links, metadata, or concurrent change. `estimatedDeletedBytes` counts candidates whose payload deletion completed, independently of later quarantine-cleanup status.

## Deferred beyond 2.0.0

V1 does not provide a GUI, daemon/scheduler, trash/undo, automatic package-manager cleanup, remote-host cleanup, arbitrary deletion rules outside Git repositories, physical disk-allocation accounting, or protection from a malicious same-user filesystem racer. Use an external scheduler around explicit scoped commands if required; keep the same exit-code and JSON checks.

## Build and test

Install the .NET 10 SDK and Git, then run:

```console
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx --no-restore -warnaserror
dotnet test RepoGlean.slnx --no-build
dotnet publish src/RepoGlean/RepoGlean.csproj -c Release -r osx-arm64 --self-contained -p:PublishAot=true
```

CI runs the warning-as-error build, full tests, and `eng/native-smoke.ps1` against the final package-named host Native AOT executable on Windows, macOS, and Linux. The smoke creates a disposable real Git repository, parses JSON scan and scoped-clean results, and proves protected content survives. A `v*` tag runs the same packaged-executable smoke for all six release RIDs on matching-architecture runners before archiving the executable with `LICENSE` and `README.md`, generating SHA-256 files, and attaching everything to the GitHub release.
