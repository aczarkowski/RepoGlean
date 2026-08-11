# C# Braces Enforcement Design

## Goal

Require braces around every C# control-flow statement throughout the repository, including production and test code. A normal repository build or formatting verification must reject future violations.

## Configuration

Add a root `.editorconfig` that applies the built-in C# braces preference to every `*.cs` file and reports `IDE0011` as an error. Enable SDK code-style analysis during builds so the editor rule is also enforced outside an IDE without adding a third-party analyzer dependency.

## Code Migration

Use the SDK analyzer output as the authoritative list of violations. Add braces to each reported statement in `src` and `tests`, preserving the statement order, conditions, and control flow. Do not combine this mechanical migration with unrelated formatting or refactoring.

## Verification

The migration is accepted when all of the following succeed from a clean invocation:

- `dotnet build RepoGlean.slnx --configuration Release --warnaserror`
- `dotnet test RepoGlean.slnx --configuration Release --no-build`
- `dotnet format RepoGlean.slnx --verify-no-changes --no-restore`
- `git diff --check`

The build and format checks prove the rule is enforced and the current tree contains no violations. The existing test suite checks that the brace-only edits did not change behavior.
