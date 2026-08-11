# C# Braces Enforcement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enforce braces for every C# control-flow statement and migrate all production and test code to comply.

**Architecture:** A root `.editorconfig` declares the SDK-provided `IDE0011` rule as an error for every C# file. A root `Directory.Build.props` enables code-style analysis in ordinary builds, making analyzer output the authoritative migration worklist and preventing regressions without adding dependencies.

**Tech Stack:** .NET 10 SDK, Roslyn code-style analyzers, EditorConfig, xUnit

## Global Constraints

- Apply the rule to every `*.cs` file under both `src` and `tests`.
- Preserve statement order, conditions, and control flow.
- Do not add third-party analyzer dependencies.
- Do not include unrelated formatting or refactoring.

---

### Task 1: Enforce and satisfy the braces rule

**Files:**
- Create: `.editorconfig`
- Create: `Directory.Build.props`
- Modify: every existing `*.cs` file reported by `IDE0011` after the two configuration files are added; this diagnostic output is the exact and authoritative file list

**Interfaces:**
- Consumes: the .NET SDK's built-in code-style analyzer configuration
- Produces: build-time `IDE0011` enforcement across all repository C# code

- [ ] **Step 1: Add the failing repository rule**

Create `.editorconfig`:

```ini
root = true

[*.cs]
csharp_prefer_braces = true:error
dotnet_diagnostic.IDE0011.severity = error
```

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Run the analyzer to verify the rule fails on existing violations**

Run: `dotnet build RepoGlean.slnx --configuration Release --warnaserror`

Expected: non-zero exit with one or more `IDE0011` errors in production and/or test C# files.

- [ ] **Step 3: Apply the minimal mechanical migration**

Run: `dotnet format RepoGlean.slnx style --diagnostics IDE0011 --no-restore`

Inspect the diff and retain only brace additions around the statements reported by `IDE0011`. Do not rewrite conditions, reorder statements, or make unrelated formatting changes.

- [ ] **Step 4: Verify analyzer and behavior checks**

Run:

```bash
dotnet build RepoGlean.slnx --configuration Release --warnaserror
dotnet test RepoGlean.slnx --configuration Release --no-build
dotnet format RepoGlean.slnx --verify-no-changes --no-restore
git diff --check
```

Expected: all commands exit zero; test output reports zero failed tests; build and format output contain no diagnostics.

- [ ] **Step 5: Review and commit the migration**

Run `git diff --stat`, `git diff -- .editorconfig Directory.Build.props src tests`, and `git status --short`. Confirm every C# change is a brace-only rewrite, then commit:

```bash
git add .editorconfig Directory.Build.props src tests
git commit -m "style: require braces in C# code"
```
