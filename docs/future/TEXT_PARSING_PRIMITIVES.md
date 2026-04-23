# Text Parsing Primitives

This document proposes a small standard-library addition that makes it
possible to implement a full JSON parser in ordinary Ashes code without
adding JSON as a language feature.

The key design constraint is:

- add the minimum surface needed for user-space parsers
- keep JSON itself out of the language
- avoid a large string API before the need is proven

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

## Recommended Minimal Surface

This document uses `Ashes.Text` as the working module name.

If the project prefers `Ashes.String` to match the current future-features
naming, the API shape should remain the same.

### Required V1 surface

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

Recommended behavior:

- accepts an optional leading `-`
- otherwise parses decimal digits only
- returns `Error(...)` on invalid input
- returns `Error(...)` on overflow

This is not JSON-specific. It is a general utility that also helps
config parsing, CLI argument parsing, and small text protocols.

### `parseFloat`

`parseFloat` converts a string to `Float`.

For JSON support, it should accept at least the JSON number grammar:

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

Like `parseInt`, invalid input should return `Error(...)`.

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

## Suggested Rollout

### 1. Add the text module surface

Add the minimal shipped module API:

- `uncons`
- `parseInt`
- `parseFloat`

Update:

- `docs/LANGUAGE_SPEC.md`
- `docs/STANDARD_LIBRARY.md`

### 2. Implement and test the helpers

Implementation may live behind normal compiler-shipped builtins or a
small runtime helper layer, but it should remain exposed as ordinary
module functions.

Tests should cover:

- empty and non-empty `uncons`
- Unicode handling for `uncons`
- valid and invalid integer parses
- valid and invalid float parses
- overflow and malformed input behavior

### 3. Add end-to-end examples

Add examples or tests proving that user-space parsing is now feasible.

A JSON parser example is the best validation target because it exercises:

- punctuation handling
- recursion
- booleans
- numbers
- strings
- whitespace
- nested structures

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

## Recommendation

Adopt the smallest practical V1:

```ash
Ashes.Text.uncons : Str -> Maybe((Str, Str))
Ashes.Text.parseInt : Str -> Result(Str, Int)
Ashes.Text.parseFloat : Str -> Result(Str, Float)
```

That is enough to unlock a full JSON parser in user code while keeping
the language itself free of JSON-specific features.