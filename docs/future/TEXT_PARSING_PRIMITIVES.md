# Text Parsing Primitives — Status & Roadmap

The minimal text parsing surface is now part of the language surface
through the builtin `Ashes.Text` module.

The current implementation includes Unicode-aware `uncons`, decimal
`parseInt`, decimal-and-exponent `parseFloat`, dedicated lowering and
LLVM backend support, user-facing documentation, backend smoke tests,
and end-to-end `.ash` coverage. The minimal compiler/runtime work
tracked in this document is now landed.

The key design constraint is:

- add the minimum surface needed for user-space parsers
- keep JSON itself out of the language
- avoid a large string API before the need is proven

------------------------------------------------------------------------

## Completed Work

| Area | What was done |
|------|---------------|
| **Language specification** | `docs/LANGUAGE_SPEC.md` now documents `Ashes.Text.uncons`, `Ashes.Text.parseInt`, and `Ashes.Text.parseFloat` and their runtime semantics. |
| **Standard library docs** | `docs/STANDARD_LIBRARY.md` now lists the shipped `Ashes.Text` surface for users. |
| **Builtin module shape** | `BuiltinRegistry` now registers `Ashes.Text` as a builtin runtime module with null `ResourceName` and members `uncons`, `parseInt`, and `parseFloat`. |
| **Type bindings and lowering** | `Lowering.cs` now binds `uncons : Str -> Maybe((Str, Str))`, `parseInt : Str -> Result(Str, Int)`, and `parseFloat : Str -> Result(Str, Float)` and lowers them to dedicated text IR instructions. |
| **IR surface** | `Ir.cs` now includes `TextUncons`, `TextParseInt`, and `TextParseFloat`, and `docs/IR_REFERENCE.md` documents them. |
| **LLVM backend implementation** | The LLVM backend now executes UTF-8 scalar splitting for `uncons`, decimal integer parsing with invalid-input and overflow errors, and decimal/exponent float parsing with invalid-input and range errors. |
| **Compiler/backend tests** | `Ashes.Tests` now covers registry shape, null-resource behavior, and Linux/Windows backend smoke tests for the new builtins. |
| **End-to-end coverage** | The `.ash` suite now includes `tests/text_*.ash` for empty/ascii/unicode `uncons`, integer parse success/failure/overflow, and float parse success/failure/range cases. |
| **Example coverage** | `examples/text_parsing_demo.ash` now demonstrates `uncons`, `parseInt`, and `parseFloat` together in ordinary Ashes code. |
| **Future-features status** | `docs/future/FUTURE_FEATURES.md` now treats the minimal `Ashes.Text` parsing surface as landed while leaving further text helpers deferred. |
| **Full verification rerun** | `Ashes.Tests`, `Ashes.Lsp.Tests`, the full `.ash` suite, and `dotnet format Ashes.slnx --verify-no-changes` were rerun after implementation and passed together. |

------------------------------------------------------------------------

## Why This Is Needed

Ashes already has most of the machinery required for recursive-descent
parsing:

- algebraic data types
- immutable lists
- recursion
- pattern matching
- `Result(E, A)` for recoverable parse errors

That is enough to model parser state and parsed values.

The missing piece is structural access to `Str`.

Today the public surface treats strings mostly as opaque values:

- concatenation with `+`
- equality / inequality
- text I/O through file, network, and console APIs

That is enough for many applications, but not for parsers. A parser needs
to consume input incrementally and inspect the next unit of text.

Without a primitive like `uncons`, implementing a JSON parser in pure
Ashes user code is awkward or impossible.

------------------------------------------------------------------------

## Goal

Enable full user-space text parsers, starting with JSON, by adding a very
small standard-library surface.

This should support:

- JSON objects
- JSON arrays
- JSON strings
- JSON numbers
- JSON booleans
- JSON `null`
- insignificant whitespace
- escape sequences inside strings

This proposal does not add:

- new syntax
- a built-in JSON value type
- a built-in JSON parser
- mutation
- parser combinators as a language feature

------------------------------------------------------------------------

## Landed Minimal Surface

The shipped module name is `Ashes.Text`.

### Landed V1 surface

```ash
Ashes.Text.uncons : Str -> Maybe((Str, Str))
Ashes.Text.parseInt : Str -> Result(Str, Int)
Ashes.Text.parseFloat : Str -> Result(Str, Float)
```

### `uncons`

`uncons` returns the next text element and the remaining suffix.

Conceptually:

- `uncons("")` => `None`
- `uncons("abc")` => `Some(("a", "bc"))`

The returned head should be a single-character `Str`.

For Unicode-aware behavior, the head should represent one Unicode scalar
value, not one raw UTF-8 byte.

### `parseInt`

`parseInt` converts a decimal string to an `Int`.

Current behavior:

- accepts an optional leading `-`
- otherwise parses decimal digits only
- returns `Error(...)` on invalid input
- returns `Error(...)` on overflow

This is not JSON-specific. It is a general utility that also helps
config parsing, CLI argument parsing, and small text protocols.

### `parseFloat`

`parseFloat` converts a string to `Float`.

For JSON support, it accepts the JSON-shaped number grammar used by the
current implementation:

- optional leading `-`
- integer part
- optional fractional part
- optional exponent using `e` or `E`

Examples that should parse:

- `0`
- `-1`
- `3.14`
- `1e3`
- `-2.5E-4`

Like `parseInt`, invalid input returns `Error(...)`.

------------------------------------------------------------------------

## Why This Surface Is Small Enough

Only `uncons` is a genuinely new structural primitive.

`parseInt` and `parseFloat` are practical numeric helpers that avoid
forcing every user-space parser to reimplement digit classification,
numeric accumulation, overflow handling, and float conversion.

This keeps the exposed API small while still making text parsing
realistic.

------------------------------------------------------------------------

## What Is Not Required

The following helpers may be convenient later, but they are not required
for the first milestone:

- `stripPrefix`
- `startsWith`
- `trim`
- `takeWhile`
- `dropWhile`
- `parseBool`

### Why `parseBool` is not needed

JSON booleans are fixed keywords: `true` and `false`.

Once `uncons` exists, user code can parse those as ordinary text tokens.
A dedicated boolean parser is convenience, not a prerequisite.

### Why `stripPrefix` is not needed

`stripPrefix` can be written in ordinary Ashes once `uncons` exists.
It may still be worth shipping later as a convenience helper, but it is
not a required primitive.

------------------------------------------------------------------------

## Why Not Add A Built-In JSON Library

JSON is important, but it should not be a compiler feature.

Adding JSON directly to the language would:

- expand the language surface for a problem that is library-shaped
- encourage other format-specific builtins
- blur the line between core language semantics and ordinary parsing code

A better boundary is:

- keep the language small
- add a minimal text-processing surface
- let JSON parsing live in normal Ashes libraries

This follows Ashes' general direction of explicit, composable, pure
building blocks.

------------------------------------------------------------------------

## What This Enables

With the three functions above, a JSON library can be implemented as
ordinary Ashes code.

A typical structure would be:

1. a small tokenizer or direct recursive-descent parser
2. a JSON AST such as:
   - `JObject(List((Str, Json)))`
   - `JArray(List(Json))`
   - `JString(Str)`
   - `JInt(Int)`
   - `JFloat(Float)`
   - `JBool(Bool)`
   - `JNull`
3. `Result(Str, (Json, Str))`-style parser functions that return the
   parsed value plus the remaining input

That approach requires no new syntax and fits the current language model
well.

------------------------------------------------------------------------

## Ordered Roadmap — Next Work Items

The minimal compiler/runtime surface tracked by this document is now
complete. Remaining work, if pursued, is follow-on library and
ergonomics work rather than missing core parsing primitives.

1. Prove the surface further with a full user-space JSON parser or a
   comparable parser library/example that exercises recursive parsing,
   whitespace handling, strings, numbers, booleans, and nested data.
2. Re-evaluate convenience helpers such as `stripPrefix`, `startsWith`,
   `trim`, `takeWhile`, `dropWhile`, and `parseBool` only after real
   library usage shows that `uncons`, `parseInt`, and `parseFloat` are
   insufficient.
3. Keep additional text APIs out of the compiler/runtime until the spec
   is updated first and there is concrete evidence that the landed
   minimal surface is not enough.

------------------------------------------------------------------------

## Explicit Non-Goals

This proposal does not include:

- parser combinator modules
- regex support
- mutable cursor APIs
- byte-oriented parsing APIs
- a general `Char` language type
- changes to pattern matching
- changes to the core type system

Those can be evaluated separately if real users need them.

------------------------------------------------------------------------

## Explicitly Deferred

The following items are not implemented yet and should not be described
as landed:

- A built-in JSON parser or JSON value type.
- Parser-combinator or regex modules.
- Mutable cursor APIs, byte-oriented parsing APIs, or a general `Char`
   language type.
- Additional text convenience helpers beyond `uncons`, `parseInt`, and
   `parseFloat` unless real library usage justifies them.
- Repackaging `Ashes.Text` as a shipped `.ash` module; the current
   surface remains a builtin runtime module.