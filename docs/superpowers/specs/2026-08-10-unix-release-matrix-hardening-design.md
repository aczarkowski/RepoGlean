# Unix Release Matrix Hardening Design

## Goal

Make secure audit traversal correct on every supported Unix release architecture and run the normal CI workflow on the same six-platform matrix as the release workflow so architecture-specific failures are detected before tagging.

## Failure Evidence

The `v2.3.0` release run compiled and packaged all targets, but its packaged-executable smoke test failed on two platforms:

- `linux-arm64` passed x64 Linux values for `O_DIRECTORY` and `O_NOFOLLOW`. The ARM64 kernel interpreted those values as incompatible flags and rejected the audit-root `open` call with `EINVAL`.
- `osx-x64` called the unsuffixed `readdir` symbol while decoding the 64-bit inode `dirent` layout. This corrupted directory-entry names and caused handle-relative inspection to fail with `ENOENT`.

Normal CI did not expose either failure because it covered only `win-x64`, `osx-arm64`, and `linux-x64`.

## Scope

This change will:

1. Select Linux `O_DIRECTORY` and `O_NOFOLLOW` values by process architecture for the supported x64 and ARM64 targets.
2. Select `readdir$INODE64` on macOS x64 and `readdir` on macOS ARM64, matching the existing architecture dispatch for `fstat` and `fstatat`.
3. Add unit-level contract tests for the architecture mappings.
4. Expand `.github/workflows/ci.yml` to run its existing complete sequence on `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and `linux-arm64`, using the same runner, executable, package name, and Linux smoke arguments as `.github/workflows/release.yml`.

This change will not alter audit eligibility, cleanup authority, release archive contents, version metadata, or the release workflow's publishing behavior.

## Implementation Design

`UnixSecureAuditEntry` will keep the current handle-relative, no-follow traversal model. Small internal helpers will map a supplied `Architecture` to the Linux directory and no-follow constants and to the macOS directory-read ABI. Production calls will pass `RuntimeInformation.ProcessArchitecture` to those helpers.

Linux x64 retains the current constants. Linux ARM64 uses the AArch64 ABI constants. Unsupported architectures fail closed instead of silently borrowing x64 values.

macOS enumeration will use separate P/Invoke declarations for `readdir` and `readdir$INODE64`. A single dispatch helper will call the ARM64 or x64 declaration and fail closed for unsupported architectures. Enumeration semantics, cancellation, descriptor ownership, and error propagation remain unchanged.

The CI workflow will add the three missing matrix entries without refactoring workflow ownership. Keeping this repair local avoids coupling release artifact publication to a new reusable-workflow abstraction. The CI and release matrices must contain the same six runner/RID/executable/smoke-argument combinations after the change.

## Testing

Development follows red-green testing:

- A Linux flag-contract test will assert the exact x64 and ARM64 values and rejection of unsupported architectures.
- A macOS ABI-contract test will assert the x64 `INODE64` and ARM64 unsuffixed selection and rejection of unsupported architectures.
- Existing secure filesystem tests will continue exercising real root opening, enumeration, link refusal, metadata, cancellation, and descriptor ownership on the host platform.
- The full solution build and test suite, formatting verification, and diff checks must pass locally.
- A fresh macOS ARM64 Native AOT package smoke must pass locally.
- Remote CI is the authoritative validation for the complete six-platform matrix, especially Linux ARM64 and macOS x64.

## Success Criteria

- The Linux ARM64 audit smoke no longer returns `EINVAL` while opening the repository root.
- The macOS x64 audit smoke enumerates intact entry names and no longer returns spurious `ENOENT` warnings.
- All six normal CI matrix jobs execute the full restore, warning-as-error build, test, Native AOT publish, package, and smoke sequence.
- The release workflow remains unchanged unless verification finds a direct inconsistency required to preserve the agreed matrix.
