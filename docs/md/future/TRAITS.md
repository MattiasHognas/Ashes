# Traits / Typeclasses: Principled Type-Directed Dispatch

## Goal

Add real traits (typeclasses) with user-defined instances and dictionary passing, so operations like
`==`, `+`, comparison, and `show` are resolved by the type system instead of special-cased in the
compiler.

## Why

- **Retires live hacks.** Polymorphic `==`, `+`, and `assertEqual` are currently implemented via an
  overload-generic *inlining* trick that monomorphizes per program. That trick is a recurring source
  of bugs (the eager-`Int`-default `==` bug, the `assertEqual`-alias monomorphization gotcha). A
  proper `Eq`/`Ord`/`Num` trait supersedes all of it cleanly.
- **Expressiveness.** User-defined instances make the standard library genuinely generic instead of
  secretly monomorphic, and let user code abstract over `Ord`/`Show`/`Num` without compiler
  privilege.
- **The machinery already exists.** The capability system already does dictionary passing,
  monomorphization (including recursive and higher-order generics), and cross-module coherence. Traits
  are largely re-pointing that infrastructure at *type-directed* rather than *capability-directed*
  dispatch — this is far less green-field than it looks.

## Current state

The type system is Hindley-Milner with let-polymorphism and no typeclass layer. Built-in polymorphic
operators are resolved by overload-generic inlining in the lowering phase.

## What we should do

1. **Spec first.** Add a traits chapter to `docs/md/reference/language.md`: declaration syntax,
   instance syntax, superclass/constraint syntax on signatures, and the coherence rules (how overlap
   and orphan instances are handled).
2. **Frontend.** Parse `trait`/`instance` declarations (or the chosen spelling — keep it in the
   `given`/`recursive`/`external` natural-keyword family) and constrained type signatures.
3. **Semantics.** Extend HM inference with constraint collection and resolution; reuse the capability
   dictionary-passing and monomorphization path to elaborate resolved constraints into dictionaries or
   monomorphic calls.
4. **Bootstrap instances.** Ship `Eq`, `Ord`, `Show`, and `Num` for the built-in types, then rewrite
   the polymorphic-operator built-ins as ordinary trait methods and delete the inlining special-cases.
5. **Diagnostics.** Errors for "no instance for `T`", ambiguous resolution, and overlapping/orphan
   instances (lowest free `ASH0xx`).
6. **Stdlib.** Make `Ashes.Test`'s `assertEqual` and the collection modules use the trait methods
   instead of the built-in overloads.

## Watch out for

- **Coherence.** Decide the orphan/overlap policy up front; retrofitting it is painful.
- Keep dispatch monomorphizing where possible so there's no runtime dictionary cost in the common
  case — the capability path already does this, so lean on it.
- Do this *before* self-hosting: a self-hosted compiler will want traits, and doing it first keeps the
  bootstrap clean.
