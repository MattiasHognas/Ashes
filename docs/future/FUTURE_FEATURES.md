# Future Features

Planned features and future work for the Ashes language and ecosystem.

| Feature | Description |
|---------|-------------|
| [Async Networking](ASYNC_NETWORKING.md) | Non-blocking TCP/HTTP inside `async` |
| [Package Manager](PACKAGE_MANAGER.md) | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | LLVM passes, memory management, codegen improvements |
| HTTPS/TLS | TLS, encryption, certificates |
| Pattern Guards | Pattern matching enhancements |
| Type Annotations | User-written type annotations |
| Selective Imports | `import Ashes.IO (print)` |
| Effects / IO Types | Effect system or IO types |
| Inline Modules | Inline module declarations |
| Ashes.String | Standard library string utilities |
| Ashes.Bytes | Standard library byte utilities |
| Ashes.Net.Http | Standard library HTTP module |
| Ashes.Math | Standard library math utilities |
| [Self-Hosting](SELF_HOSTING.md) | Rewrite the compiler in Ashes |

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
