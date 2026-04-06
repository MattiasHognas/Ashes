# Future Features

The core language (ownership, borrowing, pattern matching, optimization)
is complete. Async/await with combinators (`all`, `race`, `sleep`) is
complete. Subsequent work focuses on runtime and ecosystem.

| Feature | Status |
|---------|--------|
| Async/Await | Complete |
| Async Networking | Planned — non-blocking TCP/HTTP inside `async` |
| Package Manager | Planned — ecosystem and dependency management |
| HTTPS/TLS | Planned — TLS, encryption, certificates |
| Pattern Guards | Planned — pattern matching enhancements |
| Type Annotations | Planned — user-written type annotations |
| Import Aliasing | Planned — `import Ashes.IO as IO` |
| Selective Imports | Planned — `import Ashes.IO (print)` |
| Effects / IO Types | Planned — effect system or IO types |
| Inline Modules | Planned — inline module declarations |
| Ashes.String | Planned — standard library string utilities |
| Ashes.Bytes | Planned — standard library byte utilities |
| Ashes.Net.Http | Planned — standard library HTTP module |
| Ashes.Math | Planned — standard library math utilities |

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

------------------------------------------------------------------------

## Async Networking

Convert existing blocking TCP/HTTP operations to non-blocking
variants inside `async` blocks, powered by a platform-specific
event loop (`epoll` on Linux, IO completion ports on Windows).

Deliverables:
- Non-blocking TCP connect/send/receive
- Async HTTP get/post atop async TCP
- Event loop runtime with I/O readiness
- Tests: concurrent HTTP requests, parallel TCP connections,
  async resource cleanup, error propagation across awaits
