# RepoGlean Clean-Break Rename Design

## Goal

Rename the published DevCleaner v1 codebase and GitHub repository to RepoGlean before package-manager distribution begins.

## Public identity

- Product and assembly name: `RepoGlean`
- Executable name: `repoglean` on macOS/Linux and `repoglean.exe` on Windows
- GitHub repository: `aczarkowski/RepoGlean`
- Solution: `RepoGlean.slnx`
- Production project: `src/RepoGlean/RepoGlean.csproj`
- Test project: `tests/RepoGlean.Tests/RepoGlean.Tests.csproj`
- Root namespace: `RepoGlean`
- Release archives: `repoglean-<rid>.tar.gz`
- Default configuration directory: `repoglean`
- Private quarantine prefix: `.repoglean-quarantine-`

The product description is:

> RepoGlean safely reclaims space from regenerable Git artifacts.

## Compatibility boundary

This is a clean break. DevCleaner v1 has no users requiring migration.

- Do not ship a `devcleaner` executable or alias.
- Do not read or migrate `devcleaner/config.json`.
- Do not recognize `.devcleaner-quarantine-*` as a RepoGlean-owned namespace.
- Do not retain DevCleaner names in active source, tests, workflows, schema metadata, executable output, or current documentation.
- Historical implementation plans and specifications remain unchanged because they document the DevCleaner v1 work as it occurred.
- Leave the existing remote `v1` tag and release immutable as historical evidence.

## Versioning and release

The first RepoGlean release is `v2.0.0`. The major version records the breaking executable and product identity change. The release workflow continues to build matching-architecture .NET 10 Native AOT executables for:

- `win-x64`
- `win-arm64`
- `osx-x64`
- `osx-arm64`
- `linux-x64`
- `linux-arm64`

Every archive contains the platform executable, `README.md`, and `LICENSE`, with an adjacent SHA-256 checksum. The workflow smoke-tests the final package-named executable before publishing the GitHub release.

## Implementation

Rename source and test paths with `git mv`, then update namespaces, type names that contain the old product identity, assembly metadata, solution/project references, user-facing text, configuration paths, quarantine paths, temporary test names, documentation, and CI/release artifact names.

The JSON document contract remains schema version 1 because its fields and semantics do not change. Only the schema title and description adopt the RepoGlean identity.

## Verification

The rename is accepted only when:

1. Rename-specific tests first fail against the DevCleaner implementation and then pass against RepoGlean.
2. Active files outside historical `docs/superpowers/plans` and `docs/superpowers/specs` contain no case-insensitive `devcleaner` occurrence.
3. `dotnet restore RepoGlean.slnx`, warning-as-error build, and the full test suite pass.
4. Configuration contract/schema parity tests pass.
5. Native AOT package smokes pass locally for macOS Arm64 and x64/Rosetta.
6. The GitHub repository is renamed to `aczarkowski/RepoGlean`.
7. The verified branch is pushed and all GitHub CI jobs pass.
8. The annotated `v2.0.0` tag is pushed and the GitHub release contains all six archives and checksums.

## Distribution boundary

Homebrew, Scoop, and WinGet packaging are subsequent work. This rename establishes their canonical package name and release URLs but does not create those distribution manifests.
