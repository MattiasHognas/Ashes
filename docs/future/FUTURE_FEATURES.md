# Future Features

Planned features and future work for the Ashes language and ecosystem.

| Feature | Status | Description |
|---------|--------|-------------|
| [Text Parsing Primitives](TEXT_PARSING_PRIMITIVES.md) | Landed | Landed as `Ashes.Text.uncons`, `Ashes.Text.parseInt`, and `Ashes.Text.parseFloat`; recursive user-space JSON parser smoke coverage proves the surface; follow-on text helpers remain deferred |
| [Async Networking](ASYNC_NETWORKING.md) | Landed | Async-only TCP/HTTP inside `async`; core non-blocking runtime landed, separate packaged runtime remains deferred |
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Ongoing | LLVM passes, memory management, codegen improvements |
| HTTPS/TLS | Planned | TLS, encryption, certificates |
| Pattern Guards | Planned | Pattern matching enhancements |
| Type Annotations | Planned | User-written type annotations |
| Selective Imports | Planned | `import Ashes.IO (print)` |
| Effects / IO Types | Planned | Effect system or IO types |
| Inline Modules | Planned | Inline module declarations |
| Ashes.String | Planned | Standard library string utilities |
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
