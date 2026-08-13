# Self-Hosting: Building the Ashes Compiler in Ashes

Status as of 2026-08-13. This tracks whether Ashes-the-language and its stdlib have what a
from-scratch Ashes-the-compiler needs. See [FUTURE_FEATURES.md](FUTURE_FEATURES.md) for how this
fits the broader roadmap.

## Language/stdlib prerequisites

`Required` means a design or implementation is still needed for at least one tagged area. `Optional`
items may improve performance or ergonomics but do not block the first self-hosted compiler. An area
tag names the consumer, not the project that must implement the prerequisite. `Tests` covers the
component unit-test projects, `TestRunner` the end-to-end `.ash` runner, and `Fuzzing` both
`Ashes.Fuzzing` and `Ashes.Fuzzing.Tests`. Tests are required for every delivered change; those tags
appear only where the test infrastructure itself consumes the capability. Every capability links to
the work package that explains how it is used or what remains. A `Complete` status means the
language/stdlib substrate exists; its linked package still records the self-hosted port and
differential-validation work.

| Capability | Status | Areas |
|---|---|---|
| [Unsigned integer support (`u8`, `u16`, `u32`, `u64`)](#gap-numeric-primitives) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| [Byte type (`u8`) and byte literals](#gap-numeric-primitives) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| [Bitwise operators (`&`, `\|`, `^`, `<<`, `>>`, `~`)](#gap-numeric-primitives) | Complete | `Compiler/Backend`, `Compiler/Linker` |
| [Numeric text conversions (`parseInt`, `parseFloat`, `fromInt`, `fromFloat`, `toHex`)](#gap-numeric-primitives) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI` |
| [Basic FFI (`external` functions/types, pointers, resources, `symbol@library`)](#gap-ffi-native-arrays-out-parameters-and-foreign-buffers) | Partial | `Compiler/Backend` |
| [LLVM native arrays, out parameters, returned strings, and foreign buffers](#gap-ffi-native-arrays-out-parameters-and-foreign-buffers) | Required | `Compiler/Backend` |
| [Immutable `Bytes` with indexed reads and append helpers](#gap-efficient-immutable-binary-construction) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker`, `LSP`, `DAP` |
| [Little-endian byte encode/decode helpers (`u16/u32/u64`)](#gap-efficient-immutable-binary-construction) | Complete | `Compiler/Linker`, `DAP` |
| [Efficient preallocation, range copy, and random-access binary patching](#gap-efficient-immutable-binary-construction) | Required | `Compiler/Linker` |
| [Binary file output (`Ashes.IO.File.writeBytes`)](#gap-efficient-immutable-binary-construction) | Complete | `Compiler/Linker`, `CLI`, `TestRunner`, `Fuzzing` |
| [Path normalization, joining, parent/basename, and relative paths](#gap-host-tool-filesystem-and-process-control) | Required | `Compiler/Semantics`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| [Current/executable/temp/cache directories and environment lookup](#gap-host-tool-filesystem-and-process-control) | Required | `Compiler/Semantics`, `Compiler/Backend`, `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| [Directory enumeration, creation, deletion, and atomic rename](#gap-host-tool-filesystem-and-process-control) | Required | `Compiler/Semantics`, `CLI`, `TestRunner`, `Fuzzing` |
| [Marking emitted ELF files executable](#gap-host-tool-filesystem-and-process-control) | Required | `CLI`, `TestRunner`, `Fuzzing` |
| [stderr output and controlled process exit codes](#gap-host-tool-filesystem-and-process-control) | Required | `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| [String helpers (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`)](#gap-text-unicode-and-source-coordinates) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI`, `LSP`, `DAP` |
| [Unicode scalar classification through `Rune`](#gap-text-unicode-and-source-coordinates) | Complete; self-hosted lexer migration pending | `Compiler/Frontend`, `Formatter`, `LSP` |
| [Canonical UTF-8 source offsets and UTF-16/LSP coordinate conversion](#gap-text-unicode-and-source-coordinates) | Design required | `Compiler/Frontend`, `Formatter`, `LSP`, `DAP` |
| [Persistent immutable map (`Ashes.Collection.Map`)](#gap-persistent-collections) | Complete | `Compiler/Semantics`, `Compiler/Backend`, `LSP`, `DAP` |
| [Persistent immutable array (`Ashes.Collection.Array`)](#gap-persistent-collections) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Compiler/Backend`, `Formatter` |
| [Persistent immutable set](#gap-persistent-collections) | Optional — use `Map(K, Unit)` initially | `Compiler/Semantics`, `LSP` |
| [Generic hashing for non-`Str` keys](#gap-persistent-collections) | Optional — use `Map` initially | `Compiler/Semantics`, `LSP` |
| [Named string-builder or rope](#gap-text-construction-performance) | Optional — `Text.join` and affine-growth reuse are workable initially | `Compiler`, `Formatter`, `CLI`, `LSP`, `DAP` |
| [Records, named patterns, and record-update syntax](#gap-compiler-data-modeling) | Complete | `Compiler`, `Formatter`, `LSP` |
| [User-written type annotations, aliases, and zero-cost nominal types](#gap-compiler-data-modeling) | Complete | `Compiler`, `Formatter`, `LSP` |
| [Project/module compilation with explicit exports and path dependencies](#gap-project-and-module-hosting) | Complete in the C# compiler; host path APIs still required by the port | `Compiler/Semantics`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| [Catchable error propagation for compile-pipeline flows](#gap-errors-and-deterministic-memory) | Complete | `Compiler`, `Formatter`, `CLI`, `LSP`, `DAP` |
| [RC-Perceus deterministic memory without cyclic graphs](#gap-errors-and-deterministic-memory) | Complete; the port must avoid parent/back-reference cycles | `Compiler`, `Formatter`, `LSP`, `DAP` |
| [Persistent immutable substitution and unification architecture](#gap-hm-type-inference-is-built-on-mutable-union-find) | Required | `Compiler/Semantics` |
| [Large-ADT exhaustiveness and performance hardening](#gap-large-adt-semantics) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter` |
| [JSON parsing/serialization for `ashes.json` and JSON-RPC](#gap-tooling-protocols-and-processes) | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP` |
| [Stdio JSON-RPC Content-Length framing](#gap-tooling-protocols-and-processes) | Complete | `LSP`, `DAP` |
| [Interactive subprocess control with piped streams and timeouts](#gap-tooling-protocols-and-processes) | Complete | `DAP`, `TestRunner`, `Fuzzing` |
| [Regex utilities for tooling text](#gap-tooling-protocols-and-processes) | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP`, `TestRunner` |
| [Unit assertions plus deterministic test discovery/execution](#gap-self-hosted-validation-infrastructure) | Partial — `Ashes.Test` is complete; discovery still needs the host APIs above | `Tests`, `TestRunner` |
| [Deterministic fuzz generation, replay, shrinking, corpus, and artifacts](#gap-self-hosted-validation-infrastructure) | Complete in the C# harness; porting it is not a compiler-core gate | `Fuzzing` |
| [Tar/gzip, SHA-256, authenticated HTTP, and multipart upload](#gap-registry-and-distribution-cli) | Required only for a full CLI replacement | `CLI/Registry` |
| [Defined stage-0/stage-1/stage-2 bootstrap and reproducibility gate](#gap-bootstrap-completion-gate) | Design required | `Compiler`, `CLI`, `Tests`, `TestRunner`, `Fuzzing` |

The first self-hosted **compiler core** does not require `CLI/Registry`, `LSP`, `DAP`, `Fuzzing`, or
`TestRunner` parity. Those are separate replacement layers. Set, generic hashing, and a named text
builder are likewise not admission gates; measure the persistent alternatives before adding them.

## Capability gaps and work packages

Each package below is the implementation hand-off for one or more rows in the table. Some are missing
language or stdlib facilities; others describe how already-shipped facilities must be applied and
validated in the self-hosted toolchain. Complete a package in reviewable slices, updating the
normative documentation before any language or API change.

### Gap: numeric primitives

Unsigned integers, `u8`, byte literals, bitwise operators, and numeric text conversion are already
shipped. The remaining gap is adoption and parity in the Ashes implementation of the compiler,
especially at file-format and LLVM boundaries where signed coercions are not interchangeable.

Workable tasks:

1. Inventory every numeric type and conversion used by the C# frontend, LLVM backend, ELF/PE linkers,
   and diagnostics; assign an exact Ashes type instead of defaulting offsets and masks to `Int`.
2. Port numeric-literal decoding with explicit overflow handling for every suffix, including `u64`
   values above `i64::MAX`; remove the historical lexer exemption for `u64::MAX`.
3. Port bit masks, shifts, alignment arithmetic, endian fields, and checked narrowing without routing
   them through signed values.
4. Add shared boundary fixtures for zero, maximum values, overflow, shift widths, and conversion
   failures, then compare the C# and Ashes frontend/linker results.

Done when the self-hosted frontend accepts and rejects the same numeric literals as stage 0, and the
self-hosted linker produces structurally identical integer fields for all four targets.

### Gap: HM type inference is built on mutable union-find

`Lowering.TypeInference.cs` implements unification via a single mutable
`Dictionary<int, TypeRef> _subst` field (`Lowering.cs:552`), with in-place path compression in
`Prune` (`Lowering.TypeInference.cs:202`) and in-place binding in `Unify`
(`Lowering.TypeInference.cs:218`). This is interleaved with mutable scope and ownership stacks.
Porting this to pure-immutable Ashes means threading an immutable
substitution map through every call site instead of mutating in place — well-understood CS
(persistent union-find), but a genuine redesign of the inference core, not a mechanical translation.
Budget this as its own milestone, most likely concurrent with or just before a self-hosted Semantics
layer.

Workable tasks:

1. Define immutable inference state containing the next type-variable id, substitution map,
   constraints, scope stack, and accumulated diagnostics.
2. Implement `prune`, occurs-check, variable binding, and unification as state-returning functions;
   path compression may return an updated map but must not introduce source-visible mutation.
3. Port generalization, instantiation, recursive binding groups, traits, effects, and ownership facts
   one subsystem at a time, keeping the current C# inference engine as the oracle.
4. Serialize inferred types and diagnostics in a stable form and differentially run the semantic unit
   fixtures plus the full `.ash` corpus.
5. Benchmark large modules and large ADTs. If persistent-state churn is excessive, optimize through
   compiler-proven unique reuse rather than a mutable language escape hatch.

Done when inferred public types, accepted/rejected programs, diagnostic codes and spans, and ownership
summaries match stage 0 across the corpus and extracted inference fixtures.

### Gap: FFI native arrays, out parameters, and foreign buffers

`Ashes.Backend` talks to LLVM through ~145 direct LLVM-C API P/Invoke bindings
(`src/Ashes.Backend/Llvm/Interop/LlvmApi.cs`), not textual IR + a `clang`/`llc` subprocess. Ashes'
own `external` mechanism can name opaque and pointer types, but Ashes code cannot construct a
contiguous native array of opaque handles, allocate/read out-parameter storage, copy a returned
pointer-plus-length buffer into `Bytes`, or decode and dispose a returned native string. The current
C# adapter does all of these with `out` parameters, pinned `ReadOnlySpan<T>` values, and explicit
`Marshal`/copy operations. Representative calls include `LLVMGetTargetFromTriple`,
`LLVMFunctionType`, `LLVMBuildCall2`, `LLVMBuildGEP2`, `LLVMVerifyModule`, and
`LLVMTargetMachineEmitToMemoryBuffer`.

**Decision (2026-07-24): extend Ashes' FFI rather than switch the self-hosted backend to
textual-IR-plus-subprocess.** This keeps the self-hosted backend architecturally aligned with the
current one (direct LLVM-C calls) instead of introducing a second codegen strategy. The extension is
a parallel track independent of the Frontend and linker ports, and must be specified in the
[language reference](../reference/language.md) before implementation.

#### Native input arrays

The first required shape is a same-typed, contiguous, call-scoped array. It covers
`LLVMFunctionType`, `LLVMBuildCall2`, `LLVMBuildGEP2`, `LLVMConstArray2`,
`LLVMConstStructInContext`, and `LLVMStructTypeInContext`.

Workable tasks:

1. Specify a compiler-supported immutable native-call buffer, provisionally `FfiBuffer(a)`, built
   from an Ashes `List(a)` or `Array(a)`, plus `Ffi.length`. It exposes no indexing writes or pointer
   arithmetic.
2. Extend external parameter types so `FfiBuffer(TypeRef)` lowers to `LLVMTypeRef*`; the wrapper
   passes `Ffi.length(buffer)` to the adjacent count parameter, and an empty buffer lowers to null
   plus zero.
3. Keep the storage stable for exactly the dynamic extent of the external call. Reject storing,
   returning, or capturing its pointer in Ashes source.
4. Implement parsing, formatting, type checking, IR metadata, target lowering, diagnostics, LSP
   syntax/hover/completion, and parser/lowering fuzz generators for the chosen declaration surface.
5. Add focused wrappers and ABI tests for all six LLVM calls, including empty and multi-element
   arrays on every target.

#### Out parameters

The current binding needs outputs from `LLVMGetTargetFromTriple`, `LLVMVerifyModule`,
`LLVMTargetMachineEmitToMemoryBuffer`, and `LLVMParseIRInContext`. Do not expose general mutable
references merely to model these signatures.

Workable tasks:

1. Add declaration-only `out` parameters whose source-level call result is a tuple containing the C
   return value followed by each output (omitting `Unit` for a C `void` return). The compiler owns and
   zero-initializes the temporary slots.
2. Support nullable pointer and opaque-handle outputs first: null becomes `None`, non-null becomes
   `Some(value)`. Defer scalar outputs until the spec defines whether a native function may leave a
   slot unwritten, since zero is often a valid scalar value.
3. Lower each slot with target-correct size and alignment, call the external, load the outputs once,
   and make the temporary addresses non-escaping.
4. Add diagnostics for `out` outside external declarations, unsupported element types, indirect use
   of an ownership-sensitive external, and attempts to combine `out` with invalid `borrow`/`consume`
   shapes.
5. Test success, failure, null output, multiple output, and cross-target ABI layouts against small C
   fixtures plus the four LLVM signatures.

#### Returned native strings

LLVM returns owned C strings for host CPU names/features, data layouts, module printing, and several
error messages. Returning those as the existing FFI `Str` type is incorrect: an Ashes `Str` has its
own runtime representation, and the native allocation must be disposed with `LLVMDisposeMessage`.

Workable tasks:

1. Specify declaration metadata for borrowed versus owned UTF-8 C strings, including nullable forms;
   do not treat an arbitrary `*u8` return as an Ashes `Str`.
2. For owned strings, scan to the terminator with a documented bound policy, copy into a new Ashes
   `Str`, validate UTF-8 with deterministic replacement/error semantics, then call the declared
   destructor exactly once on every success and error path.
3. For borrowed strings, copy before the owning call/resource can end and never schedule a
   destructor. Encode nullability as `Maybe(Str)` rather than language-level null.
4. Test empty, null, malformed UTF-8, embedded-boundary, successful disposal, and conversion failure
   using an instrumented C fixture, then cover the LLVM CPU/data-layout/print/error cases.

#### Foreign pointer-plus-length buffers

Object emission returns an owning `LLVMMemoryBufferRef`; `LLVMGetBufferStart` and
`LLVMGetBufferSize` expose a borrowed pointer and length. The safe Ashes operation is an immediate,
bounded copy into owned `Bytes`, not a general foreign slice that may outlive its owner.

Workable tasks:

1. Model `LLVMMemoryBufferRef` as a declared affine external resource with
   `LLVMDisposeMemoryBuffer` as its destructor.
2. Add a trusted `Ffi.copyBytes : *u8 -> u64 -> Bytes` primitive (or equivalent declaration shape)
   that validates length overflow, copies immediately, and requires `UnsafeFfi`.
3. Keep the memory-buffer resource live across both pointer/length reads and the copy; make raw LLVM
   functions file-local behind an exported nominal wrapper.
4. Test zero-length, binary NUL bytes, large lengths, overflow rejection, and exactly-once cleanup,
   then compare emitted object bytes with the current C# backend.

#### Ownership facade and delivery order

Owning LLVM objects—contexts, modules, builders, target machines, target data, DI builders, pass
options, memory buffers, and messages—must be declared resources when they have a destructor.
Types, values, basic blocks, metadata, and targets are non-owning handles tied to an owner; keep that
lifetime invariant inside the opaque LLVM module initially. Record consuming APIs such as
`LLVMLinkModules2` explicitly. In external declarations, `borrow` means the native call does not take
Ashes ownership; it does not claim that LLVM leaves the native object unmodified.

Deliver this as three reviewable slices:

1. native input buffers;
2. nullable opaque out parameters plus owned/borrowed C strings; and
3. foreign binary buffers plus a minimal private LLVM facade.

Each slice updates the language reference first, then Frontend, Formatter, Semantics/IR, Backend,
diagnostics, LSP, unit/e2e tests, and relevant fuzz generators. Do not add C structs by value,
callbacks, varargs, scalar out parameters, or public pointer arithmetic until an audited LLVM call
actually requires them. The package is done when an Ashes program uses the facade to construct a tiny
LLVM module and emit object bytes identical to the C# adapter on all four targets.

### Gap: efficient immutable binary construction

The native ELF/PE linkers do much more than append bytes: they preallocate output images, copy
sections into aligned offsets, patch headers and instructions, and apply relocations at arbitrary
positions. `Ashes.Byte` currently exposes indexed reads, concatenation, and endian encode/decode, but
no preallocation, range copy, or random-access write operation. Rebuilding the complete value for
every patch would make a functional port accidentally quadratic.

Specify a pure binary-building surface before the linker milestone. Operations such as allocation,
range replacement, and `setU16Le`/`setU32Le`/`setU64Le` must return new `Bytes` values at the language
level while allowing the compiler to reuse a uniquely owned buffer internally. The acceptance gate is
not merely correct output: linking representative debug and release objects must stay bounded and
within an agreed factor of the C# linker on every target.

Workable tasks:

1. Inventory every indexed read, aligned copy, patch, relocation write, and output-size calculation in
   the ELF and PE linkers; use that inventory to specify the minimum `Bytes` API.
2. Add pure allocation, range-copy/replacement, and checked random-access setters for `u8`, `u16`,
   `u32`, and `u64` in both endian orders actually used. Define out-of-range behavior explicitly.
3. Lower update chains so uniquely owned buffers can be reused internally while aliases preserve the
   old value; add ownership regressions that prove both paths.
4. Port one small object reader/writer, then the ELF and PE linkers. Write the completed value with
   `Ashes.IO.File.writeBytes` rather than introducing linker-specific I/O.
5. Differentially compare sections, symbols, relocations, headers, and final bytes for debug/release
   objects on all targets; benchmark time and peak memory against the C# linker.

Done when representative links are byte-identical or have a documented deterministic-metadata
difference, pass structural target validation, and remain within an agreed performance factor.

### Gap: host-tool filesystem and process control

Single-file frontend experiments need only `readText`, but a compatible compiler must discover
projects, normalize paths, walk source roots, find its shipped `lib/` and runtime assets, create output
directories, write temporary files, and mark Linux output executable. The surrounding tools also need
stderr and controlled exit codes so a user compilation error is not reported as a language `panic`.

Add these as ordinary capability-tracked host APIs rather than ad-hoc compiler externals. Keep path
operations pure; filesystem acquisition and mutation carry `FileRead`/`FileWrite`, environment lookup
gets an explicit ambient-authority classification, and possession-based file-handle operations remain
unchanged.

Workable tasks:

1. Specify and implement pure path normalization, join, parent, basename, extension, relative-path,
   and platform-separator behavior with Windows drive/UNC and Unix-root fixtures.
2. Add capability-tracked APIs for current/executable/temp/cache directories and environment lookup;
   make installed compiler asset discovery independent of the repository working directory.
3. Add deterministic directory enumeration, recursive creation, deletion, and atomic same-filesystem
   replacement. Specify ordering, missing paths, collisions, symlinks, and cross-device failures.
4. Add a portable executable-permission operation that sets the required Unix mode bits and is a
   documented no-op or equivalent on Windows.
5. Add stderr writes and controlled process termination without turning expected compiler failures
   into `panic`; preserve cleanup of live resources on ordinary error-return paths.
6. Exercise the APIs through project discovery, compiler output, TestRunner temporary workspaces, and
   installed-layout integration tests on Linux and Windows hosts.

Done when the self-hosted CLI can compile a project from outside the repository, locate all shipped
assets, atomically write an executable, report diagnostics on stderr, and return the same exit code as
the C# CLI.

### Gap: text, Unicode, and source coordinates

The C# frontend records UTF-16 string-unit offsets while the self-hosted lexer walks UTF-8 bytes.
Byte offsets are a natural internal identity for an UTF-8 compiler, but diagnostics, formatter edits,
debug information, and LSP positions need deliberate conversions. Choose one canonical compiler span
unit, specify line/column conversion (including malformed UTF-8), and negotiate or convert LSP UTF-8
and UTF-16 positions. A differential test may normalize representations only after this contract is
fixed; the divergence cannot remain an unexplained permanent exemption.

Workable tasks:

1. Migrate `selfhost/Frontend/Lexer.ash` from removed byte-oriented text predicates to `Rune`
   classification while retaining ASCII fast paths where measured.
2. Choose and document one canonical internal span unit, malformed-UTF-8 behavior, newline handling,
   and conversion rules for byte offsets, Unicode scalar columns, UTF-16/LSP positions, and debug
   locations.
3. Implement a shared line index and conversion API consumed by diagnostics, formatter edits, LSP,
   and DAP rather than duplicating conversions in each tool.
4. Port the shipped substring/search/split/trim helpers wherever the compiler currently relies on C#
   string methods; add empty, non-ASCII, combining-mark, astral, and malformed-input fixtures.
5. Remove position normalizations from lexer/parser differential tests and rerun the full corpus plus
   extracted edge cases.

Done when token, AST, diagnostic, formatter, LSP, and debug spans agree with the documented contract
for ASCII and Unicode sources, and the self-hosted lexer builds against the current stdlib.

### Gap: persistent collections

`Ashes.Collection.Map` and `Ashes.Collection.Array` are shipped; a first compiler can represent sets
as `Map(K, Unit)` and does not require generic hashing. The risk is algorithmic behavior at compiler
scale, not basic availability.

Workable tasks:

1. Map C# dictionaries, hash sets, lists, stacks, and indexed tables to `Map`, `Map(K, Unit)`, `List`,
   or `Array`, documenting ordering requirements and expected operation complexity.
2. Port symbol tables, module maps, AST/IR sequences, work queues, and visited sets using those
   choices; add deterministic-order tests anywhere output is serialized.
3. Benchmark lookup/update-heavy inference and large indexed IR workloads. Confirm uniquely owned
   update chains receive the intended Perceus reuse.
4. Add a persistent set or generic hashing only if measurements show the `Map` representation blocks
   a milestone; specify key equality/hash coherence before doing so.

Done when representative frontend and Semantics workloads have deterministic output and acceptable
time/memory without relying on a mutable collection escape hatch.

### Gap: text construction performance

`Text.join` plus affine-growth reuse is sufficient for the first port, so a named builder or rope is
optional. Formatter output, diagnostics, IR dumps, JSON, and protocol messages still need measurement
to ensure repeated concatenation does not become quadratic.

Workable tasks:

1. Centralize large-output construction on chunk accumulation plus one `Text.join`; avoid recursive
   left-associated `+` in the self-hosted formatter and serializers.
2. Add size-scaling benchmarks for formatting a large module, rendering many diagnostics, serializing
   an AST, and emitting a large JSON-RPC message.
3. Inspect lowered ownership/reuse reports for the hot paths and fix missed unique-buffer reuse where
   possible.
4. Introduce a named builder or rope only if benchmarks still exceed an agreed threshold, with a pure
   API and deterministic flattening behavior.

Done when doubling representative output does not cause accidental quadratic growth and output is
byte-for-byte equal to the stage-0 component.

### Gap: compiler data modeling

Records, named patterns, record updates, type annotations, aliases, and zero-cost nominal `type`
declarations are shipped. The work is to use them to preserve phase boundaries and prevent accidental
mixing of identifiers, offsets, and handles in the port.

Workable tasks:

1. Define separate nominal types for source ids, symbol ids, type-variable ids, byte offsets, target
   offsets, and LLVM handles; do not use bare `Int` or raw opaque handles across module boundaries.
2. Port AST, typed-tree, IR, diagnostic, and target-layout records with exhaustive named patterns and
   record updates.
3. Add serialization round trips and compile-time negative fixtures proving incompatible nominal
   values cannot be mixed.
4. Keep conversion functions at explicit boundaries and compare serialized records with stage 0.

Done when the self-hosted phase models cover the current C# model without untyped identifier/offset
shortcuts and their stable serializations match the differential harness.

### Gap: project and module hosting

The compiler already supports sequential top-level declarations, explicit exports, imports, and path
dependencies. The self-hosted implementation must reproduce graph construction and diagnostics using
the host APIs above.

Workable tasks:

1. Port `ashes.json` loading, source-root discovery, module-name derivation, dependency namespacing,
   export filtering, and deterministic topological ordering.
2. Detect missing modules, duplicate names, dependency cycles, invalid entries, and attempts to use
   non-exported declarations with the same diagnostic codes and source spans as stage 0.
3. Build each self-hosted phase as a real separate project under `selfhost/` and declare only the
   dependencies allowed by the repository DAG.
4. Add project fixtures covering aliases, selector imports, path dependencies, diamond graphs, and
   invocation from nested or unrelated working directories.

Done when stage 0 and the self-hosted project loader choose the same ordered source set, visible
exports, and diagnostics for the full project fixture suite.

### Gap: errors and deterministic memory

Catchable effects and RC-Perceus are shipped. A compiler port must shape expected failures as values,
keep panics for violated invariants, and avoid cyclic graphs that deterministic reference counting
cannot reclaim.

Workable tasks:

1. Classify stage-0 exceptions and error returns into user diagnostics, recoverable host failures, and
   internal invariants; model the first two with typed results/effects.
2. Design parent/child compiler structures without strong back-references; use ids plus central maps
   for scopes, syntax parents, type graphs, and control-flow graphs where cycles would otherwise form.
3. Run `--explain ownership`, `--explain rc`, and `--explain memory` on representative compiler paths;
   add regressions for closure captures, recursive collections, and error unwinding.
4. Stress repeated compile/format/protocol requests in one process and compare stable memory use and
   cleanup of files, processes, and LLVM resources.

Done when expected bad input never crashes the tool, repeated workloads do not grow without bound,
and sanitizers/resource counters show balanced cleanup on success and failure.

### Gap: large ADT semantics

Large-ADT exhaustiveness and performance hardening is shipped, but the self-hosted parser and
Semantics implementation will exercise it with token, AST, type, IR, and diagnostic unions much
larger than ordinary application code.

Workable tasks:

1. Port the largest compiler ADTs and their matches without replacing structural matches with ad-hoc
   integer tag logic.
2. Differentially test exhaustiveness, redundancy, payload typing, and diagnostics using generated
   ADTs around representation and decision-tree thresholds.
3. Benchmark compile time and generated-code size for lexer/parser dispatch and IR visitors.
4. Add targeted fuzz generation for deep/nested patterns and large constructor sets, preserving any
   minimized failure as a regression.

Done when large compiler unions compile and run within agreed limits and match stage-0 behavior for
exhaustiveness and diagnostics.

### Gap: tooling protocols and processes

JSON, stdio JSON-RPC framing, interactive subprocesses, and regex are shipped. Their remaining gap is
tool-specific integration and long-lived-process correctness.

Workable tasks:

1. Port `ashes.json`, LSP, and DAP codecs with explicit schemas and golden messages; distinguish a
   missing field, JSON null, malformed data, and unknown forward-compatible fields.
2. Share one incremental Content-Length framer across LSP and DAP and test fragmented headers/bodies,
   multiple messages per read, invalid lengths, EOF, and non-ASCII payload byte counts.
3. Port debugger/test subprocess control with piped stdin/stdout/stderr, timeouts, cancellation,
   process-tree cleanup, and platform-specific quoting.
4. Use regex only for external/tooling text already defined that way; retain structural parsers for
   Ashes source and protocols. Differentially test representative compiler and debugger output.
5. Run multi-request soak tests and fuzz JSON/framing/regex inputs for hangs, unbounded allocation,
   crashes, and nondeterministic responses.

Done when self-hosted protocol transcripts and subprocess outcomes match the current LSP, DAP, and
TestRunner fixtures on supported hosts.

### Gap: self-hosted validation infrastructure

`Ashes.Test` assertions and the C# fuzzing framework exist. Deterministic discovery depends on the
host APIs, and porting TestRunner/fuzzing is a toolchain-parity layer rather than a compiler-core gate.

Workable tasks:

1. Define deterministic test discovery order and port directive parsing, temporary workspaces,
   stdin/files/network fixtures, timeout handling, output comparison, and cleanup.
2. Reuse the existing `.ash` corpus and extract component edge cases into shared fixtures consumed by
   both stage 0 and self-hosted differential drivers.
3. Port deterministic seed derivation, generation registries, replay, shrinking, corpus promotion,
   artifact layout, and unique-error classification without changing replay identity.
4. Extend generators whenever a prerequisite adds syntax, types, IR shapes, ownership behavior, or
   ABI lowering; LLVM FFI work specifically needs declaration/parser, semantic, and lowering fuzzing.
5. Run the same fixed seeds twice and compare results/artifacts byte-for-byte before adding bounded CI
   campaigns.

Done when tests are discovered and reported identically and a failure produced by the self-hosted
fuzzer can be replayed and minimized by seed with stable artifacts.

### Gap: registry and distribution CLI

Tar/gzip, SHA-256, authenticated HTTP, and multipart upload are needed only to replace the package and
registry CLI, not to declare the compiler core self-hosted.

Workable tasks:

1. Port deterministic archive creation/extraction with path-traversal, duplicate-entry, size-limit,
   and malformed-stream defenses.
2. Verify streaming SHA-256 against published vectors and use it for package integrity before
   extraction or installation.
3. Port authenticated HTTP, token storage/redaction, multipart upload, retries, cancellation, and
   server-error decoding without logging credentials.
4. Differentially test `add`, `restore`, `publish`, `yank`, `search`, and `info` against a local fake
   registry, including corrupt packages and interrupted operations.

Done when the self-hosted registry CLI passes the existing package workflow suite and produces
archives/checksums compatible with the C# CLI.

### Gap: bootstrap completion gate

Component differential tests prove compatibility but do not by themselves prove self-hosting. The
bootstrap work package is:

1. Define hermetic stage inputs: source ordering, target triple, compiler flags, environment, absolute
   path remapping, timestamps/build ids, and discovery of `libLLVM`, stdlib, and vendored payloads.
2. Add a command that records stage manifests and builds stage 1 with stage 0, stage 2 with stage 1,
   and stage 3 with stage 2 without manually changing paths.
3. Define byte-for-byte comparison as the default; enumerate and structurally normalize only metadata
   proven nondeterministic, failing on every unexplained difference.
4. Run compiler unit fixtures, the `.ash` corpus, project fixtures, and target structural checks with
   the bootstrapped compiler, retaining stage logs and differing artifacts.
5. Automate the gate per supported host in CI; execute emitted programs where the host supports them
   and apply the documented structural validation elsewhere.

The compiler-core roadmap completes only after:

1. the current C# compiler (stage 0) builds the Ashes compiler (stage 1);
2. stage 1 compiles the same compiler sources into stage 2;
3. stage 2 repeats the build, with stage-2/stage-3 output compared byte-for-byte when deterministic
   metadata permits it, otherwise by a documented structural equivalence;
4. the bootstrapped compiler passes the full unit-fixture and `.ash` corpus differential gates; and
5. the process is repeated for every supported host, with all four targets at least structurally
   validated and executable where the host supports them.

The gate must also prove that the compiler locates `libLLVM`, standard-library sources, and the
vendored native/bitcode payloads from an installed layout. A compiler that can compile itself only
from a repository-relative working directory is an intermediate milestone, not a completed bootstrap.

## Recommended rewrite order

Given the dependency DAG (`Frontend` → `Semantics` → `Backend`, `Frontend` has zero internal
dependencies) and component size (`Frontend` ~3.9k LOC vs. `Semantics` ~43.7k vs. `Backend`
~33.5k), the lowest-risk path to validate the self-hosting approach is:

1. **Frontend (lexer + parser) first**, after migrating the existing lexer to `Rune` and deciding the
   source-coordinate contract. It is the smallest surface and needs neither LLVM FFI nor HM
   inference. Validate with a differential test: run the same corpus
   (`tests/`, `lib/`, `examples/`) through both the existing C# frontend and the Ashes rewrite,
   diff a serialized AST (the existing JSON module is sufficient) between them. This also answers
   the open question of whether recursion-only/no-loop code performs acceptably at parser scale.
2. **Formatter second.** It consumes only the frontend AST, adds an early round-trip differential
   gate, and exercises large deterministic text construction without depending on Semantics.
3. **Linker third**, after efficient binary construction lands. `LlvmImageLinkerElf*.cs` /
   `LlvmImageLinkerPe*.cs` are otherwise pure algorithms — buffer parsing, relocation math, and
   binary writing — and provide the first realistic performance test of the byte surface.
4. **Semantics fourth**, once the immutable-substitution redesign for type inference is scoped as
   its own milestone.
5. **LLVM codegen fifth**, gated on the audited FFI extension.
6. **Compiler CLI sixth**, gated on host paths/directories, stderr/exit, executable permissions, and
   installed-asset discovery. This is the first stage eligible for the bootstrap completion gate.
7. **TestRunner, Fuzzing, LSP, DAP, and registry/package CLI parity** follow as separately reviewable
   toolchain layers; they are not prerequisites for declaring the compiler core self-hosted.

### Landing this incrementally without merging a half-finished compiler

Put the self-hosted implementation under a new top-level directory (e.g. `selfhost/`), separate
from `lib/Ashes/` and the C# project DAG. Each milestone's differential-test harness (step 1's
AST-diff, etc.) is small, purely additive, and non-gating — it can merge to `main` piece by piece
because it's inert until the self-hosted compiler is complete enough to actually replace a stage,
unlike the compiler itself. Follow the usual per-milestone worktree/PR workflow.

Module layout mirrors the C# project DAG as **real, separate Ashes projects** under `selfhost/`
— `selfhost/Frontend/`, `selfhost/Semantics/`, `selfhost/Backend/`, `selfhost/Formatter/`,
`selfhost/Cli/`, `selfhost/Lsp/`, `selfhost/Dap/` — each with its own `ashes.json`, rather than one
project with an internal namespace convention. Downstream projects consume upstream ones via
`ashes.json` path dependencies (`docs/md/guide/projects.md` §3.7), which namespaces the dependency's
modules automatically (e.g. `selfhost/Semantics/ashes.json` depends on `{"path": "../Frontend"}`,
making its modules available as `Frontend.Tokens`, `Frontend.Lexer`, ...). This makes the DAG a
structural property, not just a convention: `selfhost/Lsp/ashes.json` lists `Frontend`, `Semantics`,
and `Formatter` as dependencies and simply has no way to resolve `Backend.*` unless it's added —
mirroring the boundary rule this repo already enforces for the C# projects. Each project's own
`entry` can double as a standalone smoke test/tool for that slice (e.g. `Frontend`'s entry is a
token-dump CLI, useful both as a "does Frontend work standalone" check and as the differential-test
harness's Ashes-side driver).

### Test-coverage strategy: reuse the existing suite, don't re-derive it

The C# suite (`Ashes.Tests` plus `Ashes.Lsp.Tests`, 2,126 `[Test]` methods) and the current `.ash`
corpus (667 files in `tests/`, 18 in `lib/`, and 17 in `examples/` as of 2026-08-13) are the real
spec. Each self-hosted
milestone should validate against it two ways, not just "looks right on a few files":

1. **Corpus differential testing** — run the self-hosted component over every `.ash` file already
   in the repo and diff its output against the current C# component on the same input (e.g. token
   stream for the lexer, AST for the parser). This reuses the full breadth of the existing e2e
   corpus for free, without hand-porting anything, because token/AST-stream equivalence is a
   precondition for those 579+ tests to ever pass once later stages are also self-hosted.
2. **Edge-case fixture extraction** — component-specific C# unit tests (e.g. `LexerTests.cs`,
   `LexerEdgeCaseTests.cs`, `AndKeywordLexerTests.cs` — 44 tests total for the lexer) encode
   specific boundary behavior (suffix overflow, escape decoding, maximal-munch operator
   disambiguation, malformed input) that may not appear in any real `.ash` source file. Pull the
   literal input text out of each such test into a small shared fixture file, and run it through
   the same differential-diff mechanism as the corpus — the C# implementation's current output *is*
   the expected value, so there's no hand-transcribed "expected" to drift out of sync. This is the
   repeatable template for every future milestone (parser, semantics, ...), not just the lexer.

A milestone is "done" only when both checks are clean across the full corpus + fixture set, not a
sample of it.

### Compiler bugs found while writing the lexer

Self-hosting exercises real language/stdlib surface that the existing ~2,000-test corpus
apparently never covered in combination. Every bug found this way gets fixed, not just
worked around — tracked here as they're found, updated once fixed:

1. **Fixed, merged 2026-07-24 (PR #297).** Qualified alias access to ADT constructors failed
   (`alias.Constructor(...)` reported "Unknown module", while `alias.function(...)` through the
   identical alias worked). Root cause: `Lowering.ModuleResolution.cs`'s `LowerQualifiedVar` only
   wired up qualified access for functions, never constructors — fixed by threading a
   per-module constructor map through lowering, scoped so an alias not actually declaring a given
   constructor still correctly errors. Follow-up gap found and deliberately left out of the fix:
   qualified constructor *patterns* in `match` (`json.JsonInt(x) -> ...`) aren't parseable at all
   (new syntax work, not a bug fix) — open for a future task.
2. **Ownership/reuse-pass memory corruption** in code combining partial application (curried
   multi-arg calls), tuple-destructuring over a `List`, and `::`-accumulation recursion — observed
   as both silently wrong field values (a string from one token bleeding into another) and an OOM
   crash from a corrupted length field breaking loop termination. Suspected regression from commit
   `c59c9ff` (reuse-conservatism relaxation for partial-application folds). Fix in progress as of
   2026-07-24; until landed, self-hosted code should avoid table-driven dispatch via curried helpers
   over tuple lists — use explicit `if`/`match` chains instead.
3. **Likely the same root cause as #2, smaller trigger**: in an expression that both calls a
   large (~60-arm) `match`-returning-a-string-constant function on one field of a record and reads
   a *different* field of that same record, the field read comes back corrupted — it reads back as
   the match function's result instead of the actual field value. `t.text` alone is correct;
   `tokenKindName(t.kind) + "/" + t.text` in the same expression corrupts `t.text`. Did not
   reproduce in a hand-shrunk 2-constructor/2-field version, so scale (arm count, or going through
   list-destructuring `t :: rest` rather than a directly-constructed record) seems to matter.
   Reported as additional evidence to the bug-2 fix effort rather than opening a third, separate
   one — folded into that fix's scope as of 2026-07-24. Doesn't affect any real corpus file (only
   surfaces on malformed/`TokBad` input, which valid programs never produce), so non-blocking for
   milestone 1's result below, but self-hosted code should avoid this shape too until it's fixed.

### Milestone 1 (lexer) result — 2026-07-24

`selfhost/Frontend/` (`ashes.json` + `Tokens.ash` + `Lexer.ash` + `Main.ash`) is a complete,
working port of `src/Ashes.Frontend/Lexer.cs` + `Tokens.cs`, validated with the differential
strategy above via `selfhost/tools/LexDump` (C# side) + `selfhost/tools/diff-lex.sh`:

- **Full corpus** (616 `.ash` files: `tests/`, `lib/`, `examples/`): 572/616 exact token-stream
  matches. All 44 differences are the UTF-8-byte-vs-UTF-16-char-unit position divergence scoped
  in this doc's language/stdlib table above (`position`/`length` only; confirmed every mismatching
  file contains non-ASCII bytes, almost always in comment headers) — not a lexer defect.
- **41 edge-case fixtures** extracted verbatim from the C# lexer's own unit tests
  (`src/Ashes.Tests/LexerTests.cs`, `LexerEdgeCaseTests.cs`, `AndKeywordLexerTests.cs`, one fixture
  file per unique literal input under `selfhost/tests/lexer-fixtures/`): 37/41 exact matches. 2 are
  the unsigned-literal-overflow scoping decision above (`256u8`, `18446744073709551615u64`
  (`u64::MAX`) — Ashes' `Int` can't represent the full unsigned range so out-of-range values aren't
  clamped/flagged the way the C# lexer's per-bit-width validation does). 2 are bug 3 above.

Net: the self-hosted lexer is correct on every real program in the repository; the only gaps are
the pre-declared scoping decisions plus bug 3 (malformed-input-only, folded into bug 2's fix).

**Current status (2026-08-13):** the historical result above is no longer reproducible as-is. The
`Rune` migration removed `Ashes.Text.isLetter`, `isDigit`, and `isWhiteSpace`, which the self-hosted
lexer still imports, so the differential harness currently fails while compiling `Lexer.ash`. Migrate
its byte-oriented ASCII recognition to the current `Rune`/byte APIs, decide the source-coordinate
contract, then rerun all fixtures and the current 702-file corpus. Bug 1 is merged and no longer a
blocker; bugs 2 and 3 must be reclassified from current evidence after that replay rather than kept as
an indefinite “fix in progress.” The next implementation slice is the parser/remaining Frontend, not
the linker, unless the binary-construction prerequisite lands first.
