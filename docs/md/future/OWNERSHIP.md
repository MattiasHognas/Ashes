# Affine/Linear Ownership: Compile-Time-Verified Memory & Resource Safety

## Goal

Turn ownership from a best-effort optimizer analysis into a **type-checked discipline**. Uniqueness
and lifetimes become part of the type system, and the compiler *rejects* unsafe programs instead of
silently miscompiling or leaking. This is what makes the project's "no GC, compile-time-verified"
promise (`FUTURE_FEATURES.md` ground rule #6) literally true rather than aspirational.

## Why

- **The differentiator.** Roc and Koka both fall back to Perceus reference counting. A pure
  functional language that gets in-place reuse and resource safety from *static* ownership — no RC at
  all — is a position nobody else holds. Ashes is already most of the way there.
- **The unblocker.** A sound ownership story is the prerequisite for the request-side body reader,
  response streaming via function-typed ADT fields, list-cell reuse, and closing the remaining
  resource-safety holes.

## Current state

`src/Ashes.Semantics/Lowering.Ownership.cs` (~1500 lines) infers uniqueness heuristically and falls
back to copying when it can't prove reuse is safe. Known, reproduced soundness gaps:

- Nested-in-aggregate resource leak (a resource inside a record/ADT is not dropped).
- Escape-via-capture use-after-close (a closure captures a resource that outlives its scope).
- `Process` is never dropped.
- List-cell reuse is not implemented.
- The stalled reuse milestone hit "rebuild corruption" (see the ownership-milestone notes).

## What we should do

1. **Spec first.** Add an ownership/uniqueness chapter to `docs/md/reference/language.md`: what
   affine vs. linear means here, how borrows are written or inferred, and the exact programs that are
   now rejected.
2. **Uniqueness/affine typing.** Extend inference so values carry a uniqueness attribute; a unique
   value may be consumed at most once. Reuse becomes *guaranteed* when the type says the value is
   unique and dead, not guessed.
3. **Borrow inference.** Infer non-consuming borrows so read-only access doesn't force a copy or a
   move, keeping today's ergonomics (no explicit lifetime syntax if we can avoid it).
4. **Close the resource gaps.** Make drop obligations flow through aggregates and captures; reject
   escape-via-capture; ensure `Process`/sockets/files are always dropped exactly once.
5. **Diagnostics.** New error codes for "value used after move", "resource escapes its scope",
   "unique value captured by escaping closure" (take the lowest free `ASH0xx`).
6. **Migrate the optimizer.** Once the type system proves reuse, `Lowering.Ownership.cs` becomes a
   *lowering* of proven facts rather than a speculative analysis; delete the heuristic fallbacks it no
   longer needs.

## Watch out for

- Keep purity intact — reuse is an implementation detail, never user-visible mutation (ground rule
  #4/#5). `Drop` stays a compiler concept.
- Preserve current ergonomics; if uniqueness leaks into every signature the language gets heavier.
  Prefer inference over annotation.
- Land it behind tests that prove each previously-unsound program is now *rejected at compile time*,
  and each safe program still compiles to the in-place-reuse code path.
