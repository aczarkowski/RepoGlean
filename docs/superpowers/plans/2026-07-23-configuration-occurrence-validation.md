# Configuration Occurrence Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reject every invalid recognized case-variant occurrence while preserving effective-last behavior when all occurrences are valid.

**Architecture:** Add a BCL-only `JsonElement` validation pass before source-generated deserialization. It walks all recognized root and custom-rule member occurrences, enforces the same per-value contract as the published schema, and then lets the existing deserializer and aggregate typed validator select and validate the effective last configuration.

**Tech Stack:** .NET 10, System.Text.Json source generation, xUnit, JsonSchema.Net in tests only.

## Global Constraints

- Case-insensitive recognized duplicate keys remain allowed.
- The last valid recognized value is effective, but every recognized occurrence must be valid.
- Unknown root and custom-rule properties remain accepted.
- JsonSchema.Net remains test-only and the production path remains Native AOT compatible.

---

### Task 1: Prove shadowed invalid occurrences fail

**Files:**
- Modify: `tests/DevCleaner.Tests/Support/ConfigurationContractSamples.cs`
- Modify: `tests/DevCleaner.Tests/Configuration/ConfigLoaderTests.cs`

**Interfaces:**
- Consumes: `ConfigurationContractSamples.All` and `ConfigLoader.Load(string?)`.
- Produces: shared schema/executable/loader regressions and one focused nested-member loader regression.

- [ ] **Step 1: Add shared invalid-then-valid samples**

Add samples equivalent to:

```csharp
{
    "invalid schemaVersion occurrence followed by valid",
    """{"SCHEMAVERSION":2,"schemaVersion":1}""",
    false
},
{
    "invalid customRules occurrence followed by valid",
    """{"schemaVersion":1,"CUSTOMRULES":[{"id":"company.invalid","category":"Build","patterns":["**/invalid"],"preselected":true}],"customRules":[{"id":"company.valid","category":"Build","patterns":["**/valid"]}]}""",
    false
},
```

- [ ] **Step 2: Add a direct nested-member regression**

Add `Load_rejects_an_invalid_shadowed_custom_rule_member_occurrence` with one rule containing `"CATEGORY":"Logs","category":"Build"`; assert load failure and a category diagnostic.

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~Acceptance"
```

Expected: the new loader/executable assertions fail because System.Text.Json currently accepts the valid last occurrence; schema assertions already report invalid.

### Task 2: Validate every recognized occurrence

**Files:**
- Modify: `src/DevCleaner/Configuration/ConfigLoader.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: raw JSON text, `ArtifactRule.IsValidId(string)`, `ArtifactCategory`, and `IsRepositoryRelativePattern(string)`.
- Produces: `ValidateRecognizedOccurrences(string json, out string error)` plus private root/member validation helpers.

- [ ] **Step 1: Replace the narrow required-category check**

Call `ValidateRecognizedOccurrences(json, out propertyError)` before `JsonSerializer.Deserialize`. Parse once, require an object root as before, enumerate every property, and dispatch case-insensitively for `schemaVersion`, `roots`, `excludes`, `disabledRules`, and `customRules`.

- [ ] **Step 2: Validate root occurrences**

Enforce `schemaVersion` numeric value 1; nullable string-array shape for roots/excludes/disabledRules; and nullable array-of-object shape for customRules. Visit every recognized occurrence rather than returning after the first match.

- [ ] **Step 3: Validate every custom-rule member occurrence**

For each rule object, require at least one case-insensitive `id`, `category`, and `patterns`. Validate every recognized occurrence: valid rule ID strings; defined named category strings; non-empty safe string arrays for patterns; nullable non-whitespace string arrays for markers; and only Boolean `false` for preselected. Ignore unknown members.

- [ ] **Step 4: Preserve typed effective-last and aggregate validation**

Do not change source-generated deserialization, normalization, duplicate rule ID detection, or built-in ID collision detection. Valid duplicate occurrences must still deserialize to the last value.

- [ ] **Step 5: Update README wording**

State: “When recognized properties are repeated with different casing, every occurrence must be valid and the last valid value is effective.”

- [ ] **Step 6: Run focused GREEN**

Run the combined focused command from Task 1. Expected: zero failures, including existing effective-last assertions.

### Task 3: Verify and deliver

**Files:**
- Modify: `.superpowers/sdd/task-6-report.md` (ignored evidence report, after commit)

**Interfaces:**
- Consumes: completed implementation and repository release gates.
- Produces: committed parity fix and appended RED/GREEN evidence.

- [ ] **Step 1: Run release gates**

```bash
dotnet restore DevCleaner.slnx
dotnet build DevCleaner.slnx --no-restore -warnaserror
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --no-build --filter "FullyQualifiedName~Acceptance"
dotnet test tests/DevCleaner.Tests/DevCleaner.Tests.csproj --no-build --filter "FullyQualifiedName~Configuration"
dotnet test DevCleaner.slnx --no-build
dotnet publish src/DevCleaner/DevCleaner.csproj -c Release -r osx-arm64 --self-contained -p:PublishAot=true
jq empty docs/configuration.schema.json
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.12 .github/workflows/ci.yml .github/workflows/release.yml
```

Expected: every command exits 0, all tests pass, and build output has zero warnings/errors.

- [ ] **Step 2: Smoke the native executable**

Run `--version`, validate a configuration whose recognized duplicate occurrences are all valid, and verify a shadowed-invalid configuration exits 2.

- [ ] **Step 3: Review and commit**

Run `git diff --check`, inspect the complete diff, then commit tracked implementation/test/docs changes with `fix: validate every configuration occurrence`.

- [ ] **Step 4: Append report**

Append the exact commit hash, focused RED/GREEN counts, full gate results, native smoke results, decision, and remaining concerns to `.superpowers/sdd/task-6-report.md`.
