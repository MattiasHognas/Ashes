# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/`](../) — syntax/semantics in
[LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md), library APIs in [STANDARD_LIBRARY.md](../STANDARD_LIBRARY.md),
and runtime/backend behavior in [ARCHITECTURE.md](../ARCHITECTURE.md) — not here.

| Feature | Status | Description |
|---------|--------|-------------|
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Largely complete | LLVM passes, memory management, codegen. The audit roadmap and the whole 1BRC-driven optimization arc (Perceus-style in-place reuse + move/linearity elision, deterministic resource safety, structured parallelism on all three targets, byte-level parsing, SIMD `memchr` scan, zero-copy `mmap` input, data-parallel chunked fold, loop-invariant reset-safety) have **landed** — the full 1e9-row 1BRC now runs (~2m36s / 15.9 GB). See *Completed Work* there; a few concrete follow-up tasks (`CO-15`…`CO-18`) remain in its *Roadmap* |
| Effects | Largely complete | Algebraic effect handlers — effect declarations, typed effect rows (`uses { ... }`) with row-polymorphic inference, optional `perform`, compile-time unhandled-effect checking, tail-resumptive AND one-shot resumptive `handle ... with` (pre/post split — no continuation capture), first-class operation values, and effects across project modules. See [LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md) section 20 and the effects-lowering section of [ARCHITECTURE.md](../ARCHITECTURE.md). **Remaining**: per-thread handler evidence (with the TLS arena work) so effects compose with `Ashes.Parallel`; `handle` across `await`; retyping built-in IO as row-typed effects with default top-level handlers (`await` stays sugar). Aborting arms (no resume — needs unwinding) and multi-shot `resume` are permanently out of scope under the no-GC rule|
| [Inline Modules](INLINE_MODULES.md) | Planned | Nested, named `module Name =` declarations inside a file — pure compile-time namespacing, no runtime representation; transparent inline ↔ file promotion |
| [Natural Keywords](NATURAL_KEYWORDS.md) | Planned | Rename the three abbreviated keywords to full English words — `fun` → `given`, `rec` → `recursive`, `extern` → `external`; old spellings permanently reserved with a rename diagnostic. Principles: words for meaning, symbols for plumbing; no abbreviations. Corpus migrates via `fmt -w` |
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
