# Self-Hosting: Building the Ashes Compiler in Ashes

This document is a **reference analysis** of what the Ashes language would
need in order to rewrite the current C# compiler in Ashes itself. It
captures the full gap analysis between the language as it exists today and
the requirements of the compiler codebase, so the information is available
if the project ever pursues self-hosting.

Nothing here is planned or committed — it is purely informational.

------------------------------------------------------------------------

## What the C# Compiler Does Today

The compiler is roughly 10 000 lines across these phases:

| Component               | Lines  | Role                                          |
|-------------------------|--------|-----------------------------------------------|
| Lexer                   | ~320   | Hand-written character-at-a-time tokenizer     |
| Parser                  | ~885   | Recursive descent, backtracking, desugaring    |
| Lowering / Semantics    | ~5 500 | Type inference (HM), ownership, closure analysis, IR emission |
| LLVM Codegen            | ~1 600 | IR → LLVM IR → machine code                   |
| ELF / PE Linkers        | ~300   | Custom image linkers (x64, ARM64, Windows)     |
| CLI / Project Support   | ~600   | Orchestration, imports, test runner            |

Key internal data structures: mutable dictionaries for type substitution
and scopes, mutable lists for diagnostics and IR, mutable counters for
fresh IDs, byte arrays for executable image construction.

------------------------------------------------------------------------

## Capability Gaps

### 1. Foreign Function Interface (FFI)

The compiler calls ~100+ LLVM-C API functions via P/Invoke. Self-hosting
requires:

- A way to **declare and call external C functions** from Ashes source.
- **Opaque pointer / handle types** wrapping native pointers returned by
  LLVM.
- Ability to pass **null-terminated C strings** to foreign functions (the
  runtime already adds terminators internally for syscalls, but this is
  not exposed to user code).
- The same FFI would cover GDB/DWARF debug-info APIs (LLVM DIBuilder).

### 2. Byte Type and Bitwise Operations

The ELF/PE linkers and parts of codegen manipulate raw bytes:

- **Byte type** (`u8`) for constructing executable images.
- **Bitwise operators** — AND, OR, XOR, shift left / right — for
  encoding headers, relocations, and instruction operands.
- **Unsigned integers** — ELF/PE formats use unsigned 16/32/64-bit
  values for virtual addresses, section sizes, and offsets.
- **Byte arrays / buffers** — mutable or builder-pattern sequences for
  assembling the output image (the linkers currently build `byte[]` and
  `Span<byte>`).
- **Integer-to-byte conversion** — little-endian encoding of multi-byte
  integers (`BinaryPrimitives.WriteInt32LittleEndian`, etc.).

### 3. Mutable or Persistent Data Structures

This is a fundamental tension with Ashes' purity model. The C# compiler
relies heavily on:

- **Mutable dictionaries** — type substitution table
  (`Dictionary<int, TypeRef>`), scope stacks, string intern tables,
  ownership tracking maps.
- **Mutable lists** — diagnostic accumulation, IR instruction lists.
- **Mutable counters** — fresh type-variable IDs, temp-variable IDs,
  label generation.

Possible approaches in a pure language:

- **Persistent / immutable maps and sequences** (balanced-tree maps,
  finger trees). Performance may be a concern for type inference with
  hundreds of unification steps.
- **State threading** — pass state explicitly through every function
  (monadic style). Doable but verbose without do-notation or similar
  sugar.
- **Linear types with in-place mutation** — if the ownership system
  evolved to allow unique/linear references with in-place update, this
  would be the most natural fit.

### 4. String Manipulation Primitives

The compiler does extensive string work that Ashes cannot currently
express:

- **Character access by index** — the lexer reads `_text[_pos]`
  character by character.
- **Substring extraction** — `_text[start..end]` for token text.
- **Character classification** — `isLetter`, `isDigit`,
  `isWhiteSpace`.
- **String-to-number parsing** — `toInt`, `toFloat` for literal
  parsing.
- **Number-to-string conversion** — `fromInt`, `fromFloat` for
  diagnostics and `print`.
- **String interpolation or formatting** — for error messages.

An `Ashes.String` module (already listed in FUTURE_FEATURES.md) would
need at minimum: `charAt`, `substring`, `length`, `toInt`, `toFloat`,
`fromInt`, `fromFloat`, `startsWith`, `contains`, `split`, `trim`,
`indexOf`, and character-predicate helpers.

### 5. Type Annotations

Ashes currently has no syntax for writing type signatures. The compiler
uses ~15 type representations (`TInt`, `TFloat`, `TStr`, `TBool`,
`TList`, `TTuple`, `TFun`, `TVar`, `TNamedType`, `TNever`, …) and
`TypeScheme` with quantified variables.

- Without annotations the compiler cannot verify that ADT constructors
  and pattern matches are correctly typed across module boundaries —
  especially in a 10 000+ line codebase.
- Function signatures are essential for documentation and API contracts
  at this scale.

### 6. Map / Dictionary Data Structure

Dictionaries are pervasive in the compiler:

- `_subst: Dictionary<int, TypeRef>` — the core of type inference.
- `_scopes: Stack<Dictionary<string, Binding>>` — name resolution.
- `_ownershipScopes` — ownership tracking.
- `_stringIntern` — string literal deduplication.
- Symbol tables for imports, module aliases, constructor lookup.

Ashes would need either:

- A built-in `Map(K, V)` type (immutable, balanced-tree based).
- Or enough low-level primitives (hashing, arrays) to implement one.

### 7. Arrays / Indexed Collections

Lists are immutable linked lists with O(n) access. The compiler needs
O(1) indexed access for:

- Lexer character access (`_text[_pos]`).
- Byte-buffer construction in linkers.
- IR instruction arrays.
- Object-file section parsing (reading bytes at specific offsets).

An `Array(T)` or `Bytes` type with O(1) indexed read would be
necessary, at minimum for the lexer and linker.

### 8. Records / Named Product Types

The C# compiler uses records extensively:

- `Token(Kind, Text, IntValue, FloatValue, Position, Length)`
- `DiagnosticEntry(Span, Message, Code)`
- `IrInst` with ~50 variants, each carrying different fields.
- `Binding` with 7 variants.

Ashes has tuples (unnamed) and ADTs (tagged unions). What is missing:

- **Named fields** — positional access into a 6-tuple is error-prone at
  scale.
- **Record update syntax** — `{ token with Position = newPos }` is
  heavily used in the C# compiler.

Without records, single-constructor ADTs can approximate the same thing,
but field access remains positional and fragile.

### 9. Module System Enhancements

The compiler is organized as ~20 interdependent C# files/classes. Self-
hosting would need:

- Modules that can **reference each other's types** (AST types are used
  by parser, lowering, codegen, etc.).
- A way to **share type definitions** across modules without circular
  imports.
- Potentially a **project-level compilation** model (compile all modules
  together rather than one at a time).

### 10. Error Propagation Mechanism

The C# compiler uses:

- `throw new CompileDiagnosticException(errors)` to abort on errors.
- `try/catch` in the CLI layer for graceful reporting.
- A `Diagnostics` class that accumulates errors then throws.

Ashes has `Result(E, A)` and `let?` for short-circuiting, which could
work, but:

- You would need to thread `Result` through the entire pipeline.
- Or have some form of early-return / exception mechanism.
- `panic` exists but is a hard abort, not catchable.

### 11. Binary File I/O

- `Ashes.File.readText` and `writeText` exist but handle only UTF-8
  text.
- Writing executable images requires **`writeBytes`** for raw binary
  output.
- **Directory traversal** — the test runner and project mode scan
  directories for `.ash` files.
- **File-path manipulation** — joining paths, extracting filenames and
  extensions.
- **Process execution** — the test runner compiles then executes the
  output binary.
- **Environment variables** — for finding LLVM libraries, config.
- **Exit codes** — `Ashes.IO.args` exists but there is no way to set
  the exit code.

### 12. Real Memory Management

The current runtime uses a 4 MB bump allocator with no freeing. A
compiler processing non-trivial source files would:

- Allocate thousands of AST nodes, IR instructions, type variables,
  scope entries, and string literals.
- Easily exceed 4 MB when self-compiling 10 000+ lines of Ashes.
- Need either a **garbage collector** or a **real ownership-based
  freeing system** (the `Drop` instructions exist in IR but are
  currently no-ops).

### 13. Large ADT Ergonomics

The IR has types like `IrInst` with ~50 constructors, each carrying
different payloads. Ashes can express this structurally, but:

- The exhaustiveness checker would need to handle 50+ constructors
  efficiently.
- Pattern matching on deeply nested ADTs needs to be ergonomic.

### 14. Numeric Conversions and Formatting

- `Int → Str` and `Str → Int` conversions (for parsing integer
  literals, generating error messages with line numbers).
- `Float → Str` and `Str → Float` (for float-literal parsing).
- Hex formatting (ELF/PE headers use hex addresses).

------------------------------------------------------------------------

## Summary Table

| # | Capability                          | Why Needed                          | Difficulty    |
|---|-------------------------------------|-------------------------------------|---------------|
| 1 | FFI / C Interop                     | LLVM API, syscalls                  | High          |
| 2 | Byte type + bitwise ops             | Linker, binary formats              | Medium        |
| 3 | Mutable or persistent maps          | Type inference, scopes              | High          |
| 4 | String manipulation                 | Lexer, error messages               | Medium        |
| 5 | Type annotations                    | Codebase maintainability            | Medium        |
| 6 | Dictionary / Map type               | Pervasive in compiler               | Medium–High   |
| 7 | Arrays / indexed access             | Lexer, linker, codegen              | Medium        |
| 8 | Records / named fields              | Readability at scale                | Medium        |
| 9 | Module system enhancements          | Cross-module types                  | Medium        |
| 10 | Error propagation                  | Compiler error handling             | Low–Medium    |
| 11 | Binary file I/O                    | Writing executables                 | Low–Medium    |
| 12 | Real memory management             | Compiling large programs            | High          |
| 13 | Large ADT ergonomics               | 50+ variant IR type                 | Low           |
| 14 | Numeric conversions                | Parsing, diagnostics                | Low           |

------------------------------------------------------------------------

## The Three Hardest Problems

1. **FFI** — without it there is no way to call LLVM, so no code
   generation at all.
2. **Mutable / persistent data structures** — without them, type
   inference (the heart of the compiler) is impractical to implement.
3. **Real memory management** — without it the compiler cannot compile
   itself; the 4 MB bump allocator is insufficient for a 10 000-line
   input.

Everything else is incremental library and language work that follows
naturally once those three foundations exist.
