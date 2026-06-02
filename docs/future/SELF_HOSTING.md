# Self-Hosting: Building the Ashes Compiler in Ashes

This document is a **reference analysis** of what the Ashes language would
need in order to rewrite the current C# compiler in Ashes itself. It
captures the full gap analysis between the language as it exists today and
the requirements of the compiler codebase, so the information is available
if the project ever pursues self-hosting.

Nothing here is planned or committed — it is purely informational.

------------------------------------------------------------------------

## What the C# Compiler Does Today

The compiler is on the order of 30 000+ lines across these phases (the
codebase has grown well beyond the original ~10 000-line estimate):

| Component               | Lines   | Role                                          |
|-------------------------|---------|-----------------------------------------------|
| Lexer                   | ~320    | Hand-written character-at-a-time tokenizer     |
| Parser                  | ~885    | Recursive descent, backtracking, desugaring    |
| Lowering / Semantics    | ~10 900 | Type inference (HM), ownership, closure analysis, IR emission |
| LLVM Codegen + backend  | ~16 700 | IR → LLVM IR → machine code, linkers, runtime  |
| CLI entry point         | ~1 400  | Orchestration, imports, test runner            |

The ELF/PE image linkers (x64, ARM64, Windows) live inside the backend
project alongside codegen.

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
- Ability to pass **null-terminated C strings** to foreign functions.
- The same FFI would cover GDB/DWARF debug-info APIs (LLVM DIBuilder).

Note: the *runtime* has since grown substantial native interop (async
TCP/HTTP and the hermetic `rustls` TLS runtime on `linux-x64`,
`linux-arm64`, and `win-x64`), so the underlying native-interop
machinery exists.

Current status: a first user-facing `extern` surface now exists. Ashes
source can declare top-level C ABI functions (`extern strlen(Str) -> Int`),
declare opaque native handle types (`extern type LLVMModuleRef`), call
extern functions directly, and pass `Str` arguments as null-terminated C
strings. Dynamic imports can be requested for non-built-in symbols via the
symbol override form (`extern foo(Int) -> Int = "foo@libfoo.so"` on Linux, or
`extern tick() -> Int = "GetTickCount64@KERNEL32.DLL"` on Windows).
Extern declarations also support unsigned C integer widths (`u8`, `u16`,
`u32`, `u64`), `void` returns, and native pointer signatures (`*T`, including
nested `**T` out parameters), which map to Ashes `Int` call-site values,
Ashes `Unit` results, and pointer-sized values respectively.

The remaining FFI work is no longer on the immediate byte/linker path and
is tracked in the later hard-foundations phase below.

### 2. Byte Type and Bitwise Operations

The ELF/PE linkers and parts of codegen manipulate raw bytes:

- **Byte type** (`u8`) for constructing executable images.
- **Bitwise operators** — AND, OR, XOR, shift left / right — for
  encoding headers, relocations, and instruction operands. `Int` bitwise
  `&`, `|`, `^`, `<<`, and `>>` have landed; byte/unsigned-specific
  operators and unary bitwise NOT remain.
- **Unsigned integers** — ELF/PE formats use unsigned 16/32/64-bit
  values for virtual addresses, section sizes, and offsets.
- **Byte arrays / buffers** — mutable or builder-pattern sequences for
  assembling the output image (the linkers currently build `byte[]` and
  `Span<byte>`).
- **Integer-to-byte conversion** — little-endian encoding of multi-byte
  integers (`BinaryPrimitives.WriteInt32LittleEndian`, etc.).

### 3. Persistent (Immutable) Data Structures

The C# compiler relies heavily on mutable state, which Ashes does not
expose. **Ashes is committed to immutability: all values are immutable
and there is no user-visible mutation** (see Ground Rule #5 in
`FUTURE_FEATURES.md`). The mutable structures below must therefore be
expressed with persistent, immutable equivalents — not by adding
user-visible mutation to the language.

The C# compiler currently uses:

- **Mutable dictionaries** — type substitution table
  (`Dictionary<int, TypeRef>`), scope stacks, string intern tables,
  ownership tracking maps.
- **Mutable lists** — diagnostic accumulation, IR instruction lists.
- **Mutable counters** — fresh type-variable IDs, temp-variable IDs,
  label generation.

Approaches compatible with the immutability commitment:

- **Persistent / immutable maps and sequences** (balanced-tree maps,
  finger trees). This is the primary direction. Performance may be a
  concern for type inference with hundreds of unification steps.
- **State threading** — pass state explicitly through every function
  (monadic style). Doable but verbose without do-notation or similar
  sugar; usable as a fallback for counters and accumulators.
- **Compiler-internal uniqueness / arena reuse** — in-place reuse that
  stays entirely invisible to user code (the runtime arena already does
  bump-and-reset under a pure surface). This is an implementation
  optimisation only and must never surface as user-visible mutation.

### 4. String Manipulation Primitives

The compiler does extensive string work. **Some of this has already
landed**: the builtin `Ashes.Text` module ships `uncons` (Unicode-aware
character-by-character traversal), `parseInt` (`Str → Int`), `parseFloat`
(`Str → Float`), `fromInt`, `fromFloat`, and `toHex` — see
`docs/future/TEXT_PARSING_PRIMITIVES.md`. `uncons` covers the lexer's
sequential `_text[_pos]` traversal, the parse helpers cover literal
parsing, and the format helpers cover diagnostic number rendering.

Already landed:

- **Sequential character access** — `Ashes.Text.uncons` (note: O(n)
  sequential, not O(1) indexing — see Gap #7).
- **String-to-number parsing** — `Ashes.Text.parseInt`,
  `Ashes.Text.parseFloat`.
- **Number-to-string formatting** — `Ashes.Text.fromInt`,
  `Ashes.Text.fromFloat`, `Ashes.Text.toHex`.

The `Ashes.String` module has now landed with `substring`, `length`,
`indexOf`, `startsWith`, `contains`, `split`, `trim`, and character-predicate
helpers (`isLetter`, `isDigit`, `isWhiteSpace`).

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

Current status: a shipped `Ashes.Map` module now provides a persistent AVL-tree
map value type with immutable `set`/`insert`, `get`, `contains`, `size`,
`foldLeft`, `toList`, and `fromList`. Because Ashes does not yet have
typeclasses or a built-in ordering interface, callers currently provide a total
ordering function `(K -> K -> Int)` to lookup/update operations.

### 7. Arrays / Indexed Collections

Lists are immutable linked lists with O(n) access. The compiler needs
O(1) (or O(log n)) indexed access for:

- Lexer character access (`_text[_pos]`) — partly mitigated by
  `Ashes.Text.uncons`, which is sequential O(n), not random-access.
- Byte-buffer construction in linkers.
- IR instruction arrays.
- Object-file section parsing (reading bytes at specific offsets).

An immutable `Array(T)` or `Bytes` type with O(1)/O(log n) indexed read
would be necessary, at minimum for the lexer and linker. As with Gap #3,
these must be persistent/immutable values — any in-place reuse stays
internal to the implementation and invisible to user code.

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

**This gap is substantially smaller than originally framed.** The
runtime is no longer a fixed 4 MB bump allocator with no freeing. It is
now a **chunked arena that grows on demand** — each chunk is 4 MB and new
chunks are allocated from the OS (`mmap` / `VirtualAlloc`) as needed, so
there is no hard 4 MB ceiling (see `docs/ARCHITECTURE.md` § Memory
Model). Reclamation also exists, but at **scope granularity**: ownership
scopes save/restore an arena watermark, and on scope exit
`RestoreArenaState` resets the cursor while `ReclaimArenaChunks` releases
abandoned chunks (`munmap` / `VirtualFree`).

What remains is therefore not "add any freeing at all", but a question
of **granularity and steady-state footprint** for a long-running
self-compile:

- Reclamation is coarse (whole-scope arena reset), not per-object;
  `Drop` is a no-op for most owned heap values.
- A self-compile allocating thousands of AST nodes, IR instructions,
  type variables, scope entries, and string literals would need its
  peak/steady-state memory validated under the current scheme.
- Finer ownership-based per-object freeing would only be needed if
  scope-granularity reclamation proves insufficient in practice.

### 13. Large ADT Ergonomics

The IR has types like `IrInst` with ~50 constructors, each carrying
different payloads. Ashes can express this structurally, but:

- The exhaustiveness checker would need to handle 50+ constructors
  efficiently.
- Pattern matching on deeply nested ADTs needs to be ergonomic.

### 14. Numeric Conversions and Formatting

The numeric round-trip has **landed** via `Ashes.Text`: `parseInt` and
`parseFloat` cover `Str → Int` / `Str → Float`, while `fromInt`,
`fromFloat`, and `toHex` cover decimal and hexadecimal formatting for
diagnostics and linker-oriented output.

------------------------------------------------------------------------

## Summary Table

| # | Capability                          | Why Needed                          | Status / Difficulty       |
|---|-------------------------------------|-------------------------------------|---------------------------|
| 1 | FFI / C Interop                     | LLVM API, syscalls                  | Medium (core user `extern` exists; first-class extern values landed; exports deferred) |
| 2 | Byte type + bitwise ops             | Linker, binary formats              | Medium (Int bitwise ops landed) |
| 3 | Persistent (immutable) maps         | Type inference, scopes              | High                      |
| 4 | String manipulation                 | Lexer, error messages               | Medium (uncons/parse landed) |
| 5 | Type annotations                    | Codebase maintainability            | Medium                    |
| 6 | Map / dictionary type               | Pervasive in compiler               | Landed (`Ashes.Map` AVL map) |
| 7 | Arrays / indexed access             | Lexer, linker, codegen              | Medium                    |
| 8 | Records / named fields              | Readability at scale                | Medium                    |
| 9 | Module system enhancements          | Cross-module types                  | Medium                    |
| 10 | Error propagation                  | Compiler error handling             | Low–Medium                |
| 11 | Binary file I/O                    | Writing executables                 | Low–Medium                |
| 12 | Real memory management             | Compiling large programs            | Medium (grow-on-demand arena + scope reclamation exist; 250k-element stress test validates chunk growth) |
| 13 | Large ADT ergonomics               | 50+ variant IR type                 | Low                       |
| 14 | Numeric conversions                | Parsing, diagnostics                | Done (parse + decimal/hex format landed) |

------------------------------------------------------------------------

## The Three Hardest Problems

1. **FFI hardening** — the core `extern` surface exists, but
   self-hosting still needs durable wrapper/ownership conventions around
   LLVM handles before the compiler can safely be ported.
2. **Persistent (immutable) data structures** — without them, type
   inference (the heart of the compiler) is impractical to implement
   while honouring the immutability commitment.
3. **Memory management footprint** — the chunked, grow-on-demand arena
   with scope-level reclamation removes the original hard blocker (a
   fixed 4 MB bump allocator with no freeing). The remaining risk is
   validating peak/steady-state memory of a full self-compile and
   deciding whether finer-grained freeing is warranted.

Everything else is incremental library and language work that follows
naturally once those foundations exist.

------------------------------------------------------------------------

## Actionable Implementation List (Suggested Order)

The list below turns the gap analysis into concrete, independently
shippable steps in a suggested order. Each phase is testable on its own
and unblocks the next. Early items are deliberately byte- and
primitive-oriented: they are low-risk, self-contained, and prerequisite
to porting the linker. All new collections are persistent/immutable, in
keeping with the immutability commitment (Ground Rule #5).

### Phase 1 — Byte & numeric primitives (foundational, low risk)

1. [x] **Unsigned integer support** — `u8`/`u16`/`u32`/`u64` types are
   now first-class. Unsigned literal suffixes parse (`255u8`, `65535u16`,
   `4294967295u32`, `18446744073709551615u64`) and lower to a dedicated
   `TypeRef.TUInt(Bits)` type. Arithmetic wraps at the declared bit width
   (e.g. `255u8 + 1u8 = 0u8`).
2. [x] **Byte type (`u8`) + byte literals** — `u8` is a distinct runtime
   type in the type system (`TypeRef.TUInt(8)`). Byte literals via the
   `u8` suffix parse and lower correctly. `print`, `Ashes.Text.fromInt`,
   and `Ashes.Text.toHex` all accept `u8`/`u16`/`u32`/`u64` values.
3. [x] **Bitwise operators** — `&`, `|`, `^`, `<<`, `>>`, `~` over
   `Int` and all unsigned types (`u8`/`u16`/`u32`/`u64`). Wrapping
   semantics applied after each bitwise and arithmetic operation.
   Pure and isolated; needed for header, relocation, and instruction-operand
   encoding.
4. [x] **`Int → Str` / `Float → Str` conversions** (`fromInt`, `fromFloat`)
   and **hex formatting** — completes the numeric round-trip (the parse
   side already shipped in `Ashes.Text`) and unblocks diagnostics.

### Phase 2 — Indexed / byte collections

5. [x] **Immutable `Bytes` type with O(1) indexed read + length** — the
   lexer and object-file parsing need random access that `uncons` cannot
   provide. Landed: `Ashes.Bytes.empty`, `singleton`, `length`, `get`,
   `append`, `appendByte`, `fromList`.
6. [x] **Little-endian integer↔bytes encode/decode** (read/write
   `u16`/`u32`/`u64` at an offset) — mirrors `BinaryPrimitives`, the
   core linker operation. Landed: `Ashes.Bytes.u16Le`, `u32Le`, `u64Le`,
   `getU16Le`, `getU32Le`, `getU64Le`.
7. [x] **Immutable byte builder + `writeBytes` binary file output** — an
   append-style builder that returns new values (no in-place mutation),
   plus raw binary file write. Landed: `Ashes.File.writeBytes`.

### Phase 3 — String library breadth

8. [x] **`Ashes.String` helpers** — landed with `substring`, `length`,
   `indexOf`, `startsWith`, `contains`, `split`, `trim`, and character-class
   predicates (`isLetter`, `isDigit`, `isWhiteSpace`). Builds on
   `Ashes.Text`.

### Phase 4 — Data structures (immutable)

9. [x] **Immutable `Map(K, V)`** — landed as the shipped `Ashes.Map` module:
   a balanced-tree persistent map with immutable insert/update/lookup helpers.
   The current surface uses caller-supplied comparison functions
   `(K -> K -> Int)` until the language grows a built-in ordering abstraction.
10. [x] **Immutable indexed `Array(T)`** (optional, after `Bytes`/`Map`) —
    landed as the shipped `Ashes.Array` module: a persistent balanced-tree
    indexed sequence with immutable `empty`, `isEmpty`, `length`, `get`,
    `set`, `append`, `toList`, and `fromList` helpers.

### Phase 5 — Type system & ergonomics for a large codebase

11. [x] **Records / named product types + record-update syntax** — replace
    fragile positional tuple access in `Token`, `DiagnosticEntry`, etc.
12. [x] **User-written type annotations** — function signatures and ADT
    typing across module boundaries.
13. [x] **Module system enhancements** — cross-module type sharing without
    circular imports; a project-level compilation model.
14. [x] **Catchable error propagation** — `Result`-threading helpers or
    early-return sugar so the pipeline can abort-and-report without
    `panic`.

### Phase 6 — The hard foundations (largest; can run as parallel research)

15. [x] **Deferred FFI hardening** — extern functions as first-class values or
    module exports, plus safe wrappers and ownership conventions for
    opaque LLVM-C handles.
16. [x] **Memory-management hardening** — validate the chunked arena's
    steady-state behaviour under a real self-compile; add finer
    ownership-based freeing only if scope-granularity reclamation proves
    insufficient.
17. [x] **Large-ADT exhaustiveness / performance** — ensure the
    exhaustiveness checker scales to the 50+ `IrInst` constructors.

### Suggested first slice

Items **1–3** remain the next foundational byte/numeric slice (unsigned
ints, byte type, remaining bitwise ops). Item **4** (`Int`/`Float` → `Str`
+ hex) has landed.
