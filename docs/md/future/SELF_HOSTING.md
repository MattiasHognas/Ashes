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
appear only where the test infrastructure itself consumes the capability.

| Capability | Status | Areas |
|---|---|---|
| Unsigned integer support (`u8`, `u16`, `u32`, `u64`) | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| Byte type (`u8`) and byte literals | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker` |
| Bitwise operators (`&`, `\|`, `^`, `<<`, `>>`, `~`) | Complete | `Compiler/Backend`, `Compiler/Linker` |
| Numeric text conversions (`parseInt`, `parseFloat`, `fromInt`, `fromFloat`, `toHex`) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI` |
| Basic FFI (`external` functions/types, pointers, resources, `symbol@library`) | Partial — see [LLVM FFI gap](#gap-ffi-native-arrays-out-parameters-and-foreign-buffers) | `Compiler/Backend` |
| LLVM native arrays, out parameters, returned strings, and foreign buffers | Required — see [LLVM FFI gap](#gap-ffi-native-arrays-out-parameters-and-foreign-buffers) | `Compiler/Backend` |
| Immutable `Bytes` with indexed reads and append helpers | Complete | `Compiler/Frontend`, `Compiler/Backend`, `Compiler/Linker`, `LSP`, `DAP` |
| Little-endian byte encode/decode helpers (`u16/u32/u64`) | Complete | `Compiler/Linker`, `DAP` |
| Efficient preallocation, range copy, and random-access binary patching | Required — see [binary-construction gap](#gap-efficient-immutable-binary-construction) | `Compiler/Linker` |
| Binary file output (`Ashes.IO.File.writeBytes`) | Complete | `Compiler/Linker`, `CLI`, `TestRunner`, `Fuzzing` |
| Path normalization, joining, parent/basename, and relative paths | Required — see [host-tool API gap](#gap-host-tool-filesystem-and-process-control) | `Compiler/Semantics`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| Current/executable/temp/cache directories and environment lookup | Required — see [host-tool API gap](#gap-host-tool-filesystem-and-process-control) | `Compiler/Semantics`, `Compiler/Backend`, `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| Directory enumeration, creation, deletion, and atomic rename | Required — see [host-tool API gap](#gap-host-tool-filesystem-and-process-control) | `Compiler/Semantics`, `CLI`, `TestRunner`, `Fuzzing` |
| Marking emitted ELF files executable | Required — see [host-tool API gap](#gap-host-tool-filesystem-and-process-control) | `CLI`, `TestRunner`, `Fuzzing` |
| stderr output and controlled process exit codes | Required — see [host-tool API gap](#gap-host-tool-filesystem-and-process-control) | `CLI`, `LSP`, `DAP`, `TestRunner`, `Fuzzing` |
| String helpers (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter`, `CLI`, `LSP`, `DAP` |
| Unicode scalar classification through `Rune` | Complete; self-hosted lexer migration pending | `Compiler/Frontend`, `Formatter`, `LSP` |
| Canonical UTF-8 source offsets and UTF-16/LSP coordinate conversion | Design required — see [source-coordinate gap](#gap-source-coordinate-contract) | `Compiler/Frontend`, `Formatter`, `LSP`, `DAP` |
| Persistent immutable map (`Ashes.Collection.Map`) | Complete | `Compiler/Semantics`, `Compiler/Backend`, `LSP`, `DAP` |
| Persistent immutable array (`Ashes.Collection.Array`) | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Compiler/Backend`, `Formatter` |
| Persistent immutable set | Optional — use `Map(K, Unit)` initially | `Compiler/Semantics`, `LSP` |
| Generic hashing for non-`Str` keys | Optional — use `Map` initially | `Compiler/Semantics`, `LSP` |
| Named string-builder or rope | Optional — `Text.join` and affine-growth reuse are workable initially | `Compiler`, `Formatter`, `CLI`, `LSP`, `DAP` |
| Records, named patterns, and record-update syntax | Complete | `Compiler`, `Formatter`, `LSP` |
| User-written type annotations, aliases, and zero-cost nominal types | Complete | `Compiler`, `Formatter`, `LSP` |
| Project/module compilation with explicit exports and path dependencies | Complete in the C# compiler; host path APIs still required by the port | `Compiler/Semantics`, `CLI`, `LSP`, `TestRunner`, `Fuzzing` |
| Catchable error propagation for compile-pipeline flows | Complete | `Compiler`, `Formatter`, `CLI`, `LSP`, `DAP` |
| RC-Perceus deterministic memory without cyclic graphs | Complete; the port must avoid parent/back-reference cycles | `Compiler`, `Formatter`, `LSP`, `DAP` |
| Persistent immutable substitution and unification architecture | Required — see [HM inference gap](#gap-hm-type-inference-is-built-on-mutable-union-find) | `Compiler/Semantics` |
| Large-ADT exhaustiveness and performance hardening | Complete | `Compiler/Frontend`, `Compiler/Semantics`, `Formatter` |
| JSON parsing/serialization for `ashes.json` and JSON-RPC | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP` |
| Stdio JSON-RPC Content-Length framing | Complete | `LSP`, `DAP` |
| Interactive subprocess control with piped streams and timeouts | Complete | `DAP`, `TestRunner`, `Fuzzing` |
| Regex utilities for tooling text | Complete | `Compiler/Semantics`, `CLI`, `LSP`, `DAP`, `TestRunner` |
| Unit assertions plus deterministic test discovery/execution | Partial — `Ashes.Test` is complete; discovery still needs the host APIs above | `Tests`, `TestRunner` |
| Deterministic fuzz generation, replay, shrinking, corpus, and artifacts | Complete in the C# harness; porting it is not a compiler-core gate | `Fuzzing` |
| Tar/gzip, SHA-256, authenticated HTTP, and multipart upload | Required only for a full CLI replacement | `CLI/Registry` |
| Defined stage-0/stage-1/stage-2 bootstrap and reproducibility gate | Design required — see [bootstrap completion gate](#gap-bootstrap-completion-gate) | `Compiler`, `CLI`, `Tests`, `TestRunner`, `Fuzzing` |

The first self-hosted **compiler core** does not require `CLI/Registry`, `LSP`, `DAP`, `Fuzzing`, or
`TestRunner` parity. Those are separate replacement layers. Set, generic hashing, and a named text
builder are likewise not admission gates; measure the persistent alternatives before adding them.

## Structural gaps (beyond stdlib checklist)

These aren't stdlib features — they're properties of how the *current C# compiler* is built that a
pure-immutable, no-mutation Ashes rewrite has to work around.

### Gap: HM type inference is built on mutable union-find {#gap-hm-type-inference-is-built-on-mutable-union-find}

`Lowering.TypeInference.cs` implements unification via a single mutable
`Dictionary<int, TypeRef> _subst` field (`Lowering.cs:525`), with in-place path compression in
`Prune` (`Lowering.TypeInference.cs:224`) and in-place binding in `Unify`
(`Lowering.TypeInference.cs:257`). This is interleaved with a mutable scope/ownership stack across
roughly 2,100 lines. Porting this to pure-immutable Ashes means threading an immutable
substitution map through every call site instead of mutating in place — well-understood CS
(persistent union-find), but a genuine redesign of the inference core, not a mechanical translation.
Budget this as its own milestone, most likely concurrent with or just before a self-hosted Semantics
layer.

### Gap: FFI — native arrays, out parameters, and foreign buffers {#gap-ffi-native-arrays-out-parameters-and-foreign-buffers}

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
current one (direct LLVM-C calls) instead of introducing a second codegen strategy. Scope for the
extension, to be turned into a proper spec change in [the language reference](../reference/language.md) before implementation
(per the project's normal feature-implementation flow):

- **Native handle arrays**: marshal an Ashes collection of same-typed opaque handles as a stable
  pointer plus element count for the duration of one external call.
- **Out parameters**: return typed multiple results without exposing unrestricted source-level
  pointer arithmetic or mutation.
- **Foreign buffers and strings**: copy pointer-plus-length results into owned `Bytes`/`Str` values
  and pair each owned native allocation with its declared destructor.
- **Opaque binding facade**: keep raw LLVM externals file-local and export zero-cost nominal handle
  types plus checked wrapper functions. Context-owned child handles remain a trusted binding
  invariant unless a later scoped-borrow design can express their lifetime.
- **Audit before expansion**: the current binding contains no managed callback registration, and its
  LLVM struct construction passes arrays of handles rather than C structs by value. Do not add
  struct-by-value, callbacks, or varargs until an actually used LLVM-C signature requires them.

This is tracked as a parallel-track design item, independent of the Frontend/Linker milestones below.

### Gap: efficient immutable binary construction {#gap-efficient-immutable-binary-construction}

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

### Gap: host-tool filesystem and process control {#gap-host-tool-filesystem-and-process-control}

Single-file frontend experiments need only `readText`, but a compatible compiler must discover
projects, normalize paths, walk source roots, find its shipped `lib/` and runtime assets, create output
directories, write temporary files, and mark Linux output executable. The surrounding tools also need
stderr and controlled exit codes so a user compilation error is not reported as a language `panic`.

Add these as ordinary capability-tracked host APIs rather than ad-hoc compiler externals. Keep path
operations pure; filesystem acquisition and mutation carry `FileRead`/`FileWrite`, environment lookup
gets an explicit ambient-authority classification, and possession-based file-handle operations remain
unchanged.

### Gap: source-coordinate contract {#gap-source-coordinate-contract}

The C# frontend records UTF-16 string-unit offsets while the self-hosted lexer walks UTF-8 bytes.
Byte offsets are a natural internal identity for an UTF-8 compiler, but diagnostics, formatter edits,
debug information, and LSP positions need deliberate conversions. Choose one canonical compiler span
unit, specify line/column conversion (including malformed UTF-8), and negotiate or convert LSP UTF-8
and UTF-16 positions. A differential test may normalize representations only after this contract is
fixed; the divergence cannot remain an unexplained permanent exemption.

### Bootstrap completion gate {#gap-bootstrap-completion-gate}

Component differential tests prove compatibility but do not by themselves prove self-hosting. The
compiler-core roadmap completes only after:

1. the current C# compiler (stage 0) builds the Ashes compiler (stage 1);
2. stage 1 compiles the same compiler sources into stage 2;
3. stage 2 repeats the build, with stage-2/stage-3 output compared byte-for-byte when deterministic
   metadata permits it, otherwise by a documented structural equivalence;
4. the bootstrapped compiler passes the full unit-fixture and `.ash` corpus differential gates; and
5. the process is repeated for every supported host, with all four targets at least structurally
   validated and executable where the host supports them.

The gate must also define how the compiler locates `libLLVM`, standard-library sources, and the
vendored native/bitcode payloads. A compiler that can compile itself only from a repository-relative
working directory is an intermediate milestone, not a completed bootstrap.

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
