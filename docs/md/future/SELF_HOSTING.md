# Self-Hosting: Building the Ashes Compiler in Ashes

Status as of 2026-08-13. This is a capability audit of what Ashes-the-language, its compiler/runtime,
and its standard library must provide before a compiler can be written in Ashes. It deliberately does
not track how much of the compiler has been ported, port milestones, or bootstrap progress. See
[FUTURE_FEATURES.md](FUTURE_FEATURES.md) for how self-hosting fits the broader roadmap.

## Language/stdlib prerequisites

`Required` means Ashes still needs a design or implementation. An area tag names the eventual
consumer, not the project that must implement the prerequisite. Tests are required for every
delivered change. `Complete` capabilities link to the normative documentation for the shipped
surface; `Partial`, `Required`, and `Design required` capabilities link to actionable Ashes work
packages below.

| Capability | Status | Areas |
|---|---|---|
| [Unsigned integer support (`u8`, `u16`, `u32`, `u64`)](../reference/language.md#_2-1-integers) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| [Byte type (`u8`) and byte literals](../reference/language.md#_2-1-integers) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| [Bitwise operators (`&`, `\|`, `^`, `<<`, `>>`, `~`)](../reference/language.md#_3-5-bitwise) | Complete | `Compiler/Backend`, `Compiler/Linker` |
| [Numeric text conversions (`parseInt`, `parseFloat`, `fromInt`, `fromFloat`, `toHex`)](../reference/standard-library.md#ashes-text) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI` |
| [Basic FFI (`external` functions/types, pointers, resources, `symbol@library`)](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Backend` |
| [Call-scoped native arrays of opaque handles](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Backend`, `LSP`, `Fuzzing` |
| [LLVM out parameters](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `LSP`, `Fuzzing` |
| [Returned native strings](../reference/language.md#_5-1-external-declarations) | Complete | `Compiler/Backend`, `LSP`, `Fuzzing` |
| [Foreign pointer-plus-length buffers](#gap-foreign-pointer-plus-length-buffers) | Required | `Compiler/Backend` |
| [Immutable `Bytes` with indexed reads and append helpers](../reference/standard-library.md#ashes-byte) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker`, `LSP`, `DAP` |
| [Little-endian byte encode/decode helpers (`u16/u32/u64`)](../reference/standard-library.md#ashes-byte) | Complete | `Compiler/Linker`, `DAP` |
| [Efficient preallocation, range copy, and random-access binary patching](#gap-efficient-immutable-binary-construction) | Required | `Compiler/Linker` |
| [Binary file output (`Ashes.IO.File.writeBytes`)](../reference/standard-library.md#ashes-io-file) | Complete | `Compiler/Linker`, `CLI`, `TestRunner`, `Fuzzing` |
| [Path normalization, joining, parent/basename, and relative paths](#gap-host-tool-filesystem-and-process-control) | Required | `Compiler/Semantics`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| [Current/executable/temp/cache directories and environment lookup](#gap-host-tool-filesystem-and-process-control) | Required | `Compiler/Semantics`, `Compiler/Backend`, `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| [Directory enumeration, creation, deletion, and atomic rename](#gap-host-tool-filesystem-and-process-control) | Required | `Compiler/Semantics`, `CLI`, `TestRunner`, `Fuzzing` |
| [Marking emitted ELF files executable](#gap-host-tool-filesystem-and-process-control) | Required | `CLI`, `TestRunner`, `Fuzzing` |
| [stderr output and controlled process exit codes](#gap-host-tool-filesystem-and-process-control) | Required | `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| [String helpers (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`)](../reference/standard-library.md#ashes-text) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI`, `LSP`, `DAP` |
| [Unicode scalar classification through `Rune`](../reference/standard-library.md#ashes-rune) | Complete | `Compiler/Frontend`, `Formatter`, `LSP` |
| [Canonical UTF-8 source offsets and UTF-16/LSP coordinate conversion](#gap-text-unicode-and-source-coordinates) | Design required | `Compiler/Frontend`, `Formatter`, `LSP`, `DAP` |
| [Persistent immutable map (`Ashes.Collection.Map`)](../reference/standard-library.md#ashes-collection-map) | Complete | `Compiler/Semantics`, `Compiler/Backend`, `LSP`, `DAP` |
| [Persistent immutable array (`Ashes.Collection.Array`)](../reference/standard-library.md#ashes-collection-array) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Compiler/Backend`, `Formatter` |
| [Records, named patterns, and record-update syntax](../reference/language.md#_4-3-record-types) | Complete | `Compiler`, `Formatter`, `LSP` |
| [User-written type annotations, aliases, and zero-cost nominal types](../reference/language.md#_4-2-zero-cost-nominal-types) | Complete | `Compiler`, `Formatter`, `LSP` |
| [Project/module compilation with explicit exports and path dependencies](../guide/projects.md) | Complete | `Compiler/Semantics`, `CLI`, `LSP` |
| [Catchable error propagation for compile-pipeline flows](../reference/language.md#_13-4-error-handling) | Complete | `Compiler`, `Formatter`, `CLI`, `LSP`, `DAP` |
| [RC-Perceus deterministic memory without cyclic graphs](../internals/architecture.md#memory-model) | Complete | `Compiler`, `Formatter`, `LSP`, `DAP` |
| [Large-ADT exhaustiveness and performance hardening](../reference/language.md#_11-pattern-matching) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter` |
| [JSON parsing/serialization for `ashes.json` and JSON-RPC](../reference/standard-library.md#ashes-text-json) | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP` |
| [Stdio JSON-RPC Content-Length framing](../reference/standard-library.md#ashes-net-rpc) | Complete | `LSP`, `DAP` |
| [Interactive subprocess control with piped streams and timeouts](../reference/standard-library.md#ashes-io-process) | Complete | `DAP`, `TestRunner`, `Fuzzing` |
| [Regex utilities for tooling text](../reference/standard-library.md#ashes-text-regex) | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP`, `TestRunner` |
| [`Ashes.Test` unit assertions](../reference/standard-library.md#ashes-test) | Complete | `Tests` |

Registry/package commands, LSP, DAP, TestRunner, fuzz-harness parity, bootstrap staging, persistent
sets, generic hashing, and a named text builder are not missing Ashes prerequisites. They may become
separate port or optimization work later, but are outside this capability audit until a concrete
language/runtime/stdlib blocker is demonstrated.

## Capability gaps and work packages

Each package below is the implementation hand-off for one or more incomplete rows in the table. A
package must add or specify an Ashes capability; work that only ports compiler code does not belong
here. Update the normative documentation before any language or API implementation.

### Gap: Foreign pointer-plus-length buffers

`Ashes.Backend` talks to LLVM through ~145 direct LLVM-C API P/Invoke bindings
(`src/Ashes.Backend/Llvm/Interop/LlvmApi.cs`), not textual IR + a `clang`/`llc` subprocess. Ashes'
own `external` mechanism can now materialize call-scoped contiguous arrays of opaque handles,
nullable opaque/pointer out parameters, and borrowed or owned native UTF-8 strings. Ashes code still
cannot copy a returned pointer-plus-length buffer into `Bytes`. The current C# adapter handles this
shape with explicit `Marshal`/copy operations; the representative blocker is
`LLVMTargetMachineEmitToMemoryBuffer`.

**Decision (2026-07-24): extend Ashes' FFI rather than require an Ashes compiler to use textual LLVM
IR plus a subprocess.** This preserves direct access to LLVM-C and avoids making process spawning an
accidental backend requirement. Specify the extension in the
[language reference](../reference/language.md) before implementation.

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

Deliver the remaining work as one reviewable slice: foreign binary buffers plus a minimal private
LLVM facade. It updates the language reference first, then Frontend, Formatter, Semantics/IR, Backend,
diagnostics, LSP, unit/e2e tests, and relevant fuzz generators. Do not add C structs by value,
callbacks, varargs, scalar out parameters, or public pointer arithmetic until an audited LLVM call
actually requires them. The package is done when an Ashes program uses the facade to construct a tiny
LLVM module and emit object bytes identical to the C# adapter on all four targets.

### Gap: efficient immutable binary construction

The native ELF/PE linkers do much more than append bytes: they preallocate output images, copy
sections into aligned offsets, patch headers and instructions, and apply relocations at arbitrary
positions. `Ashes.Byte` currently exposes indexed reads, concatenation, and endian encode/decode, but
no preallocation, range copy, or random-access write operation. Rebuilding the complete value for
every patch would make any immutable binary builder accidentally quadratic.

Specify a pure binary-building surface. Operations such as allocation,
range replacement, and `setU16Le`/`setU32Le`/`setU64Le` must return new `Bytes` values at the language
level while allowing the compiler to reuse a uniquely owned buffer internally.

Workable tasks:

1. Inventory every indexed read, aligned copy, patch, relocation write, and output-size calculation in
   the ELF and PE linkers; use that inventory to specify the minimum `Bytes` API.
2. Add pure allocation, range-copy/replacement, and checked random-access setters for `u8`, `u16`,
   `u32`, and `u64` in both endian orders actually used. Define out-of-range behavior explicitly.
3. Lower update chains so uniquely owned buffers can be reused internally while aliases preserve the
   old value; add ownership regressions that prove both paths.
4. Add model-based tests that compare every operation sequence with a simple list-backed reference,
   including aliasing, overlap, alignment, boundaries, and invalid ranges.
5. Extend builtin/lowering fuzz generators for the new operations and preserve minimized ownership or
   codegen failures as regressions.
6. Benchmark relocation-shaped patch workloads derived from the current ELF/PE linkers, measuring
   scaling, peak memory, and output equality on every target.

Done when the public API is specified, its functional/aliasing properties pass, all targets produce
identical bytes, and increasing patch counts does not produce quadratic time or memory growth.

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
6. Exercise the APIs with Ashes integration programs that discover a project fixture, locate assets
   from an installed-layout fixture, and atomically create output on Linux and Windows hosts.

Done when an Ashes program launched outside the repository can locate installed-layout fixtures,
atomically write an executable file, report an expected failure on stderr, and return a controlled
exit code on Linux and Windows.

### Gap: text, Unicode, and source coordinates

The current compiler records UTF-16 string-unit offsets, while an Ashes program naturally encounters
UTF-8 source bytes at file boundaries. Diagnostics, formatter edits, debug information, and LSP
positions therefore need a specified conversion contract that Ashes code can call directly.

Workable tasks:

1. Choose and document one canonical internal span unit, malformed-UTF-8 behavior, newline handling,
   and conversion rules for byte offsets, Unicode scalar columns, UTF-16/LSP positions, and debug
   locations.
2. Implement a shared line index and conversion API usable from Ashes and consumed by diagnostics,
   formatter edits, LSP, and DAP rather than duplicating conversions in each tool.
3. Add empty, ASCII, non-ASCII, combining-mark, astral, mixed-newline, and malformed-input fixtures
   for every conversion direction.
4. Add property tests for round trips at valid boundaries, monotonic positions, bounded invalid
   positions, and consistent line starts.
5. Update the current compiler, formatter, LSP, and DAP to use the shared contract, proving that the
   surface is sufficient for compiler tooling before self-hosting begins.

Done when token, AST, diagnostic, formatter, LSP, and debug spans all use the documented contract and
the conversion API is available to ordinary Ashes code.
