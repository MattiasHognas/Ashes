# k7: generic-callee leak behind IrOptimizer known-label lists (32_ptr)

Stage-1 holder: IrOptimizer optimizeIrProgramWithOptions `firstKnownLabels` (slot 57 is never dropped in the
eef21fd1 lowered dump) and devirtualizeCapturedClosureCalls `knownReturned`. Each (label, next) pair is live
twice at exit.

Reproducer k7.tpl leaks +4 cells/round at stage 0, plain and poisoned.
- k15.tpl, the same program with computeCaptured's `acc` and `known` typed, is flat.
- k14 (pass `all` instead of the tuple field `sites`) is flat, k9 (no computeCaptured call) is flat, and
  v3/v2 (the tuple `(site, site)` or `(label, label)` instead of `(site, label)`) are flat.
- The leak needs a generic callee that builds `(site, label)` with one generic element.
- The leaked cells are 32-byte RC strings: the copies `name :: sites` makes in collectSites, line 45
  (gdb watch on a --debug build, work/w4/lastev.sh). After allocation their count is never changed by anyone.
- Switch sweep (work/w4/sw1.sh): only GRC_NO_RCTUPLESIBLING makes it flat.

Refuted:
- The handed-over child-preserving release (LowerCallDropHandedOverArgumentResultCannotHold). Parking the
  argument in an owned slot, forcing a full release, or ignoring the adoption flag all left the +4 in place.
- The IrOptimizer closure passes. Disabling captured/returned devirtualization, currying-stage inlining or
  scalarization leaves the +4.

Next hypothesis: the strings are never touched after allocation, so they are probably held by an arena cell
that is reclaimed, not by a reference-counted cons. Check collectSites' RC tuple `(sites, bad)` with `bad`
normalized, and how the caller's tuple pattern hands `sites` to the generic callee.

## 20:2x findings
- In computeCaptured's TCO loop, the key passed to the constrained call `lookupAssociation(site)(known)` (Eq dictionary,
  LowerTraitDictionaryRealArguments) takes a pattern-owner marker (RcDup 15, real) parked in an owned slot
  (HandOverDictionaryCallArgumentsToOwnedSlots). The None arm releases the slot at its back edge (general_rc_owned_*);
  the Some arm, whose successor `(site, label) :: acc` keeps the call's arena result, does not.
- lookupAssociation's summary: aliases entries only (key never reached), but GetOwnershipSummaryForCallRoot is null at
  that call (env-captured callee inside the lambda).
- Tried and refuted: skip the marker when the dictionary callee's summary proves no reach (k15 starts leaking +4:
  the marker's retain is balanced elsewhere); release a handed-over generic string right after the call when the
  callee cannot reach it (path not taken here: OwnsGenericStringArgument is not this call's path).
- Next: why the Some arm's back edge keeps the slot. Its successor holds `label` (the call's arena result of
  unresolved layout `Option('a)`), so the slot waits "until the function's result is out of the call's reach",
  which never settles inside a loop. Candidate rule: at a TCO back edge, an owned slot parked for a call whose
  result the successor keeps is released once the successor is copied (or the slot carried to the loop exit).

## Where the Some arm's slot is lost (20:25)
EmitGeneralRcBackEdgeDrops (Lowering.GeneralRc.cs): an iteration slot is released at a back edge only when every
successor owns its parts, the slot is taken by a successor, or NoSuccessorCanHoldPartOf says no successor's TYPE can
hold a part of it. The Some arm's successor `acc : List((Str, 'a))` can hold a Str, so the dictionary marker slot for
`site` is "left to leak, since an arena successor may name them without a reference" (the method's own doc). The
successor actually holds an IndependentClone of `site` (rc_arena_demote) and `label`, never `site` itself.
The name-based analysis that would prove this (SuccessorsNamingNoPartOf) exists only for predecessor slots, which
have Binders. The dictionary hand-over slot (HandOverDictionaryCallArgumentsToOwnedSlots) carries no binders.

Candidate fix (not attempted; it is UAF-sensitive):
1. Give HandOverDictionaryCallArgumentsToOwnedSlots' slots the argument's binder names.
2. In EmitGeneralRcBackEdgeDrops, release an iteration slot with binders when SuccessorsNamingNoPartOf proves no
   successor names them, treating an element copied by EmitArenaCopyOfReferenceCounted as not naming its source.
   That needs the demotion to be visible to the name analysis.
Stage-1 mirror: the same back-edge slot logic in CoreLowering.ash.

## For W3's back-edge release design (32_1)
k7 is the same leave-to-leak in EmitGeneralRcBackEdgeDrops, for an iteration slot rather than a predecessor: the
dictionary hand-over slot of a pattern binder (`site`) whose arm successor's type, List((Str, 'a)), could hold a
Str. A guarded release at the back edge (RcIsUnique, then a child-preserving drop that keeps only the children whose
types a successor could hold, TypeUnreachableChildren) would release this slot as well: `site` is a String with no
children, so a unique `site` is dropped, and a shared one is decremented. k7 leaks +4/round; k15 is the flat control.
