# RepoGlean Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the active DevCleaner product, executable, code, tests, release artifacts, and GitHub repository identity with the clean-break RepoGlean identity and publish verified `v2.0.0` assets.

**Architecture:** Preserve the existing safety and JSON behavior while mechanically renaming the .NET solution and every active product boundary. Add contract tests for the new executable/config/quarantine identity before changing production code, then rename build and release infrastructure after the application contract is green.

**Tech Stack:** .NET 10, C#, xUnit, Native AOT, PowerShell smoke script, GitHub Actions, GitHub Releases.

## Global Constraints

- Product and assembly name is exactly `RepoGlean`.
- Executable is exactly `repoglean` or `repoglean.exe`.
- Root namespace is exactly `RepoGlean`.
- Default configuration directory is exactly `repoglean`.
- Private quarantine prefix is exactly `.repoglean-quarantine-`.
- Do not add DevCleaner aliases, legacy configuration fallback, or legacy quarantine recognition.
- Historical files below `docs/superpowers/plans`, `docs/superpowers/specs`, and `.superpowers/sdd` remain unchanged.
- JSON schema version and output field semantics remain version 1.
- Existing remote `v1` tag and release remain immutable.
- First RepoGlean release is exactly `v2.0.0`.

---

### Task 1: Specify and test the RepoGlean public identity

**Files:**
- Modify: `tests/DevCleaner.Tests/Application/ReadOnlyCommandTests.cs`
- Modify: `tests/DevCleaner.Tests/Configuration/ConfigLoaderTests.cs`
- Modify: `tests/DevCleaner.Tests/Git/GitClientTests.cs`

**Interfaces:**
- Consumes: `DevCleanerApp.RunAsync`, `ConfigLoader.GetDefaultPath`, and `GitClient.QuarantineDirectoryPrefix`.
- Produces: failing contract assertions for `repoglean 2.0.0`, the `repoglean/config.json` default, and `.repoglean-quarantine-`.

- [ ] **Step 1: Add rename contract assertions**

Add focused tests that require:

```csharp
Assert.Equal("repoglean 2.0.0", output.ToString().Trim());
Assert.EndsWith(Path.Combine("repoglean", "config.json"), ConfigLoader.GetDefaultPath());
Assert.Equal(".repoglean-quarantine-", GitClient.QuarantineDirectoryPrefix);
```

Use the existing application runner and environment-isolation helpers in each test file.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj \
  --filter "FullyQualifiedName~ReadOnlyCommandTests|FullyQualifiedName~ConfigLoaderTests|FullyQualifiedName~GitClientTests"
```

Expected: failures showing `devcleaner 1.0.0`, a `devcleaner/config.json` suffix, and `.devcleaner-quarantine-`.

- [ ] **Step 3: Commit the verified failing contracts**

```bash
git add tests/DevCleaner.Tests
git commit -m "test: specify RepoGlean public identity"
```

### Task 2: Rename the .NET solution and application identity

**Files:**
- Rename: `DevCleaner.slnx` to `RepoGlean.slnx`
- Rename: `src/DevCleaner` to `src/RepoGlean`
- Rename: `src/RepoGlean/DevCleaner.csproj` to `src/RepoGlean/RepoGlean.csproj`
- Rename: `src/RepoGlean/DevCleanerApp.cs` to `src/RepoGlean/RepoGleanApp.cs`
- Rename: `src/RepoGlean/Configuration/DevCleanerConfig.cs` to `src/RepoGlean/Configuration/RepoGleanConfig.cs`
- Rename: `src/RepoGlean/Configuration/DevCleanerJsonContext.cs` to `src/RepoGlean/Configuration/RepoGleanJsonContext.cs`
- Modify: all active `.cs`, `.csproj`, and `.slnx` files under `src` and `tests`

**Interfaces:**
- Consumes: the Task 1 public identity tests.
- Produces: `RepoGleanApp`, `RepoGleanConfig`, `RepoGleanJsonContext`, `RepoGlean.slnx`, and `RepoGlean` namespaces/assemblies.

- [ ] **Step 1: Rename source paths with Git**

```bash
git mv DevCleaner.slnx RepoGlean.slnx
git mv src/DevCleaner src/RepoGlean
git mv src/RepoGlean/DevCleaner.csproj src/RepoGlean/RepoGlean.csproj
git mv src/RepoGlean/DevCleanerApp.cs src/RepoGlean/RepoGleanApp.cs
git mv src/RepoGlean/Configuration/DevCleanerConfig.cs src/RepoGlean/Configuration/RepoGleanConfig.cs
git mv src/RepoGlean/Configuration/DevCleanerJsonContext.cs src/RepoGlean/Configuration/RepoGleanJsonContext.cs
```

- [ ] **Step 2: Replace active code identifiers**

Within active source and tests, replace:

```text
DevCleanerApp         -> RepoGleanApp
DevCleanerConfig      -> RepoGleanConfig
DevCleanerJsonContext -> RepoGleanJsonContext
DevCleaner.Tests      -> RepoGlean.Tests
DevCleaner            -> RepoGlean
devcleaner            -> repoglean
```

Do not modify historical plan/spec documents.

Set the production project version explicitly so local contract tests and the
first release agree:

```xml
<Version>2.0.0</Version>
```

- [ ] **Step 3: Rename the test project paths**

```bash
git mv tests/DevCleaner.Tests tests/RepoGlean.Tests
git mv tests/RepoGlean.Tests/DevCleaner.Tests.csproj tests/RepoGlean.Tests/RepoGlean.Tests.csproj
```

Update `RepoGlean.slnx`, the project reference, and `InternalsVisibleTo("RepoGlean.Tests")`.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run:

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj \
  --filter "FullyQualifiedName~ReadOnlyCommandTests|FullyQualifiedName~ConfigLoaderTests|FullyQualifiedName~GitClientTests"
```

Expected: all selected tests pass.

- [ ] **Step 5: Run the complete unit/acceptance suite**

```bash
dotnet test RepoGlean.slnx
```

Expected: all tests pass with zero failures.

- [ ] **Step 6: Commit the application rename**

```bash
git add RepoGlean.slnx src tests
git commit -m "refactor: rename DevCleaner to RepoGlean"
```

### Task 3: Rename documentation, schema, smoke, and CI artifacts

**Files:**
- Modify: `README.md`
- Modify: `docs/configuration.schema.json`
- Modify: `eng/native-smoke.ps1`
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/release.yml`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: `RepoGlean.slnx`, `src/RepoGlean/RepoGlean.csproj`, and the `repoglean` executable identity.
- Produces: package archives named `repoglean-<rid>.tar.gz` and smoke/CI commands that exercise `repoglean`.

- [ ] **Step 1: Update current documentation and schema metadata**

Change the active README commands, filenames, configuration paths, product prose, project commands, and schema title/description to RepoGlean. Preserve all safety claims and platform limitations verbatim except for the product name.

- [ ] **Step 2: Update smoke and workflow paths**

Use these exact workflow identities:

```text
executable: RepoGlean or RepoGlean.exe
package_executable: repoglean or repoglean.exe
package directory: artifacts/package/repoglean-<rid>
archive: artifacts/repoglean-<rid>.tar.gz
artifact name: repoglean-<rid>
```

Point restore/build/test/publish commands at `RepoGlean.slnx` and `src/RepoGlean/RepoGlean.csproj`.

- [ ] **Step 3: Assert absence of the old active identity**

Run:

```bash
rg -n -i 'devcleaner' \
  --hidden \
  --glob '!.git' \
  --glob '!.superpowers/sdd/**' \
  --glob '!docs/superpowers/plans/**' \
  --glob '!docs/superpowers/specs/**'
```

Expected: no matches.

- [ ] **Step 4: Restore, build, and test**

Run:

```bash
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx --no-restore -warnaserror
dotnet test RepoGlean.slnx --no-build
```

Expected: restore succeeds, build has zero warnings/errors, and all tests pass.

- [ ] **Step 5: Commit infrastructure and documentation**

```bash
git add README.md docs/configuration.schema.json eng .github .gitignore
git commit -m "build: publish RepoGlean artifacts"
```

### Task 4: Verify packaged Native AOT executables

**Files:**
- Generated only: `artifacts/`

**Interfaces:**
- Consumes: `src/RepoGlean/RepoGlean.csproj` and `eng/native-smoke.ps1`.
- Produces: verified macOS Arm64 and x64 package-named Native AOT executables.

- [ ] **Step 1: Publish and smoke macOS Arm64**

```bash
dotnet restore src/RepoGlean/RepoGlean.csproj -r osx-arm64
dotnet publish src/RepoGlean/RepoGlean.csproj -c Release -r osx-arm64 \
  --self-contained --no-restore -p:PublishAot=true -p:Version=2.0.0 \
  -o artifacts/native/osx-arm64
mkdir -p artifacts/package/repoglean-osx-arm64
cp artifacts/native/osx-arm64/RepoGlean artifacts/package/repoglean-osx-arm64/repoglean
pwsh -NoProfile -File eng/native-smoke.ps1 \
  -ExecutablePath artifacts/package/repoglean-osx-arm64/repoglean
```

Expected: the packaged Arm64 smoke passes.

- [ ] **Step 2: Publish and smoke macOS x64 under Rosetta**

```bash
arch -x86_64 dotnet restore src/RepoGlean/RepoGlean.csproj -r osx-x64
arch -x86_64 dotnet publish src/RepoGlean/RepoGlean.csproj -c Release -r osx-x64 \
  --self-contained --no-restore -p:PublishAot=true -p:Version=2.0.0 \
  -o artifacts/native/osx-x64
mkdir -p artifacts/package/repoglean-osx-x64
cp artifacts/native/osx-x64/RepoGlean artifacts/package/repoglean-osx-x64/repoglean
arch -x86_64 pwsh -NoProfile -File eng/native-smoke.ps1 \
  -ExecutablePath artifacts/package/repoglean-osx-x64/repoglean
```

Expected: the packaged x64 smoke passes under Rosetta.

- [ ] **Step 3: Verify the worktree is clean except for ignored artifacts**

```bash
git status --short
```

Expected: no output.

### Task 5: Integrate, rename GitHub, and publish v2.0.0

**Files:**
- External state: GitHub repository, `master`, tag `v2.0.0`, release assets.

**Interfaces:**
- Consumes: the verified RepoGlean commits.
- Produces: `github.com/aczarkowski/RepoGlean` and its complete `v2.0.0` release.

- [ ] **Step 1: Review the full diff and rerun final acceptance**

```bash
git diff master...HEAD --check
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx --no-restore -warnaserror
dotnet test RepoGlean.slnx --no-build
```

Expected: no diff errors, clean build, and zero test failures.

- [ ] **Step 2: Fast-forward the verified branch into local master**

From the root checkout:

```bash
git pull --ff-only
git merge --ff-only codex/repoglean-rename
```

- [ ] **Step 3: Rename the GitHub repository**

Rename `aczarkowski/DevCleaner` to `aczarkowski/RepoGlean` using authenticated GitHub repository settings or API, then update the local `origin` URL to:

```text
git@github.com:aczarkowski/RepoGlean.git
```

Verify:

```bash
git remote -v
git ls-remote origin refs/heads/master refs/tags/v1
```

- [ ] **Step 4: Push verified master**

```bash
git push origin master
```

Wait for the complete CI matrix and require every job to pass.

- [ ] **Step 5: Create and push the annotated release tag**

```bash
git -c tag.gpgSign=false tag -a v2.0.0 -m "RepoGlean v2.0.0"
git push origin refs/tags/v2.0.0
```

- [ ] **Step 6: Verify the published release**

Require the GitHub release to contain exactly six `repoglean-<rid>.tar.gz` archives and their six adjacent `.sha256` files. Download at least the host archive, verify its checksum, extract it, and run:

```bash
repoglean --version
```

Expected:

```text
repoglean 2.0.0
```
