# Text Parsing Primitives — Status & Roadmap

The minimal text parsing surface is now part of the language surface
through the builtin `Ashes.Text` module.

The current implementation includes Unicode-aware `uncons`, decimal
`parseInt`, decimal-and-exponent `parseFloat`, dedicated lowering and
LLVM backend support, user-facing documentation, backend smoke tests,
end-to-end `.ash` coverage, and a user-space JSON parser smoke test.
The minimal compiler/runtime work tracked in this document is now
landed.

The landed scope intentionally stays narrow: add the minimum surface
needed for user-space parsers, keep JSON itself out of the language,
and avoid a large string API before the need is proven.

---

## Completed Work

All original text parsing roadmap items have been completed:

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
| **User-space parser proof** | `tests/text_json_parser_smoke.ash` now exercises `Ashes.Text` through a recursive user-space JSON parser covering whitespace, strings, numbers, booleans, null, arrays, objects, and nested data. |
| **Example coverage** | `examples/text_parsing_demo.ash` now demonstrates `uncons`, `parseInt`, and `parseFloat` together in ordinary Ashes code. |
| **Future-features status** | `docs/future/FUTURE_FEATURES.md` now treats the minimal `Ashes.Text` parsing surface as landed while leaving further text helpers deferred. |
| **Full verification rerun** | `Ashes.Tests`, `Ashes.Lsp.Tests`, the full `.ash` suite, and `dotnet format Ashes.slnx --verify-no-changes` were rerun after implementation and passed together. |


Remaining work is outside this completed base milestone: a built-in
JSON parser or JSON value type, parser-combinator or regex modules,
mutable cursor APIs, byte-oriented parsing APIs, a general `Char`
language type, additional convenience helpers beyond `uncons`,
`parseInt`, and `parseFloat` unless real library usage justifies them,
and repackaging `Ashes.Text` as a shipped `.ash` module rather than a
builtin runtime module.
