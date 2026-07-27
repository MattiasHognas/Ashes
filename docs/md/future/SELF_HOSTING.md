# Self-Hosting: Building the Ashes Compiler in Ashes

Status as of 2026-07-24. This tracks whether Ashes-the-language and its stdlib have what a
from-scratch Ashes-the-compiler needs. See [FUTURE_FEATURES.md](FUTURE_FEATURES.md) for how this
fits the broader roadmap.

## Language/stdlib prerequisites

| Capability | Complete |
|---|---|
| Unsigned integer support (`u8`, `u16`, `u32`, `u64`) | Yes |
| Byte type (`u8`) and byte literals | Yes |
| Bitwise operators (`&`, `\|`, `^`, `<<`, `>>`, `~`) | Yes |
| Numeric text conversions (`parseInt`, `parseFloat`, `fromInt`, `fromFloat`, `toHex`) | Yes |
| FFI surface (`external` functions/types, pointer signatures, symbol@library imports) | Partial — see [FFI gap](#gap-ffi-struct-callback-varargs) |
| Immutable `Bytes` type with indexed access and append helpers | Yes |
| Little-endian byte encode/decode helpers (`u16/u32/u64`) | Yes |
| Binary file output (`Ashes.IO.File.writeBytes`) | Yes |
| String helper module (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`, char predicates) | Yes |
| Persistent immutable map (`Ashes.Collection.Map`) | Yes |
| Persistent immutable array (`Ashes.Collection.Array`) | Yes |
| Persistent immutable set | No — no `Collection.Set` module; usable today as `Map(K, ())` |
| Generic hashing (non-`Str` keys) | No — `HashMap`/`HashTrie` are `Str`-keyed only (FNV-1a via `Ashes.Byte.hash`); other key types fall back to the AVL `Map`, which is workable but not O(1) |
| String-builder / rope type for large generated text | No explicit type, but `Text.join` does divide-and-conquer concat and the CO-36 affine-growth optimizer makes tail-recursive accumulator patterns amortized-linear — the *pattern* works, there's just no named abstraction |
| Records and record-update syntax | Yes |
| User-written type annotations | Yes |
| Project/module compilation support across multiple files | Yes |
| Catchable error propagation for compile pipeline flows | Yes |
| Memory model: RC-Perceus (deterministic destruction, no GC) | Yes, shipped (PR #294) — but admission is acyclic-only ([architecture.md](../internals/architecture.md)); a self-hosted AST/symbol table is naturally tree-shaped so this is fine, but any back-references (e.g. parent pointers) need a design that avoids cycles |
| Large-ADT exhaustiveness/performance hardening | Yes |
| JSON parsing/serialization support for `ashes.json` and JSON-RPC (CLI/LSP/DAP) | Yes |
| Stdio JSON-RPC framing utilities (Content-Length read/write over byte streams) for LSP/DAP | Yes |
| Interactive subprocess control with piped stdin/stdout/stderr, async reads, and request timeouts (DAP debugger backends) | Yes |
| Regex utilities/module for protocol and tooling text parsing (import/project/LSP/DAP parsing paths) | Yes |

The stdlib gaps (Set, generic hashing, string-builder) are mechanical additions — not attempted yet,
but nothing here is architecturally blocked.

## Structural gaps (beyond stdlib checklist)

These aren't stdlib features — they're properties of how the *current C# compiler* is built that a
pure-immutable, no-mutation Ashes rewrite has to work around.

### Gap: HM type inference is built on mutable union-find

`Lowering.TypeInference.cs` implements unification via a single mutable
`Dictionary<int, TypeRef> _subst` field (`Lowering.cs:525`), with in-place path compression in
`Prune` (`Lowering.TypeInference.cs:224`) and in-place binding in `Unify`
(`Lowering.TypeInference.cs:257`). This is interleaved with a mutable scope/ownership stack across
roughly 2,100 lines. Porting this to pure-immutable Ashes means threading an immutable
substitution map through every call site instead of mutating in place — well-understood CS
(persistent union-find), but a genuine redesign of the inference core, not a mechanical translation.
Budget this as its own milestone, most likely concurrent with or just before a self-hosted Semantics
layer.

### Gap: FFI — struct-by-value, callbacks, varargs {#gap-ffi-struct-callback-varargs}

`Ashes.Backend` talks to LLVM through ~145 direct LLVM-C API P/Invoke bindings
(`src/Ashes.Backend/Llvm/Interop/LlvmApi.cs`), not textual IR + a `clang`/`llc` subprocess. Ashes'
own `external` mechanism only expresses `Int | UInt(bits) | Float | Float32 | Bool | Str |
Opaque(name) | Ptr(pointee) | Void` (`src/Ashes.Semantics/Ir.cs:1605-1627`, enforced in
`Lowering.Symbols.cs:347-404`) — no struct-by-value marshaling, no function-pointer/callback types,
no varargs. A meaningful slice of the LLVM-C surface needs at least struct-by-value and callback
support, so a self-hosted backend cannot bind libLLVM as-is today.

**Decision (2026-07-24): extend Ashes' FFI rather than switch the self-hosted backend to
textual-IR-plus-subprocess.** This keeps the self-hosted backend architecturally aligned with the
current one (direct LLVM-C calls) instead of introducing a second codegen strategy. Scope for the
extension, to be turned into a proper spec change in `LANGUAGE_SPEC.md` before implementation
(per the project's normal feature-implementation flow):

- **Struct-by-value**: a new `FfiType` variant for fixed-layout structs (field list with FFI
  types), plus ABI-correct lowering in the backend (System V x86-64 / AArch64 AAPCS64 / Windows x64
  classification rules for register-vs-memory passing — this is the hard part, not the type-system
  surface).
- **Callbacks / function pointers**: a way to declare a named FFI function-pointer *type*
  (parameter/return `FfiType`s) usable as a parameter type in an `external` declaration, plus
  codegen support for taking the address of an Ashes function and marshaling it as that pointer
  type. LLVM-C's diagnostic-handler and pass-callback APIs need this.
- **Varargs**: lower priority — check first whether the ~145 functions actually used by the
  current backend include any varargs entry points before spending design effort here.
- Out of scope for the first pass: the full generality of arbitrary C ABI (unions, bitfields,
  variable-length arrays) — scope to exactly what LLVM-C's headers require.

This is tracked as a parallel-track design item, independent of the Frontend/Linker milestones below.

## Recommended rewrite order

Given the dependency DAG (`Frontend` → `Semantics` → `Backend`, `Frontend` has zero internal
dependencies) and component size (`Frontend` ~3.9k LOC vs. `Semantics` ~43.7k vs. `Backend`
~33.5k), the lowest-risk path to validate the self-hosting approach is:

1. **Frontend (lexer + parser) first.** Smallest surface, no FFI, no HM inference, so it hits
   neither structural gap above. Validate with a differential test: run the same corpus
   (`tests/`, `lib/`, `examples/`) through both the existing C# frontend and the Ashes rewrite,
   diff a serialized AST (the existing JSON module is sufficient) between them. This also answers
   the open question of whether recursion-only/no-loop code performs acceptably at parser scale.
2. **Linker second.** `LlvmImageLinkerElf*.cs` / `LlvmImageLinkerPe*.cs` (~4,550 lines total) are
   pure algorithmic C# — buffer parsing, relocation math, binary writing — with no LLVM or OS API
   dependency. Good second slice: exercises `Bytes`/`Byte` manipulation at real scale, independent
   of both structural gaps.
3. **Semantics third**, once the immutable-substitution redesign for type inference is scoped as
   its own milestone.
4. **Backend last**, gated on the FFI extension above landing.

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

### Test-coverage strategy: reuse the existing ~2,000 tests, don't re-derive them

The C# suite (`Ashes.Tests`, ~1,570 `[Test]` methods) plus the e2e `.ash` corpus (579 files in
`tests/`, 17 in `lib/`, 17 in `examples/` as of 2026-07-24) is the real spec. Each self-hosted
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

**Status**: paused pending bugs 1 and 2 landing (per-repo convention: fix real bugs found this way
before continuing, not just work around and move on). Once fixed, next steps: swap the lexer's
table-dispatch workaround back to the idiomatic tuple-table version bug 2 was originally found in
(to confirm the fix), then start milestone 2 (the linker, per the rewrite order above).
