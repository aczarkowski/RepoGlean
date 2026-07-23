# Configuration Occurrence Validation Design

## Goal

Keep case-insensitive recognized configuration keys forward-compatible with JSON Schema behavior: duplicate case variants are allowed, the last valid value is effective, and every recognized occurrence must independently satisfy the published structural and semantic contract.

## Design

Before deserialization, `ConfigLoader` parses the document once and walks every property whose name case-insensitively matches `schemaVersion`, `roots`, `excludes`, `disabledRules`, or `customRules`. Each occurrence is validated directly as a `JsonElement` against the corresponding v1 contract. Every object in every `customRules` occurrence is also checked across every case-insensitive occurrence of `id`, `category`, `patterns`, `markers`, and `preselected`, including required-member presence.

This validator uses only BCL JSON APIs and existing rule/category/path helpers. JsonSchema.Net remains test-only. After all occurrences pass, the existing source-generated System.Text.Json deserialization keeps its effective-last behavior, normalization runs, and aggregate checks such as unique IDs and built-in collisions remain in the existing typed validator.

## Error handling

The loader rejects the first invalid recognized occurrence with a field-specific configuration error. Unknown properties remain accepted. Comments and trailing commas remain accepted. Null continues to be allowed only for the collection fields for which the schema documents null normalization.

## Tests

The shared schema/loader/executable matrix adds invalid-then-valid duplicates for `schemaVersion` and `customRules`; both must remain invalid because the schema evaluates every matching property. A direct loader regression covers a shadowed invalid custom-rule member followed by a valid case variant. Existing tests continue to prove that valid duplicate occurrences are accepted and the last value is effective.

Acceptance requires focused configuration and executable tests, the full solution suite, warning-free build, macOS arm64 Native AOT publish and smoke, schema parse/evaluation, and actionlint.
