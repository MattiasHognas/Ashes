# Future Features

Planned features and future work for the Ashes language and ecosystem.

| Feature | Status | Description |
|---------|--------|-------------|
| [Text Parsing Primitives](TEXT_PARSING_PRIMITIVES.md) | Landed | Landed as `Ashes.Text.uncons`, `Ashes.Text.parseInt`, and `Ashes.Text.parseFloat`; recursive user-space JSON parser smoke coverage proves the surface; follow-on text helpers remain deferred |
| [Async Networking](ASYNC_NETWORKING.md) | Landed | Async-only TCP/HTTP inside `async`; core non-blocking runtime landed, separate packaged runtime remains deferred |
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Landed | LLVM passes, memory management, codegen improvements. Roadmap complete: decision-tree pattern matching, compile-time string-literal interning, mutual-recursion TCO, and LLVM jump-table relocation support in the image linkers. Further codegen improvements remain welcome but the audit roadmap is done |
| [HTTPS/TLS](HTTPS_TLS.md) | Landed | Transparent `https://` in `Ashes.Http` and the public `Ashes.Net.Tls` surface now ride on the hermetic `rustls` runtime across `linux-x64`, `linux-arm64`, and `win-x64` |
| [Brace-Free Records](BRACE_FREE_RECORDS.md) | Landed | Curly-brace record declaration/construction/update forms replaced with pipe-style declarations, named constructor calls, and bare `with` updates; old `{ ... }` forms now report a migration diagnostic |
| [Structured Parallelism](STRUCTURED_PARALLELISM.md) | Approved | Deterministic CPU parallelism over pure functions (`Ashes.Parallel.both`/`map`/`reduce`); per-thread arenas + result copy-out via raw clone/futex. Signed off; implementation pending |
| [In-Place Reuse Analysis](REUSE_ANALYSIS.md) | Approved | Compile-time linearity ⇒ in-place reuse of uniquely-owned values; fixes the hot-loop arena leak (FLAWS #2) and O(1) maps (#3) with no new syntax. Shares the deep-copy emitter with Structured Parallelism. Signed off; implementation pending |
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
