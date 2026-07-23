# DevCleaner

DevCleaner is a stateless, cross-platform command-line tool that finds regenerable development artifacts in Git working trees, reports their estimated logical size, and can permanently remove an explicitly selected set. Git is the authority: an item is eligible only when it is ignored by Git and matches an active built-in or custom rule. Unknown ignored content is never selected.

DevCleaner v1 is a .NET 10 Native AOT executable for Windows, macOS, and Linux. It does not require a .NET runtime, but it does require the `git` executable on `PATH`.

## Install

Download the archive for the machine that will run DevCleaner:

| Operating system | x64 | Arm64 |
| --- | --- | --- |
| Windows | `devcleaner-win-x64.tar.gz` | `devcleaner-win-arm64.tar.gz` |
| macOS | `devcleaner-osx-x64.tar.gz` | `devcleaner-osx-arm64.tar.gz` |
| Linux | `devcleaner-linux-x64.tar.gz` | `devcleaner-linux-arm64.tar.gz` |

Verify the archive against its adjacent `.sha256` file, extract it, and put `devcleaner` (`devcleaner.exe` on Windows) somewhere on `PATH`. On macOS or Linux, retain or restore its executable bit with `chmod +x devcleaner` if your transfer tool discarded permissions.

Confirm both prerequisites:

```console
$ git --version
$ devcleaner --version
devcleaner 1.0.0
```

Release archives contain the single self-contained executable, this README, and the MIT `LICENSE`.

## Commands

```text
devcleaner scan [root ...] [options]
devcleaner clean [root ...] [options]
devcleaner rules list [--format table|json] [--config path]
devcleaner config path|show|validate [--config path]
devcleaner help | --help | version | --version
```

With no arguments, DevCleaner prints help. `scan` never changes files. `clean --dry-run` performs the same discovery, filtering, selection, and safety validation as cleanup, but does not delete. Interactive cleanup selects repositories and artifacts, then requires the exact lowercase confirmation `delete`.

### Scan and clean options

| Option | Meaning |
| --- | --- |
| `--repo name-or-path` | Include a repository by leaf name or full path; repeatable. |
| `--category build|cache|test|ide|dependency` | Include one artifact category; repeatable. Selecting `dependency` explicitly opts in dependency artifacts. |
| `--exclude path-or-glob` | Exclude a root-relative path, absolute path, or repository-relative glob; repeatable and additive with configured exclusions. |
| `--min-size size` | Include candidates at or above the estimated size. Accepts bytes and decimal or binary units such as `500MB` or `2GiB`. |
| `--all-drives` | Add all accessible fixed-drive roots to the requested roots. |
| `--format table|json` | Select human output (default) or the versioned machine-readable document. |
| `--config path` | Use an explicit configuration file. |
| `--quiet` | Suppress detail rows while keeping a summary. |
| `--verbose` | Include candidate detail rows in table output. |
| `--no-color` | Disable ANSI styling. Redirected and JSON output are never colored. |
| `--no-progress` | Suppress interactive progress messages. Progress otherwise uses stderr only. |

`scan` also accepts `--details`, which prints candidate rows. `clean` accepts:

| Option | Meaning |
| --- | --- |
| `--dry-run` | Validate and report selected candidates without deletion or prompts. |
| `--yes` | Run unattended. It is rejected unless paired with an explicit `--all`, `--repo`, or `--category` scope. |
| `--all` | Include dependency artifacts as well as the normally preselected categories. |

JSON cleanup is non-interactive by contract: `clean --format json` requires `--dry-run` or `--yes`.

### Examples

```console
# Inspect all detected candidates below the current directory.
devcleaner scan . --details

# Produce a JSON report for CI or another program.
devcleaner scan ~/src --format json --no-progress

# Preview normally preselected build/cache/test/IDE artifacts.
devcleaner clean ~/src --dry-run --format json

# Permanently delete build artifacts in one named repository without prompts.
devcleaner clean ~/src --yes --repo example --category build --format json

# Explicitly include dependency directories such as node_modules and virtual environments.
devcleaner clean ~/src --yes --category dependency
```

## Configuration

The default path is `%APPDATA%\devcleaner\config.json` on Windows and `$XDG_CONFIG_HOME/devcleaner/config.json` on macOS/Linux. If `XDG_CONFIG_HOME` is unset, macOS/Linux use `~/.config/devcleaner/config.json`. `devcleaner config path` prints the resolved path, `config show` prints the effective document, and `config validate` validates it without requiring Git.

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

Custom rule IDs are non-whitespace, stable lowercase dotted or hyphenated identifiers. Candidate patterns are non-whitespace repository-relative globs without `.` or `..` path segments; `*` and `?` do not cross a path separator, while `**` spans path segments. Markers accept any non-whitespace glob string and are matched only against repository-relative visible paths. A custom rule needs a standard category and at least one pattern, cannot reuse a built-in ID, defaults to `preselected=false`, and is rejected if it explicitly sets `preselected=true` in v1.

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

Before listing a candidate, DevCleaner requires a Git working tree, an active matching rule, Git-ignored status, no tracked or otherwise visible content inside the candidate, a path below the repository and requested roots, a stable filesystem identity, and one filesystem mount. Discovery does not follow directory symlinks, junctions, or reparse points; it rejects candidate links, links inside candidates, nested repositories, Git metadata, mount crossings, and inaccessible or uncertain paths.

Cleanup revalidates the requested-root boundary, repository identity, rule activation, ignore state, visible content, candidate identity/type/mount, and link-free ancestors. It then creates a private GUID-named quarantine directory on the same filesystem and atomically moves the candidate to a unique payload name with a no-copy, no-overwrite platform primitive. The moved object and quarantine identities are checked again before recursive deletion. A failed or changed check is reported instead of being treated as success, and an identity-mismatched object is never deleted.

The approved v1 threat boundary is precise: DevCleaner protects against accidental concurrent filesystem changes from normal developer tools before permanent deletion. It does **not** claim protection against a malicious process running as the same user that actively discovers and races the GUID-private quarantine path inside the .NET runtime's recursive deletion implementation. The runtime treats symbolic links, junctions, and other name-surrogate reparse points encountered by that deletion primitive as leaf entries rather than following their targets; the malicious same-user race inside that runtime primitive remains out of scope.

Deletion is permanent: DevCleaner does not use the recycle bin/trash and has no undo. If ownership was transferred to quarantine but deletion has not succeeded, DevCleaner attempts identity-checked recovery only when the original leaf is absent, its parent boundary remains link-free, and an atomic move back is safe. Otherwise it reports the exact retained quarantine payload path. Once recursive deletion starts, interruption or I/O failure may leave a partially deleted artifact; the report preserves completed outcomes and identifies the interrupted/failed candidate. Back up anything that is not reproducible before cleaning.

## Output and automation contract

Human reports go to stdout; interactive progress goes to stderr. `--no-progress` suppresses progress, and JSON mode suppresses it automatically so stdout contains one JSON document and no banners. JSON integer fields are byte/file/count values, `schemaVersion` is currently `1`, and the stable top-level fields are:

- `schemaVersion`, `operation`, and `status` (`success`, `partial`, `failed`, or `interrupted`);
- `effectiveRoots`, `repositories`, `totals`, `warnings`, and `errors`;
- `rules` for `rules list`, and `cleanup` for clean reports.

Cleanup candidates add `outcome` and `message`; the cleanup summary distinguishes originally selected candidates from processed, deleted, skipped, and failed candidates. Automation should parse fields rather than table text and must check the process exit code:

| Exit code | Meaning |
| --- | --- |
| `0` | Success, user-canceled interactive cleanup, or no candidates. |
| `1` | Fatal operational failure, including unavailable Git. |
| `2` | Invalid invocation or configuration. |
| `3` | Partial result: warnings, safety skips, or per-candidate failures. |
| `130` | Interrupted operation. |

Reported sizes are estimates: DevCleaner sums logical file lengths, saturating at the maximum signed 64-bit integer. They are not filesystem block usage or promised reclaimed capacity, and may differ because of sparse files, compression, clones, hard links, metadata, or concurrent change. `estimatedDeletedBytes` counts only candidates reported deleted.

## Deferred beyond v1

V1 does not provide a GUI, daemon/scheduler, trash/undo, automatic package-manager cleanup, remote-host cleanup, arbitrary deletion rules outside Git repositories, physical disk-allocation accounting, or protection from a malicious same-user filesystem racer. Use an external scheduler around explicit scoped commands if required; keep the same exit-code and JSON checks.

## Build and test

Install the .NET 10 SDK and Git, then run:

```console
dotnet restore DevCleaner.slnx
dotnet build DevCleaner.slnx --no-restore -warnaserror
dotnet test DevCleaner.slnx --no-build
dotnet publish src/DevCleaner/DevCleaner.csproj -c Release -r osx-arm64 --self-contained -p:PublishAot=true
```

CI runs the warning-as-error build, full tests, and a host Native AOT smoke test on current Windows, macOS, and Linux hosted runners. A `v*` tag builds and smoke-tests all six release RIDs on matching-architecture runners, archives the executable with `LICENSE` and `README.md`, generates SHA-256 files, and attaches everything to the GitHub release.
