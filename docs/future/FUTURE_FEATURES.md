# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/`](../) — syntax/semantics in
[LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md), library APIs in [STANDARD_LIBRARY.md](../STANDARD_LIBRARY.md),
and runtime/backend behavior in [ARCHITECTURE.md](../ARCHITECTURE.md) — not here. (Text parsing,
async TCP/HTTP, HTTPS/TLS, brace-free records, and `Ashes.Math` were landed and their design docs
retired into those.)

| Feature | Status | Description |
|---------|--------|-------------|
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Largely complete | LLVM passes, memory management, codegen. The audit roadmap and the whole 1BRC-driven optimization arc (Perceus-style in-place reuse + move/linearity elision, deterministic resource safety, structured parallelism on all three targets, byte-level parsing, SIMD `memchr` scan, zero-copy `mmap` input, data-parallel chunked fold, loop-invariant reset-safety) have **landed** — the full 1e9-row 1BRC now runs (~2m36s / 15.9 GB). See *Completed Work* there; a few concrete follow-up tasks (`CO-15`…`CO-18`) remain in its *Roadmap* |
| [Effects](EFFECTS.md) | Planned | Algebraic effect handlers — typed effect rows (`uses { ... }`), lexical handlers, optional `perform`, inferred operation/handler types, one-shot/tail-resumptive continuations. Basis for capabilities, DI/testability, typed errors, and async. Multi-shot deferred (no-GC) |
| [Inline Modules](INLINE_MODULES.md) | Planned | Nested, named `module Name =` declarations inside a file — pure compile-time namespacing, no runtime representation; transparent inline ↔ file promotion |
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
