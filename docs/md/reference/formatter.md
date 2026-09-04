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

- A file is a sequence of imports, an optional export declaration, then top-level declarations, then an optional
  trailing expression (see [Language Reference](language.md) §1.1).
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
- An `export` declaration uses one item per line, indented 4 spaces. Every item receives a trailing
  comma, and the closing `)` is unindented.

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
- When the first element of a non-empty list literal starts on a new line, the formatter preserves a
  multiline list: every element appears on its own line, indented one level; commas follow every
  element except the last; and the closing bracket aligns with the list expression's indentation.
- Cons expressions use spaces around `::`.
- Subexpressions are parenthesized when needed to preserve meaning.

Example:

```ash
[
    first,
    second,
    third
]
```

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
- A transparent alias stays on one line: `type alias Name(a) = Target(a)`.
- A zero-cost nominal type stays on one line without `|`:
  `type Name(a) = Constructor(Payload(a))`. A following `deriving` clause is indented one level on
  its own line, as for an ordinary type declaration.

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

External resources

- An external resource type stays on one line:
  `external type Handle resource destructor closeHandle`.
- External parameter ownership markers use one space before their type:
  `external inspect(borrow Handle) -> Int` and
  `external transfer(consume Handle) -> void`.
- An external capability classification follows the return type as a canonical `needs` row:
  `external readConfig(Str) -> Str needs {FileRead}`. Capability names retain source order;
  the empty row is `needs {}`.

Records

- Record construction is rendered as a constructor call with named arguments:
  `Point(x = 1, y = 2)`. Fields keep their source order.
- Record update is rendered brace-free: `p with x = 5`, with comma-separated
  fields for multiple updates: `p with x = 5, y = 6`. Parentheses are added only
  where required by the surrounding precedence (`with` binds looser than
  application and the binary operators).
- `with` takes every following `name = value` pair as one of its own fields, so a
  record-literal field value, a multiline call argument, or a multiline list element
  whose unparenthesized right edge is a record update (the update itself, or the
  trailing body of a `let`, lambda, `if`, `match`, or `handle` ending in one) is
  parenthesized as a whole *when another field, argument, or element follows it*:
  `Value(state = (inner with x = 1), temp = temp)`,
  `Value(state = (given (c) -> c with x = 1), temp = temp)`. The last field in the
  list needs no such protection — its own closing bracket already ends the update
  unambiguously: `Value(temp = temp, state = inner with x = 1)`. An operand whose
  right edge is already closed (a call, a bracket, a pipeline whose last stage is a
  parenthesized lambda) keeps its usual form regardless of position. Inline call
  arguments, tuple and list elements, match scrutinees, and an update's own field
  values are parenthesized whenever they contain an update anywhere, independent of
  position (`f((p with x = 1))`, `p with a = (q with b = 1), c = 2`,
  `match (p with x = 1) with`).

Spacing

- Binary operators use spaces around the operator.
- Comparison operators use spaces around the operator.
- Function calls keep their existing canonical form: `f(x)` for parenthesized calls and `f x` for whitespace application.
- An inline parenthesized argument list remains inline. When the first written argument starts on a
  new line, the formatter preserves a multiline parenthesized call: every argument appears on its own
  line, indented one level; commas follow every argument except the last; and the closing parenthesis
  aligns with the call expression's indentation. This applies recursively to nested calls and does not
  change the language's curried application semantics.
- The same layout rule applies to the named arguments of record construction.
- No line ends in trailing whitespace.

Example:

```ash
expectUnsupportedDerivedField(
    "Callback",
    "Eq",
    fieldType,
    []
)
```

Comments

- The leading `//` comment block at the top of a file (before the first import or
  declaration) is preserved verbatim, in place.
- Standalone `//` comment lines elsewhere in the file are preserved: each is
  re-anchored to the surrounding significant lines (by a whitespace-insensitive
  token signature) and reinserted at the anchor's position in the formatted
  output. A comment whose anchor line was merged into a longer line (a
  multi-line definition the formatter collapses onto one line keeps its first
  line's tokens as that line's head and its last line's tokens as its tail) is
  matched against the merged line; a comment whose anchor line no longer exists
  at all is placed after the nearest preceding anchor rather than dropped.
- Trailing same-line comments (`let x = 1 // note`) are not yet preserved; the
  reinsertion is line-based. Keep comments on their own line.
- A single `fmt` call is idempotent: formatting internally repeats the
  parse/format/reinsert pass (capped) until the output stops changing, so a
  comment's anchor is always resolved against the code's own final, stable
  shape rather than an intermediate wrapping. Running `fmt` again on already
  formatted output is always a no-op.

## Enforcement

- Exact-output formatter tests lock down representative policy rules.
- Idempotence tests ensure formatting is stable across repeated runs.
- Repository `.ash` files are expected to be formatted with `ashes fmt` before changes are considered complete.
