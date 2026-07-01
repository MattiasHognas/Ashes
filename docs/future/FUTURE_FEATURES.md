# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/`](../) — syntax/semantics in
[LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md), library APIs in [STANDARD_LIBRARY.md](../STANDARD_LIBRARY.md),
and runtime/backend behavior in [ARCHITECTURE.md](../ARCHITECTURE.md) — not here. (Text parsing,
async TCP/HTTP, HTTPS/TLS, and brace-free records were landed and their design docs retired into
those.)

> 🔧 **In-progress work:** internal compiler optimizations — see
> **[COMPILER_OPTIMIZATION.md](COMPILER_OPTIMIZATION.md)**. In-place reuse, deterministic resource
> safety, and structured parallelism (all three targets) are landed; the remaining optimization
> backlog (data-parallel `map`/`reduce`, the move/linearity analysis, arm64 networking+parallelism
> coexistence, and a few smaller items) lives there under stable IDs `CO-1`…`CO-6`.

| Feature | Status | Description |
|---------|--------|-------------|
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | In progress | LLVM passes, memory management, codegen. Landed: the audit roadmap (decision-tree matching, string-literal interning, mutual-recursion TCO, jump-table relocations) **plus** Perceus-style in-place reuse, deterministic resource safety, and structured parallelism on all three targets. Remaining optimization backlog tracked there as `CO-1`…`CO-6` (data-parallel `map`/`reduce`, move/linearity analysis, arm64 networking+parallelism, use-after-close diagnostic, tunables) |
| [Effects](EFFECTS.md) | Planned | Algebraic effect handlers — typed effect rows (`uses { ... }`), lexical handlers, optional `perform`, inferred operation/handler types, one-shot/tail-resumptive continuations. Basis for capabilities, DI/testability, typed errors, and async. Multi-shot deferred (no-GC) |
| [Inline Modules](INLINE_MODULES.md) | Planned | Nested, named `module Name =` declarations inside a file — pure compile-time namespacing, no runtime representation; transparent inline ↔ file promotion |
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
