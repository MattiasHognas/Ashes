# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/md/`](../index.md) — syntax/semantics in
[Language Spec](../reference/language.md), library APIs in [Standard Library](../reference/standard-library.md),
runtime/backend behavior in [Architecture](../internals/architecture.md), and the history of the
compiler's optimization/codegen work in the [Compiler Changelog](../internals/changelog.md) — not here.

| Feature | Status | Description |
|---------|--------|-------------|
| [Affine/Linear Ownership](OWNERSHIP.md) | Planned | Turn best-effort ownership analysis into a type-checked affine/linear discipline with borrow inference |
| [Traits / Typeclasses](TRAITS.md) | Planned | Type-directed dispatch on the capability dictionary machinery, retiring the polymorphic-operator inlining hacks |
| [Package Registry Website](REGISTRY_WEBSITE.md) | Planned | Server-rendered browse/search UI over the existing registry API, surfacing per-package capability requirements |
| [Self-Hosting](SELF_HOSTING.md) | Exploratory | Rewrite the compiler in Ashes |
| [WebAssembly Target](WASM_TARGET.md) | Exploratory | A `wasm32` backend for browsers and sandboxed plugin hosts |

---

## Ground Rules

1. **Spec first.** Update the [Language Reference](../reference/language.md) before
   implementing any new syntax or semantic rule.
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
