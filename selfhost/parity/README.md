# Self-hosted parity fixtures

Parity fixtures compare versioned, serialized public results from the permanent stage-0 compiler and
the self-hosted implementation. Each implementation reads the same source and expected-output files;
neither implementation calls into the other. Fixture formats belong here as their corresponding
self-hosted phases become stable.

## Token streams

Token fixtures live in `frontend/tokens` as matching `<name>.source` and `<name>.tokens` files. The
`ashes-token-v1` format starts with its schema marker, then records one token per LF-terminated line.
Each record has these tab-separated fields:

1. token-kind name;
2. lowercase hex of the token text's UTF-8 bytes;
3. decoded integer payload;
4. decoded float payload;
5. token start as a UTF-8 byte offset;
6. token length in UTF-8 bytes.

UTF-8 hex keeps embedded tabs, newlines, carriage returns, and quotes from changing the record shape.
For a float token, the payload field preserves the token spelling when parsing that spelling produces
the decoded payload. This avoids host-specific float rendering while still exposing a lexer that
decoded its spelling incorrectly. Non-float tokens serialize their zero float payload as `0`.

The shared fixtures currently cover every keyword, every operator and delimiter, decoded numeric,
string, and rune payloads, comments, and Unicode identifiers and byte spans. Malformed input belongs
to the structured-diagnostic parity format below rather than the token-only format.

## Diagnostics

Diagnostic fixtures live in `frontend/diagnostics` as matching `<name>.source` and `<name>.diagnostics`
files. The `ashes-diagnostic-v1` format starts with its schema marker, then a `recovered-items\t<N>`
line — the number of top-level items a program parse still recovered despite any diagnostics, proving
recovery continued past an error rather than aborting the parse — then one LF-terminated record per
diagnostic, in collection order. Each record has these tab-separated fields:

1. the diagnostic's stable code, or `-` when it has none;
2. lowercase hex of the diagnostic message's UTF-8 bytes;
3. the diagnostic span's start as a UTF-8 byte offset;
4. the diagnostic span's length in UTF-8 bytes.

The shared fixtures cover representative lexer diagnostics (unterminated strings, invalid and
out-of-range numeric literals, unexpected characters) and parser diagnostics (missing expressions and
patterns, a trailing token past the end of a program, a refutable let-pattern, `and` without a
preceding `let recursive`, and a constructor-less type declaration).

## Lowered IR

Lowered IR fixtures live in `semantics/lowered-ir` as matching `<name>.source` and `<name>.ir` files.
Each `.source` file is an Ashes program, and its corresponding `.ir` file contains the normalized lowered
IR dump produced by `IrTextFormatter` (or `formatIr`).

The serialized IR output format includes:
1. Header: `IR (lowered)` and separator line.
2. Trait evidence table (if present in the program).
3. Sequence of functions (helper functions followed by program entry function `_start_main`), where each function
   specifies its label, origins/attributes, local and temporary register counts, followed by 2-space indented
   labels and 4-space indented instructions with opcode, operands, and optional source location annotations.

The shared fixtures cover arithmetic, let bindings, closures and captures, pattern matching with user ADTs,
and mutual recursion.
