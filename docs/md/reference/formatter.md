# Formatter Specification

This document defines the canonical formatting policy for Ashes source.

The formatter is expected to apply these rules consistently. Changes to formatter behavior should update this document and the formatter tests together.

## Canonical Rules

Indentation

- Use 4 spaces per indentation level.
- Do not emit tabs in canonical repository formatting.

Line endings

- Canonical repository formatting uses `\n` line endings.
- Formatter tests normalize line endings for cross-platform stability.
- Programmatic callers may request a different newline style explicitly, but repo-owned `.ash` files should remain canonical `\n`.

Top-level declarations

- A file is a sequence of imports, then top-level declarations, then an optional
  trailing expression (see [LANGUAGE_SPEC.md](language.md) §1.1).
- Exactly one blank line separates adjacent top-level declarations, and one blank
  line separates the last declaration from the trailing expression.
- The block of `import` lines at the top of the file is not blank-line separated
  internally; a single blank line separates the import block from the first
  declaration.
- Each `import` is preserved in its written form, including its shape and any
  alias. Whole-module imports render as `import M` (optionally `import M as X`);
  selector imports keep the selected name as `import M.binding` (optionally
  `import M.binding as x`). The selector's `.binding` is never rewritten into a
  module alias, and an `as` alias is never dropped.
- Top-level `let` / `let recursive` declarations have no trailing `in`; they are formatted
  like a `let` binding without the `in` line.

Example:

```ash
import Ashes.IO

let name = "world"

let greeting = "hello " + name

Ashes.IO.print(greeting)
```

`let recursive ... and ...` groups

- The `let recursive` binding starts the group. Each `and` clause starts its own line at
  the same indentation as `let recursive` (no blank line between members of the group).
- Each binding's value follows the same multiline rules as any `let` binding.

Example:

```ash
let recursive even = given (n) -> if n == 0 then true else odd(n - 1)
and odd = given (n) -> if n == 0 then false else even(n - 1)
```

`let ... in ...`

- `let` starts the binding line.
- Multiline values are indented one level.
- `in` starts its own line when either the value or body is multiline.
- Nested `let ... in` expressions are preserved as written; the formatter does not
  flatten a nested `let ... in` pyramid into top-level declarations, nor the reverse.

Example:

```ash
let x =
    let y = 1
    in y
in x
```

Lambdas

- `given (...) ->` stays on one line when the body fits on one line.
- Multiline lambda bodies are indented one level after the arrow.

`if / then / else`

- `if`, `then`, and `else` each start their own line in canonical multiline layout.
- Multiline branches are indented one level.

Example:

```ash
if cond
then
    expr1
else
    expr2
```

Lists and cons

- Short list literals remain compact on one line.
- Cons expressions use spaces around `::`.
- Subexpressions are parenthesized when needed to preserve meaning.

`match`

- `match <expr> with` stays on one line.
- Each arm starts on its own line.
- Nested multiline expressions inside an arm are indented one level.
- Pattern guards are formatted inline: `| pattern when condition -> expr`

Example:

```ash
match xs with
    | [] -> 0
    | head :: tail ->
        match tail with
            | [] -> head
            | _ -> head
```

Example with pattern guard:

```ash
match x with
    | n when n >= 10 -> "big"
    | _ -> "small"
```

Type declarations

- `type Name =` starts the declaration.
- Each constructor appears on its own line, indented one level, prefixed with `|`.

Example:

```ash
type Color =
    | Red
    | Green
    | Blue
```

Record types use the same one-field-per-line `|` layout, with each field
rendered as `| name: Type`:

```ash
type Point =
    | x: Int
    | y: Int
```

Records

- Record construction is rendered as a constructor call with named arguments:
  `Point(x = 1, y = 2)`. Fields keep their source order.
- Record update is rendered brace-free: `p with x = 5`, with comma-separated
  fields for multiple updates: `p with x = 5, y = 6`. Parentheses are added only
  where required by the surrounding precedence (`with` binds looser than
  application and the binary operators).

Spacing

- Binary operators use spaces around the operator.
- Comparison operators use spaces around the operator.
- Function calls keep their existing canonical form: `f(x)` for parenthesized calls and `f x` for whitespace application.

## Enforcement

- Exact-output formatter tests lock down representative policy rules.
- Idempotence tests ensure formatting is stable across repeated runs.
- Repository `.ash` files are expected to be formatted with `ashes fmt` before changes are considered complete.