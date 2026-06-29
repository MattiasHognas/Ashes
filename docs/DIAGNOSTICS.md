# Diagnostics Reference

Ashes diagnostics carry a stable code in addition to message text and source span.

This currently applies to structured parser and semantic diagnostics emitted via
the compiler diagnostics pipeline. Project-loading, module-resolution, and some
CLI/TestRunner compile failures are still surfaced as uncoded compile errors.

Current codes:

| Code     | Meaning                                                       |
| -------- | ------------------------------------------------------------- |
| `ASH001` | Unknown identifier                                            |
| `ASH002` | Generic type mismatch                                         |
| `ASH003` | Parse error                                                   |
| `ASH004` | Match branch type mismatch                                    |
| `ASH005` | List element type mismatch                                    |
| `ASH006` | Use-after-drop (using a resource after it has been closed)    |
| `ASH007` | Double-drop (closing a resource that has already been closed) |
| `ASH013` | Duplicate top-level binding name                              |
| `ASH014` | Reference to a binding not yet declared (forward reference)   |
| `ASH015` | `and` used without a preceding `let rec`                      |
| `ASH016` | Conflicting unqualified import selectors for the same name    |

Codes are intended to stay stable even if diagnostic wording is improved over time.
Codes `ASH008`–`ASH009` are reserved for future resource-lifecycle diagnostics.

`ASH010`–`ASH012` were previously allocated for an `async`-block enforcement model
(`await`/networking outside `async`, async error-type conflict). The language no
longer has an `async` keyword — `async` is a builtin (`Ashes.Async.task`), and
async-only safety is enforced by the `Task` type — so those codes were never
emitted and have been retired. The numbers are unused and free for reuse.

## Top-level declaration and import diagnostics

These codes cover the flat top-level declaration form (`import* declaration* expr?`)
and the binding/type import selectors. See
[LANGUAGE_SPEC.md](LANGUAGE_SPEC.md) for the full grammar and scoping rules.

- `ASH013` — **Duplicate top-level binding name.** Two top-level declarations bind
  the same name in the same file (for example two `let x = ...` declarations, or a
  `let` and a `let rec ... and ...` group that reuse a name). Each top-level binding
  name must be unique within the file.
  Message: `Duplicate top-level binding 'name'.`

- `ASH014` — **Forward reference.** A declaration refers to a binding that is
  declared later in the file. Top-level scoping is sequential (Model A): a binding is
  visible only to subsequent declarations and the trailing expression, not to earlier
  ones. Self-recursion requires `let rec`, and mutual recursion requires a
  `let rec ... and ...` group.
  Message: `Binding 'name' is not yet declared at this point.`

- `ASH015` — **`and` without `let rec`.** An `and` clause appears without a preceding
  `let rec`. Mutual recursion is written `let rec X = ... and Y = ...`; a bare `and`
  (after a plain `let`, or with no preceding binding) is rejected.
  Message: `'and' requires a preceding 'let rec'.`

- `ASH016` — **Conflicting unqualified import selectors.** Two unqualified selector
  imports (`import M.name`) bring the same unqualified name into scope. Disambiguate
  with `as`, for example `import M.name as m` and `import N.name as n`.
  Message: `Conflicting unqualified import selectors for 'name'.`

## Record diagnostics

Records use the brace-free syntax described in
[LANGUAGE_SPEC.md](LANGUAGE_SPEC.md) §4.1. These diagnostics are currently
surfaced as parse errors (`ASH003`) or uncoded semantic errors:

- **Removed curly-brace record syntax.** The old `{ ... }` record declaration,
  construction, and update forms are no longer accepted. Encountering a `{`
  where a record declaration, literal, or update was previously written reports
  a parse error directing the author to the brace-free forms
  (`type T = | f: T`, `T(f = e)`, `e with f = e`).
  Messages: `Brace record declarations have been removed; use '| field: Type' alternatives.`,
  `Brace record construction has been removed; use 'Name(field = value)'.`,
  `Brace record update has been removed; use 'base with field = value'.`

- **Named arguments outside record construction.** Named-argument call syntax
  (`f(x = 1)`) is only valid for record construction. Using it on an arbitrary
  expression is a parse error.
  Message: `Named arguments are only allowed in record construction.`

- **Mixed record and constructor branches.** A `type` declaration mixes
  `| field: Type` field branches with `| Constructor(...)` branches.
  Message: `Record field alternatives cannot be mixed with constructor alternatives.`

The semantic record diagnostics (unknown record type, missing/unknown/duplicate
field) remain uncoded.

Currently uncoded compile failures include examples such as:

- `Non-exhaustive match expression. Missing case: ...`
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
