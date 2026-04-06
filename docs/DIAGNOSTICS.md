# Diagnostics Reference

Ashes diagnostics carry a stable code in addition to message text and source span.

This currently applies to structured parser and semantic diagnostics emitted via
the compiler diagnostics pipeline. Project-loading, module-resolution, and some
CLI/TestRunner compile failures are still surfaced as uncoded compile errors.

Current codes:

| Code | Meaning |
|------|---------|
| `ASH001` | Unknown identifier |
| `ASH002` | Generic type mismatch |
| `ASH003` | Parse error |
| `ASH004` | Match branch type mismatch |
| `ASH005` | List element type mismatch |
| `ASH006` | Use-after-drop (using a resource after it has been closed) |
| `ASH007` | Double-drop (closing a resource that has already been closed) |

Codes are intended to stay stable even if diagnostic wording is improved over time.

Currently uncoded compile failures include examples such as:

- `Could not resolve module 'Foo' ...`
- `Ambiguous module resolution for 'Foo' ...`
- `Import name collision for imported binding 'x' ...`
- `Import module qualifier collision for 'X' ...`

These are user-facing and tested, but they do not yet carry stable `ASH###`
codes.

Message style rules:

- Use sentence case.
- End user-facing compiler diagnostics with a period.
- Use `expects` for arity mismatches and `requires` for operator/type constraints.
- Prefer `Non-exhaustive match expression. Missing case: ...` for missing match coverage.
- Prefer code-based assertions plus key substrings in tests when full wording is not the thing under test.