# Future Features

The core language (ownership, borrowing, pattern matching, optimization)
is complete. Subsequent work focuses on runtime and ecosystem.

| Feature | Area |
|---------|------|
| Async/Await | Async syntax and core primitives |
| Async Runtime | Scheduler, concurrency and runtime support |
| Networking | HTTP and TCP layers with async |
| Package Manager | Ecosystem and dependency management |
| HTTPS/TLS | TLS, encryption, certificates and security concerns |

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
