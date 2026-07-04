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
| `ASH015` | `and` used without a preceding `let recursive`                      |
| `ASH016` | Conflicting unqualified import selectors for the same name    |
| `ASH017` | Unsatisfied capability (residual top-level capability row is non-empty) |
| `ASH018` | Capability not permitted by a closed `needs` row, or a provider used at a non-monomorphizable generic instance |
| `ASH019` | Unknown capability or capability operation                    |
| `ASH020` | Invalid handler (bad arm, or a not-yet-supported form)        |
| `ASH025` | Renamed capability keyword (`effect`→`capability`, `uses`→`needs`) |
| `ASH026` | Duplicate or incomplete static provider (`provide`)            |
| `ASH027` | Capability satisfied by both a provider and an enclosing handler |
| `ASH021` | Disallowed form in an inline `module` block                   |
| `ASH022` | Inline module path collides with a file module of the same path |
| `ASH023` | Inline module named `Ashes` or shadowing a reserved `Ashes.*` path |
| `ASH024` | Duplicate inline module name in the same scope                |

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
  `let` and a `let recursive ... and ...` group that reuse a name). Each top-level binding
  name must be unique within the file.
  Message: `Duplicate top-level binding 'name'.`

- `ASH014` — **Forward reference.** A declaration refers to a binding that is
  declared later in the file. Top-level scoping is sequential (Model A): a binding is
  visible only to subsequent declarations and the trailing expression, not to earlier
  ones. Self-recursion requires `let recursive`, and mutual recursion requires a
  `let recursive ... and ...` group.
  Message: `Binding 'name' is not yet declared at this point.`

- `ASH015` — **`and` without `let recursive`.** An `and` clause appears without a preceding
  `let recursive`. Mutual recursion is written `let recursive X = ... and Y = ...`; a bare `and`
  (after a plain `let`, or with no preceding binding) is rejected.
  Message: `'and' requires a preceding 'let recursive'.`

- `ASH016` — **Conflicting unqualified import selectors.** Two unqualified selector
  imports (`import M.name`) bring the same unqualified name into scope. Disambiguate
  with `as`, for example `import M.name as m` and `import N.name as n`.
  Message: `Conflicting unqualified import selectors for 'name'.`

## Capability diagnostics

These codes cover the capability surface (`capability` declarations, `needs` rows,
`perform`, `handle ... with`). See [LANGUAGE_SPEC.md](LANGUAGE_SPEC.md) §20 for the
grammar and typing rules, and [future/FUTURE_FEATURES.md](future/FUTURE_FEATURES.md)
for the remaining roadmap.

- `ASH017` — **Unsatisfied capability.** The program's residual capability row at the top level is
  non-empty after default built-in handlers are applied: some code reachable from the
  entry expression performs a capability that no enclosing handler discharges. The span
  points at the first perform-site of the offending effect.
  Message: `Unhandled capability 'Capability': no enclosing handler discharges it.`

- `ASH018` — **Capability not permitted by a closed row.** A function whose written `uses`
  row is closed performs a capability (directly or by calling a capability-requiring function) that
  the row does not include.
  Message: `Capability 'Capability' is not permitted by the closed row needs {...}.`

- `ASH019` — **Unknown capability or operation.** A qualified reference names a declared
  capability but an operation it does not declare, a `needs` row mentions an undeclared
  capability, or `perform` is applied to something that is not a capability operation call.
  Messages: `Capability 'Capability' has no operation 'op'.`,
  `Unknown capability 'Capability' in needs row.`,
  `'perform' must be applied to a capability operation call.`

- `ASH020` — **Invalid handler.** A `handle` expression has a malformed arm (an arm for
  an unknown capability/operation, a duplicate arm, a duplicate `return` arm, or a missing
  operation for a handled capability), uses `resume` in an unsupported position (supported:
  tail position, let value, match scrutinee — exactly once per path), or has an arm path
  that never resumes (aborting arms need unwinding and are not supported).
  Messages include: `Handler arm 'Effect.op' does not name a declared effect operation.`,
  `Duplicate handler arm for 'Effect.op'.`,
  `Handler for capability 'Capability' must handle operation 'op'.`

- `ASH025` — **Renamed capability keyword.** The former spellings `effect` and `uses` were
  renamed to `capability` and `needs`; using an old spelling reports this with the replacement.
  Messages: `'effect' has been renamed to 'capability'.`, `'uses' has been renamed to 'needs'.`

- `ASH026` — **Duplicate or incomplete provider.** Two `provide` declarations target the same
  concrete capability instance, a provider supplies an operation more than once, or a provider is
  missing one of the capability's operations (a provider must supply all operations exactly once).
  Messages include: `Duplicate provider for 'Ord(Str)'.`, `Provider for 'Ord(Str)' is missing
  operation 'compare'.`

- `ASH027` — **Ambiguous capability satisfaction.** At a capability operation call, both a static
  `provide` for the concrete instance and an enclosing `handle` could satisfy it. There is no
  hidden precedence — choose one.
  Message: `Capability 'Clock' is satisfied both by a provider and by an enclosing handler. Choose one.`

## Inline module diagnostics

These cover inline (`module Name = ...`) declarations. Inline modules resolve through the
same path as file modules, so unknown-member, unknown-selector, and import-collision cases
reuse `ASH013`–`ASH016`. See [LANGUAGE_SPEC.md](LANGUAGE_SPEC.md) §13.1 for the surface.

- `ASH021` — **Disallowed form in an inline module.** A `module` block contains a trailing
  expression or an `external` declaration — neither is permitted (a module block is
  declarations only, and `external` is a file-level FFI concern that is never exported).
  Message: `Inline module 'Name' may not contain a trailing expression.` /
  `Inline module 'Name' may not contain an 'external' declaration.`

- `ASH022` — **Inline/file module collision.** An inline module and a project file resolve to
  the same module path (e.g. `module Vec` inside `Geom.ash` and a file `Geom/Vec.ash`). A
  module path must resolve to exactly one module.
  Message: `Module path 'Geom.Vec' is defined by both an inline module and a file.`

- `ASH023` — **Reserved inline module name.** An inline module is named `Ashes`, or its
  composed path shadows a reserved `Ashes.*` path.
  Message: `Inline module may not be named 'Ashes' (reserved for the standard library).`

- `ASH024` — **Duplicate inline module.** Two inline modules with the same name are declared in
  the same scope (the same file level, or the same enclosing module).
  Message: `Duplicate inline module 'Name' in this scope.`

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
