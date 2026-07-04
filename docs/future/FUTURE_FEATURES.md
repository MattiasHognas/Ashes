# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/`](../) — syntax/semantics in
[LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md), library APIs in [STANDARD_LIBRARY.md](../STANDARD_LIBRARY.md),
and runtime/backend behavior in [ARCHITECTURE.md](../ARCHITECTURE.md) — not here.

| Feature | Status | Description |
|---------|--------|-------------|
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Largely complete | LLVM passes, memory management, codegen. The audit roadmap and the whole 1BRC-driven optimization arc (Perceus-style in-place reuse + move/linearity elision, deterministic resource safety, structured parallelism on all three targets, byte-level parsing, SIMD `memchr` scan, zero-copy `mmap` input, data-parallel chunked fold, loop-invariant reset-safety) have **landed** — the full 1e9-row 1BRC now runs (~2m36s / 15.9 GB). See *Completed Work* there; a few concrete follow-up tasks (`CO-15`…`CO-18`) remain in its *Roadmap* |
| [Server Support](SERVER_SUPPORT.md) | Planned | Add first-class HTTP server support for native Ashes programs, keeping the API explicit and functional while preserving Ashes’ no-external-runtime model |
| [Unified Capabilities](UNIFIED_CAPABILITIES.md) | Partial | **Shipped**: effects renamed to capabilities (`effect`→`capability`, `uses`→`needs`, `ASH025` rename diagnostic); dynamic `handle` (unchanged); and static `provide Capability(args) = ...` for **concrete** instances — the full dependency-injection story (`provide Clock`/`Random`/`Env`) and concrete parameterized instances (`Ord(Str)`) resolve with no handler, with provider/handler ambiguity (`ASH027`) and duplicate/incomplete-provider (`ASH026`) checks. See [LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md) §20. **Remaining**: resolving a requirement at a *generic* instance (an `Ord(a)` call inside a polymorphic function) — needs monomorphization or dictionary passing (dynamic dispatch on an erased type is impossible without RTTI); plus provider import/export across modules |
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
