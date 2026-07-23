# RepoGlean Homebrew Tap Design

## Goal

Publish RepoGlean through a public Homebrew tap owned by `aczarkowski`, backed by
the existing immutable `v2.0.0` Native AOT release archives.

## Repository and user experience

Create the public GitHub repository `github.com/aczarkowski/homebrew-tap` with
`master` as its default branch. Homebrew maps that repository to the short tap
name `aczarkowski/tap`.

The tap contains one formula:

```text
Formula/repoglean.rb
```

The primary installation command is:

```bash
brew install aczarkowski/tap/repoglean
```

After installation, `repoglean` is available on `PATH`. The tap README also
documents the equivalent two-step flow:

```bash
brew tap aczarkowski/tap
brew install repoglean
```

RepoGlean's main README adds the primary one-command installation method while
retaining the direct archive instructions.

## Formula contract

`class RepoGlean < Formula` declares:

- description: `Safely reclaim space from regenerable Git artifacts`
- homepage: `https://github.com/aczarkowski/RepoGlean`
- version: `2.0.0`
- license: `MIT`
- runtime dependency: Homebrew `git`

The formula installs the existing precompiled, self-contained executable. It
selects exactly one archive using Homebrew's operating-system and CPU
conditionals:

| Homebrew host | Release archive | SHA-256 |
| --- | --- | --- |
| macOS ARM64 | `repoglean-osx-arm64.tar.gz` | `2c5d0ef69bad09bc1283b2867f2bd22d955f8970990ee71e92f0a72464733603` |
| macOS x64 | `repoglean-osx-x64.tar.gz` | `d58f08fb7b00f2acf0dcbdca6af8334f8ea6d9d6a44a6876dd4243df497bf900` |
| Linux ARM64 | `repoglean-linux-arm64.tar.gz` | `b52b95dcb2b24d99862fd82deb132144a18b51b9fe2e2344aa9b7b9d7695cc20` |
| Linux x64 | `repoglean-linux-x64.tar.gz` | `3731f411e7227b092d0098e1cb89de08208096bd3b95b774a389e2a2fd9aba96` |

Each archive has a single top-level `repoglean-<rid>` directory. The formula's
install method places its `repoglean` executable in Homebrew's `bin`.

The formula test runs:

```bash
repoglean --version
```

and requires the exact output `repoglean 2.0.0`.

The formula also declares:

```ruby
livecheck do
  url :stable
  strategy :github_latest
end
```

This lets `brew livecheck aczarkowski/tap/repoglean` compare the formula version
with RepoGlean's latest non-draft GitHub release. Livecheck only detects a newer
version; it does not mutate the formula.

## Tap validation and CI

Bootstrap the repository with Homebrew's current `brew tap-new` structure, then
replace its example formula content with `Formula/repoglean.rb` and add a concise
RepoGlean-specific README.

The committed tap must pass:

```bash
brew style aczarkowski/tap/repoglean
brew audit --strict --online aczarkowski/tap/repoglean
brew livecheck aczarkowski/tap/repoglean
brew install aczarkowski/tap/repoglean
brew test aczarkowski/tap/repoglean
repoglean --version
```

The generated Homebrew tap workflows remain enabled so future formula changes
receive Homebrew's standard audit and test coverage. The initial formula is also
validated through a real local installation from the published GitHub
repository.

## Release updates

The tap includes a deterministic formula updater and a GitHub Actions workflow.
The workflow runs daily at `06:17 UTC` and supports `workflow_dispatch` for an
immediate manual check.

The updater reads RepoGlean's public GitHub `releases/latest` API response and:

1. accepts only a stable tag matching `v<major>.<minor>.<patch>`;
2. requires all four macOS/Linux archives and their four adjacent checksum
   assets;
3. validates each checksum as exactly 64 lowercase hexadecimal characters;
4. renders the complete formula with the new version, URLs, and checksums;
5. makes no change when the formula already describes the latest release.

When the updater produces a change, the workflow runs the updater's tests,
Homebrew style, strict online audit, and livecheck. It then creates or updates a
branch and opens a pull request in `aczarkowski/homebrew-tap`. It never pushes a
version change directly to `master`.

The workflow needs only the tap repository's scoped `GITHUB_TOKEN` with
`contents: write` and `pull-requests: write`. Reading RepoGlean's public release
metadata and assets does not require a cross-repository credential.

The tap maintainer reviews and merges the update pull request. Users receive the
new formula through their next `brew update`, after which `brew outdated` and
`brew upgrade repoglean` follow Homebrew's normal version comparison.

## Error handling and safety

- Homebrew rejects a download whose checksum differs from the committed value.
- Unsupported operating systems or CPU architectures fail rather than falling
  back to an incompatible binary.
- The formula never downloads mutable branch content; it uses immutable,
  version-tagged release URLs only.
- The tap is public and contains no repository secrets or cross-repository
  write credential.
- An incomplete, malformed, draft, or prerelease GitHub release cannot produce
  a formula update.
- Update automation opens a pull request and cannot bypass review by writing a
  new version directly to `master`.
- The existing RepoGlean `v2.0.0` release and tag remain unchanged.

## Acceptance criteria

1. `github.com/aczarkowski/homebrew-tap` exists publicly with `master` as its
   default branch.
2. The repository contains `Formula/repoglean.rb`, a tap README, and standard
   Homebrew tap CI.
3. Formula style and strict online audit pass.
4. Livecheck reports `2.0.0` as current against RepoGlean's latest stable
   release.
5. Updater tests prove correct four-platform rendering, no-op behavior,
   malformed-tag rejection, missing-asset rejection, and malformed-checksum
   rejection.
6. The scheduled workflow has only the permissions required to update a branch
   and open a tap pull request.
7. A fresh installation through `brew install aczarkowski/tap/repoglean`
   succeeds on the current macOS ARM64 host.
8. `brew test aczarkowski/tap/repoglean` passes.
9. The installed executable reports exactly `repoglean 2.0.0`.
10. The main RepoGlean README documents installation, livecheck, and upgrade
    commands.
11. RepoGlean's full existing test suite remains green.
