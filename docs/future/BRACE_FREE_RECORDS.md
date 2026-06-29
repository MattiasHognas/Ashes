# Brace-Free Records

> **Status: implemented.** The brace-free syntax below is live and the old
> curly-brace forms have been removed (all three rollout phases completed at
> once: both forms were never shipped together — the parser now accepts only the
> brace-free forms and reports a migration diagnostic on the old `{ ... }` forms).
> The normative description lives in [LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md) §4.1,
> [FORMATTER_SPEC.md](../FORMATTER_SPEC.md), and [DIAGNOSTICS.md](../DIAGNOSTICS.md).
> Decisions taken on the open questions: named arguments are accepted for record
> construction only (not arbitrary function calls); `with` supports a
> comma-separated field list and left-associative chaining
> (`p with x = 1 with y = 2`); record patterns remain positional for now.

This document proposes a record syntax that avoids curly braces and aligns
record declarations and usage with the existing ADT/call style.

------------------------------------------------------------------------

## Proposed Syntax

### Record declaration

Instead of:

`type Point = { x: Int, y: Int }`

use:

`type Point = | x: Int | y: Int`

This keeps record declarations in the `type ... = | ...` style.

### Record construction

Instead of:

`let p = Point { x = 1, y = 2 }`

use:

`let p = Point(x = 1, y = 2)`

This uses constructor call syntax with named arguments.

### Record update

Instead of:

`let p2 = { p with x = 5 }`

use:

`let p2 = p with x = 5`

Parenthesized form should remain valid when needed:

`let p2 = (p with x = 5)`

------------------------------------------------------------------------

## Design Goals

- Avoid introducing curly braces in Ashes source syntax.
- Keep records visually and semantically close to ordinary ADTs.
- Preserve immutability and current record-update semantics.
- Keep field access unchanged (`p.x`).

------------------------------------------------------------------------

## Compatibility Direction

- Keep existing curly-brace record syntax working temporarily behind a
  compatibility period.
- Prefer formatting and docs to emit the new brace-free syntax.
- Remove old syntax only after parser, formatter, tests, and examples are
  migrated.

------------------------------------------------------------------------

## Implementation Steps

1. **Grammar and parser updates**
   - Extend `type` parsing to recognize field-style alternatives inside
     `|` branches (`| name: Type`).
   - Add named call arguments in `(...)` for constructor/function calls.
   - Add `with` expression parsing without braces (`expr with field = expr`).
   - Define precedence/associativity for `with` relative to calls, pipes,
     and binary operators.

2. **AST representation**
   - Introduce an explicit named-argument AST node/shape for calls.
   - Keep record internals desugared to the same single-constructor ADT model
     used today.
   - Represent bare `with` updates directly or desugar them to the existing
     record-update node.

3. **Semantic lowering and typing**
   - Validate named-argument usage (unknown, missing, duplicate fields).
   - Reorder named construction arguments to declared field order before
     constructor application.
   - Preserve existing type-check behavior and diagnostics for record update.

4. **Formatter and pretty-printing**
   - Emit new syntax for record declarations, construction, and updates.
   - Keep stable formatting for multiline named arguments and chained `with`
     updates.
   - If compatibility mode exists, normalize old syntax to the new syntax.

5. **Language/docs updates**
   - Update `docs/LANGUAGE_SPEC.md` and `docs/STANDARD_LIBRARY.md` examples.
   - Add migration notes from curly-brace syntax to brace-free syntax.

6. **Tests**
   - Parser tests: declaration, construction, update, precedence,
     parenthesization, and diagnostics.
   - Semantics tests: typing/lowering parity with current record behavior.
   - Formatter tests: canonical output and round-trip stability.
   - End-to-end `.ash` tests covering the new syntax in real programs.

7. **Rollout**
   - Phase 1: accept both old and new syntax; emit new syntax.
   - Phase 2: warn on old syntax.
   - Phase 3: remove old syntax after migration is complete.

------------------------------------------------------------------------

## Open Decisions

- Should named arguments be allowed only for constructors, or also regular
  function calls?
- Should `p with x = 1 with y = 2` be valid directly, and if so what is
  its associativity?
- Should record pattern syntax gain a named form in addition to positional
  constructor patterns?
