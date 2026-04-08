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

`let ... in ...`

- `let` starts the binding line.
- Multiline values are indented one level.
- `in` starts its own line when either the value or body is multiline.

Example:

```ash
let x =
    let y = 1
    in y
in x
```

Lambdas

- `fun (...) ->` stays on one line when the body fits on one line.
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

Spacing

- Binary operators use spaces around the operator.
- Comparison operators use spaces around the operator.
- Function calls keep their existing canonical form: `f(x)` for parenthesized calls and `f x` for whitespace application.

## Enforcement

- Exact-output formatter tests lock down representative policy rules.
- Idempotence tests ensure formatting is stable across repeated runs.
- Repository `.ash` files are expected to be formatted with `ashes fmt` before changes are considered complete.