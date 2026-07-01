# Future Features

Planned features and future work for the Ashes language and ecosystem.

> 🔧 **In-progress work:** internal compiler optimizations — see
> **[COMPILER_OPTIMIZATION.md](COMPILER_OPTIMIZATION.md)**. In-place reuse, deterministic resource
> safety, and structured parallelism (all three targets) are landed; the remaining optimization
> backlog (data-parallel `map`/`reduce`, the move/linearity analysis, arm64 networking+parallelism
> coexistence, and a few smaller items) lives there under stable IDs `CO-1`…`CO-6`.

| Feature | Status | Description |
|---------|--------|-------------|
| [Text Parsing Primitives](TEXT_PARSING_PRIMITIVES.md) | Landed | Landed as `Ashes.Text.uncons`, `Ashes.Text.parseInt`, and `Ashes.Text.parseFloat`; recursive user-space JSON parser smoke coverage proves the surface; follow-on text helpers remain deferred |
| [Async Networking](ASYNC_NETWORKING.md) | Landed | Async-only TCP/HTTP inside `async`; core non-blocking runtime landed, separate packaged runtime remains deferred |
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | In progress | LLVM passes, memory management, codegen. Landed: the audit roadmap (decision-tree matching, string-literal interning, mutual-recursion TCO, jump-table relocations) **plus** Perceus-style in-place reuse, deterministic resource safety, and structured parallelism on all three targets. Remaining optimization backlog tracked there as `CO-1`…`CO-6` (data-parallel `map`/`reduce`, move/linearity analysis, arm64 networking+parallelism, use-after-close diagnostic, tunables) |
| [HTTPS/TLS](HTTPS_TLS.md) | Landed | Transparent `https://` in `Ashes.Http` and the public `Ashes.Net.Tls` surface now ride on the hermetic `rustls` runtime across `linux-x64`, `linux-arm64`, and `win-x64` |
| [Brace-Free Records](BRACE_FREE_RECORDS.md) | Landed | Curly-brace record declaration/construction/update forms replaced with pipe-style declarations, named constructor calls, and bare `with` updates; old `{ ... }` forms now report a migration diagnostic |
| [Effects](EFFECTS.md) | Planned | Algebraic effect handlers — typed effect rows (`uses { ... }`), lexical handlers, optional `perform`, inferred operation/handler types, one-shot/tail-resumptive continuations. Basis for capabilities, DI/testability, typed errors, and async. Multi-shot deferred (no-GC) |
| [Inline Modules](INLINE_MODULES.md) | Planned | Nested, named `module Name =` declarations inside a file — pure compile-time namespacing, no runtime representation; transparent inline ↔ file promotion |
| Ashes.String | Landed | Standard library string utilities (`length`, `take`, `drop`, `substring`, `indexOf`, `startsWith`, `contains`, `split`, `join`, `trim`, `trimStart`, `trimEnd`, `isLetter`, `isDigit`, `isWhiteSpace`, `compare`) |
| [Ashes.Math](ASHES_MATH.md) | Planned | Two layers: a hermetic core (Int helpers, `sqrt`, Float arithmetic, constants — no library) plus native transcendentals (`sin`/`cos`/`exp`/`log`/…) from a **vendored `libm` (openlibm), statically linked and dead-stripped** per binary — compile-time only, no runtime dependency |
| [Self-Hosting](SELF_HOSTING.md) | Exploratory | Rewrite the compiler in Ashes |

---

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
