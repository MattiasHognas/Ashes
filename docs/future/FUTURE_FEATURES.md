# Future Features

Planned features and future work for the Ashes language and ecosystem. **Shipped** features are
documented in the normative docs under [`docs/`](../) — syntax/semantics in
[LANGUAGE_SPEC.md](../LANGUAGE_SPEC.md), library APIs in [STANDARD_LIBRARY.md](../STANDARD_LIBRARY.md),
and runtime/backend behavior in [ARCHITECTURE.md](../ARCHITECTURE.md) — not here. (Text parsing,
async TCP/HTTP, HTTPS/TLS, and brace-free records were landed and their design docs retired into
those.)

| Feature | Status | Description |
|---------|--------|-------------|
| [Package Manager](PACKAGE_MANAGER.md) | Partial | Local deps first, lock file second, registry third |
| [Compiler Optimization](COMPILER_OPTIMIZATION.md) | Largely complete | LLVM passes, memory management, codegen. The audit roadmap and the whole 1BRC-driven optimization arc (Perceus-style in-place reuse + move/linearity elision, deterministic resource safety, structured parallelism on all three targets, byte-level parsing, SIMD `memchr` scan, zero-copy `mmap` input, data-parallel chunked fold, loop-invariant reset-safety) have **landed** — the full 1e9-row 1BRC now runs (~2m36s / 15.9 GB). See *Completed Work* there; a few smaller open ideas are below |
| [Effects](EFFECTS.md) | Planned | Algebraic effect handlers — typed effect rows (`uses { ... }`), lexical handlers, optional `perform`, inferred operation/handler types, one-shot/tail-resumptive continuations. Basis for capabilities, DI/testability, typed errors, and async. Multi-shot deferred (no-GC) |
| [Inline Modules](INLINE_MODULES.md) | Planned | Nested, named `module Name =` declarations inside a file — pure compile-time namespacing, no runtime representation; transparent inline ↔ file promotion |
| [Ashes.Math](ASHES_MATH.md) | Planned | Two layers: a hermetic core (Int helpers, `sqrt`, Float arithmetic, constants — no library) plus native transcendentals (`sin`/`cos`/`exp`/`log`/…) from a **vendored `libm` (openlibm), statically linked and dead-stripped** per binary — compile-time only, no runtime dependency |
| [Self-Hosting](SELF_HOSTING.md) | Exploratory | Rewrite the compiler in Ashes |

---

## Open compiler-optimization ideas

The 1BRC optimization backlog is complete (see
[COMPILER_OPTIMIZATION.md](COMPILER_OPTIMIZATION.md)). These smaller, still-open ideas remain — all are
low-priority (the 1BRC ultimate goal already runs), and the first two live in the compiler's most
use-after-free-prone code (the in-place-reuse analysis), so any attempt needs heavy scale-testing.

- **Generalize in-place-reuse eligibility beyond the `Map.set` shape** (e.g. `Ashes.HashMap.set`).
  Investigation (was `CO10_FINDINGS.md`): the *eligibility* detection is easy — teaching
  `TryGetNestedRecReturn` to peel a leading `let` and accept the eta shape makes `HashMap.set`
  specializable and per-node tree reuse fires. But memory stays linear because `IsFullyReusing` rejects
  `HashMap.set`'s spec with **identical alloc counts** to `Map.set`'s. Root cause, traced to closure
  compilation: in `Map.set`'s spec the recursive `go` **unifies with the spec label itself** (self-calls
  are `Binding.Self` → a fresh `MakeClosure(spec)` immediately `CallClosure`'d, accepted), whereas
  `HashMap.set`'s `go` becomes a **separate nested function** materialized once and `StoreLocal`'d
  (`MakeClosure → StoreLocal`), rejected as an escape. The divergence reproduces with the leading `let`
  removed and in both the eta (`… in go`) and non-eta (`… in go(map)`) forms, so it is *why* the two
  `go`s' labels differ that is unexplained. A fix must either make `HashMap.set`'s `go` unify with the
  spec label or teach `IsFullyReusing` to accept a stored-but-non-escaping recursive closure. Key code:
  `Lowering.cs` — `TryGetNestedRecReturn`, `IsFullyReusing`, `AccumulatorIsFullyPersistent`,
  `GetOrCreateReuseSpecialization`.
- **Recognize a `let x = match … in loop(x)` accumulator for the loop arena reset.** The reset-safety
  check (`IsStableAccumulatorExpr`) only proves stability when the recursive call sits directly in each
  `match` arm (`| Some -> loop(Map.set(…)(m))`); a `let m2 = match … in loop(m2)` binds the accumulator
  to a match result whose stability isn't traced through the `let`. Tracing it would let the natural
  written form stay constant-memory without hand-restructuring (see `challenges/1brc/brc_parallel.ash`).
- **Zero-copy borrowed slices from `Bytes.subText` / `String.substring`.** The view representation
  exists (`EmitStringView`); the `mmap`-input half landed. Making `subText` return a view is *not* a
  clear win — a slice stored in a structure (a 1BRC station name in the `Map`) is materialized on insert
  anyway, so a view is created then copied. Only transient slices benefit, so a useful version needs
  escape/lifetime analysis (view when the slice doesn't escape, copy when it's stored).
- **An ergonomic `Ashes.Parallel.reduceChunks` combinator** (record-boundary chunk splitting built in).
  Blocked by the no-inter-file-import stdlib rule — `Ashes.Parallel` can't call `Ashes.Bytes` — so
  chunk-splitting stays a user-level pattern (as in `challenges/1brc/brc_parallel.ash`) until that rule
  is lifted (or the helper is made a compiler intrinsic).

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
