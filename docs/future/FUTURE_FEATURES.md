# Future Features

Planned features and future work for the Ashes language and ecosystem.

> 🔧 **In-progress work:** In-Place Reuse (#2). The shared deep-copy foundation, the reuse
> primitive, and the **direct-accumulator** in-place reuse are landed and verified; the remaining
> piece is the `Map.set`-group **specialization** for indirect reuse (the 1BRC fold). Full design,
> the concrete leak discriminator, and the four confirmed obstacles are in
> **[REUSE_ANALYSIS.md](REUSE_ANALYSIS.md)**. (Structured Parallelism #5 and Resource Safety are
> done.)

| Feature | Status | Description |
|---------|--------|-------------|
| [Text Parsing Primitives](TEXT_PARSING_PRIMITIVES.md) | Landed | Landed as `Ashes.Text.uncons`, `Ashes.Text.parseInt`, and `Ashes.Text.parseFloat`; recursive user-space JSON parser smoke coverage proves the surface; follow-on text helpers remain deferred |
| [Async Networking](ASYNC_NETWORKING.md) | Landed | Async-only TCP/HTTP inside `async`; core non-blocking runtime landed, separate packaged runtime remains deferred |
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Landed | LLVM passes, memory management, codegen improvements. Roadmap complete: decision-tree pattern matching, compile-time string-literal interning, mutual-recursion TCO, and LLVM jump-table relocation support in the image linkers. Further codegen improvements remain welcome but the audit roadmap is done |
| [HTTPS/TLS](HTTPS_TLS.md) | Landed | Transparent `https://` in `Ashes.Http` and the public `Ashes.Net.Tls` surface now ride on the hermetic `rustls` runtime across `linux-x64`, `linux-arm64`, and `win-x64` |
| [Brace-Free Records](BRACE_FREE_RECORDS.md) | Landed | Curly-brace record declaration/construction/update forms replaced with pipe-style declarations, named constructor calls, and bare `with` updates; old `{ ... }` forms now report a migration diagnostic |
| [Structured Parallelism](STRUCTURED_PARALLELISM.md) | Landed (linux-x64) | `Ashes.Parallel.both` is **genuinely parallel on linux-x64** — GS-segment per-thread arenas + `clone`/`futex` worker threads + deep-copy-on-join, deterministic, memory-bounded, ~2x speedup. Concrete result types fork to a worker; abstract/polymorphic uses (and `map`/`reduce`, whose element type is abstract inside the polymorphic body) run sequentially — full data-parallel `map`/`reduce` would need monomorphization. arm64/win-x64 run `both` inline |
| [In-Place Reuse Analysis](REUSE_ANALYSIS.md) | Largely landed | Perceus-style in-place reuse without runtime refcounting. Landed, sound, and constant-memory bounded for **pure-rewrite folds**: direct-accumulator reuse, helper-rebuild inlining, recursive-function specialization, and the full **`Map.set` shape** (multi-param / nested-recursive-returning / helper-rebuilding / intermediate-value linearity). A defensive entry deep-copy makes the accumulator uniquely owned; a conservative `IsFullyReusing` gate guards the per-iteration arena reset. **Remaining:** the insert path of an insert-or-update `Map.set` — a fresh node for a new key lands above the watermark and blocks the reset; needs a to-space / persistent region for genuinely-new cells (detailed in the doc) |
| [Resource Safety](RESOURCE_SAFETY.md) | Landed | Deterministic file/socket/process cleanup vs Ground Rule 6. All gaps fixed & verified: affine ownership + recursive `Drop` for resource-bearing aggregates, move-on-destructure/construction, TCO back-edge resource drops, `Process` reaping, and deterministic close of resources captured by escaping closures (dropper at `closure+24`) |
| Selective Imports | Partial | Single-binding form `import M.binding [as x]` is landed; only the grouped multi-name form `import Ashes.IO (print, ...)` remains |
| [Effects](EFFECTS.md) | Planned | Algebraic effect handlers — typed effect rows (`uses { ... }`), lexical handlers, optional `perform`, inferred operation/handler types, one-shot/tail-resumptive continuations. Basis for capabilities, DI/testability, typed errors, and async. Multi-shot deferred (no-GC) |
| Inline Modules | Planned | Inline module declarations |
| Ashes.String | Landed | Standard library string utilities (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`, `isLetter`, `isDigit`, `isWhiteSpace`) |
| [Ashes.Math](ASHES_MATH.md) | Planned | Two layers: a hermetic core (Int helpers, `sqrt`, Float arithmetic, constants — no library) plus native transcendentals (`sin`/`cos`/`exp`/`log`/…) from a **vendored, statically-embedded `libm`** (the rustls model — compile-time only, no runtime dependency) |
| [Self-Hosting](SELF_HOSTING.md) | Exploratory | Rewrite the compiler in Ashes |

------------------------------------------------------------------------

## Ground Rules

1. **Spec first.** Update `LANGUAGE_SPEC.md` before implementing any new
   syntax or semantic rule.
2. **Layer discipline.** Respect the project dependency graph
   (Frontend → Semantics → Backend). Runtime behaviour never goes in
   Frontend.
3. **Test every invariant.** Each feature must ship with tests that prove
   the new guarantees.
4. **No user-visible `Drop`.** `Drop` is a compiler concept. Users see
   automatic cleanup.
5. **Purity preserved.** All values are immutable. There is no mutation.
   All APIs — standard library and user-defined — are pure: they return
   new values and never modify their arguments. There are no in-place
   updates visible to user code.
6. **No GC.** All resource and memory management is deterministic and
   compile-time verified.
