# Diagnostics Reference

Ashes diagnostics carry a stable code in addition to message text and source span.

Current codes:

| Code | Meaning |
|------|---------|
| `ASH001` | Unknown identifier |
| `ASH002` | Generic type mismatch |
| `ASH003` | Parse error |
| `ASH004` | Match branch type mismatch |
| `ASH005` | List element type mismatch |

Codes are intended to stay stable even if diagnostic wording is improved over time.

Message style rules:

- Use sentence case.
- End user-facing compiler diagnostics with a period.
- Use `expects` for arity mismatches and `requires` for operator/type constraints.
- Prefer `Non-exhaustive match expression. Missing case: ...` for missing match coverage.
- Prefer code-based assertions plus key substrings in tests when full wording is not the thing under test.