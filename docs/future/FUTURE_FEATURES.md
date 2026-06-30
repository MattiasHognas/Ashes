# Future Features

Planned features and future work for the Ashes language and ecosystem.

> 🔧 **In-progress work with a clear handoff:** Structured Parallelism (#5) and In-Place
> Reuse (#2) are partly built (shared deep-copy foundation done). To resume, read
> **[RESUME_HERE.md](RESUME_HERE.md)** — it has what's done, what's left, and the next step.

| Feature | Status | Description |
|---------|--------|-------------|
| [Text Parsing Primitives](TEXT_PARSING_PRIMITIVES.md) | Landed | Landed as `Ashes.Text.uncons`, `Ashes.Text.parseInt`, and `Ashes.Text.parseFloat`; recursive user-space JSON parser smoke coverage proves the surface; follow-on text helpers remain deferred |
| [Async Networking](ASYNC_NETWORKING.md) | Landed | Async-only TCP/HTTP inside `async`; core non-blocking runtime landed, separate packaged runtime remains deferred |
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Landed | LLVM passes, memory management, codegen improvements. Roadmap complete: decision-tree pattern matching, compile-time string-literal interning, mutual-recursion TCO, and LLVM jump-table relocation support in the image linkers. Further codegen improvements remain welcome but the audit roadmap is done |
| [HTTPS/TLS](HTTPS_TLS.md) | Landed | Transparent `https://` in `Ashes.Http` and the public `Ashes.Net.Tls` surface now ride on the hermetic `rustls` runtime across `linux-x64`, `linux-arm64`, and `win-x64` |
| [Brace-Free Records](BRACE_FREE_RECORDS.md) | Landed | Curly-brace record declaration/construction/update forms replaced with pipe-style declarations, named constructor calls, and bare `with` updates; old `{ ... }` forms now report a migration diagnostic |
| [Structured Parallelism](STRUCTURED_PARALLELISM.md) | In progress | `Ashes.Parallel.both`/`map`/`reduce` shipped (deterministic; **sequential for now**); shared deep-copy foundation (`Ashes.Internal.deepCopy`) done; **freestanding per-thread arena (linux-x64, GS-segment TCB) done & verified** (#5 step 1). Remaining: `clone`/`futex` worker spawn/join to make `both` genuinely parallel |
| [In-Place Reuse Analysis](REUSE_ANALYSIS.md) | In progress | Shared deep-copy foundation done (recursive ADT copiers via `Ashes.Internal.deepCopy`). Remaining: interprocedural linearity analysis + reuse tokens + TCO arena-reset integration (the in-place fast path) |
| [Resource Safety](RESOURCE_SAFETY.md) | Proposed | Audit of file/socket/process cleanup vs Ground Rule 6 (compile-time-verified, deterministic). Current scope-drop is sound only for directly-bound, non-escaping, non-nested resources; gaps found (nested-in-aggregate leaks, escape-via-capture use-after-close, `Process` undropped). Plan: affine ownership + move analysis (shares the #2 linearity engine) + recursive `Drop` |
| Pattern Guards | Planned | Pattern matching enhancements |
| Type Annotations | Planned | User-written type annotations |
| Selective Imports | Planned | `import Ashes.IO (print)` |
| Effects / IO Types | Planned | Effect system or IO types |
| Inline Modules | Planned | Inline module declarations |
| Ashes.String | Landed | Standard library string utilities (`substring`, `length`, `indexOf`, `startsWith`, `contains`, `split`, `trim`, `isLetter`, `isDigit`, `isWhiteSpace`) |
| Ashes.Bytes | Planned | Standard library byte utilities |
| Ashes.Net.Http | Planned | Standard library HTTP module |
| Ashes.Math | Planned | Standard library math utilities |
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
