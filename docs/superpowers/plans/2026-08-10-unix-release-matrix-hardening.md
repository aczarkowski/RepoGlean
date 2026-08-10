# Unix Release Matrix Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct secure Unix audit traversal on Linux ARM64 and macOS x64, then run normal CI's full validation sequence on all six release platforms.

**Architecture:** Keep `UnixSecureAuditEntry`'s handle-relative traversal unchanged while making its native constants and entry points architecture-aware through small internal helpers that production code and tests share. Extend the existing CI matrix directly with the three release targets it currently omits.

**Tech Stack:** .NET 10, C#, xUnit, P/Invoke, PowerShell, GitHub Actions YAML, Native AOT

## Global Constraints

- Preserve handle-relative, no-follow, identity-checked, fail-closed audit traversal.
- Support exactly the shipped x64 and ARM64 Unix architectures.
- Do not change audit eligibility, cleanup authority, release archives, version metadata, or release publishing behavior.
- Normal CI must run restore, warning-as-error build, tests, Native AOT publish, packaging, and smoke tests on all six release platforms.
- The CI runner/RID/executable/package/smoke-argument tuples must match `.github/workflows/release.yml`.

---

### Task 1: Correct architecture-specific Unix interop

**Files:**
- Modify: `tests/RepoGlean.Tests/Auditing/SecureAuditFileSystemTests.cs:87-95`
- Modify: `src/RepoGlean/Auditing/SecureAuditFileSystem.cs:245-250,649-725`

**Interfaces:**
- Consumes: `System.Runtime.InteropServices.Architecture` and `RuntimeInformation.ProcessArchitecture`.
- Produces: `UnixSecureAuditEntry.LinuxGenericOpenFlags(Architecture)`, `UnixSecureAuditEntry.LinuxDirectoryOpenFlags(Architecture)`, and `UnixSecureAuditEntry.MacUsesInode64ReadDir(Architecture)`.

- [ ] **Step 1: Write failing architecture-contract tests**

Add tests that assert:

```csharp
Assert.Equal(0x0800 | 0x20000 | 0x80000, UnixSecureAuditEntry.LinuxGenericOpenFlags(Architecture.X64));
Assert.Equal(0x0800 | 0x20000 | 0x80000 | 0x10000, UnixSecureAuditEntry.LinuxDirectoryOpenFlags(Architecture.X64));
Assert.Equal(0x0800 | 0x8000 | 0x80000, UnixSecureAuditEntry.LinuxGenericOpenFlags(Architecture.Arm64));
Assert.Equal(0x0800 | 0x8000 | 0x80000 | 0x4000, UnixSecureAuditEntry.LinuxDirectoryOpenFlags(Architecture.Arm64));
Assert.Throws<PlatformNotSupportedException>(() => UnixSecureAuditEntry.LinuxGenericOpenFlags(Architecture.Arm));
Assert.True(UnixSecureAuditEntry.MacUsesInode64ReadDir(Architecture.X64));
Assert.False(UnixSecureAuditEntry.MacUsesInode64ReadDir(Architecture.Arm64));
Assert.Throws<PlatformNotSupportedException>(() => UnixSecureAuditEntry.MacUsesInode64ReadDir(Architecture.Arm));
```

- [ ] **Step 2: Run the focused tests and verify RED**

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj -c Release --filter 'FullyQualifiedName~Linux_open_flags_match_each_supported_architecture_abi|FullyQualifiedName~Mac_directory_enumeration_selects_the_inode64_symbol_only_on_x64'
```

Expected: compilation fails because all three helpers are absent.

- [ ] **Step 3: Implement Linux flag mapping**

Implement the tested methods with exact x64 and ARM64 values and `PlatformNotSupportedException` for other architectures. Route the Linux branches of `GenericOpenFlags()` and `DirectoryOpenFlags()` through them using `RuntimeInformation.ProcessArchitecture`; retain the current macOS values.

- [ ] **Step 4: Implement macOS `readdir` dispatch**

Implement `MacUsesInode64ReadDir`. Keep the existing `readdir` P/Invoke for Linux and macOS ARM64, add:

```csharp
[DllImport("libc", EntryPoint = "readdir$INODE64", SetLastError = true)]
private static extern IntPtr ReadDirMacX64(IntPtr directory);
```

Route enumeration through a wrapper that uses `ReadDirMacX64` only when running on macOS x64 and uses `ReadDir` otherwise.

- [ ] **Step 5: Run focused secure-audit tests and verify GREEN**

```bash
dotnet test tests/RepoGlean.Tests/RepoGlean.Tests.csproj -c Release --filter 'FullyQualifiedName~SecureAuditFileSystemTests|FullyQualifiedName~RepositoryAuditorTests'
```

Expected: all selected tests pass with no warnings or failures.

- [ ] **Step 6: Reproduce Linux ARM64 in an ARM64 container**

Run an audit fixture under `mcr.microsoft.com/dotnet/sdk:10.0` with `--platform linux/arm64`, a read-only repository mount, and an ephemeral project copy. Expected: successful audit containing `audit-state`, not exit code 3 with `EINVAL`.

- [ ] **Step 7: Commit the repair**

```bash
git add src/RepoGlean/Auditing/SecureAuditFileSystem.cs tests/RepoGlean.Tests/Auditing/SecureAuditFileSystemTests.cs
git commit -m "fix: support secure audit traversal across Unix architectures"
```

### Task 2: Expand normal CI to all release platforms

**Files:**
- Modify: `.github/workflows/ci.yml:18-35`
- Reference: `.github/workflows/release.yml:18-48`

**Interfaces:**
- Consumes: the six release matrix tuples in `.github/workflows/release.yml`.
- Produces: the identical six tuples in CI while retaining every existing CI step.

- [ ] **Step 1: Verify the current matrix is incomplete**

```bash
for rid in win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64; do rg -q "rid: $rid" .github/workflows/ci.yml || echo "missing $rid"; done
```

Expected: `win-arm64`, `osx-x64`, and `linux-arm64` are missing.

- [ ] **Step 2: Add the missing release tuples**

Add `windows-11-arm`/`win-arm64`, `macos-15-intel`/`osx-x64`, and `ubuntu-24.04-arm`/`linux-arm64` with the release workflow's executable, package executable, and smoke arguments. Change the ARM64 macOS runner alias from `macos-latest` to the release workflow's exact `macos-15` value.

- [ ] **Step 3: Verify all six entries exist**

```bash
for rid in win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64; do rg -q "rid: $rid" .github/workflows/ci.yml || exit 1; done
test "$(rg -c '^          - runner:' .github/workflows/ci.yml)" -eq 6
```

Expected: exit code 0 and exactly six runner entries.

- [ ] **Step 4: Compare CI and release tuples**

Inspect both matrix blocks and verify each runner, RID, executable, package executable, and smoke argument matches. Leave the build, test, publish, package, and smoke steps unchanged.

- [ ] **Step 5: Commit the CI expansion**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: validate the full release platform matrix"
```

### Task 3: Complete local acceptance

**Files:**
- Verify: `src/RepoGlean/Auditing/SecureAuditFileSystem.cs`
- Verify: `tests/RepoGlean.Tests/Auditing/SecureAuditFileSystemTests.cs`
- Verify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: fresh local acceptance evidence; remote six-platform CI remains unverified until push.

- [ ] **Step 1: Restore and build with warnings as errors**

```bash
dotnet restore RepoGlean.slnx
dotnet build RepoGlean.slnx -c Release --no-restore -warnaserror
```

Expected: zero warnings and zero errors.

- [ ] **Step 2: Run all tests**

```bash
dotnet test RepoGlean.slnx -c Release --no-build
```

Expected: zero failures and zero skipped tests.

- [ ] **Step 3: Verify formatting and patch hygiene**

```bash
dotnet format RepoGlean.slnx --verify-no-changes --no-restore
git diff --check
```

Expected: both commands exit 0.

- [ ] **Step 4: Run a fresh macOS ARM64 Native AOT smoke**

Publish to a fresh temporary directory with `dotnet publish -c Release -r osx-arm64 --self-contained --no-restore -p:PublishAot=true`, prepare the package, and run `eng/native-smoke.ps1` against it. Expected: `Native packaged-executable smoke PASS`.

- [ ] **Step 5: Review final state**

```bash
git diff HEAD~2 --check
git diff HEAD~2 --stat
git status --short --branch
```

Expected: only the agreed code, tests, and CI workflow changed after the plan commit; the branch is clean and ahead of its remote.
