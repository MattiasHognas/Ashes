# OPT-85 recursive reclamation: resolved, and the result is negative

**The 87% was not real.** It was memory "saved" by a miscompilation that reset the arena over data
still in use. With the miscompilation fixed, admitting self-recursive ADTs to the reclamation
classifiers is **worth nothing measurable**. This branch is a record of that, not a candidate to
merge as an optimization.

## The three states, measured

| | probe peak RSS | self-hosted semantics suite |
|---|---|---|
| baseline (recursion not admitted) | 7.35 GB | passes |
| recursion admitted, emitter disagreeing | 0.90 GB | **segfaults** |
| recursion admitted, emitter agreeing | 7.04 GB | passes |

Per module, baseline → correct fix: Symbols 352 → 360 MB, Scope 1,130 → 1,145, ResultReach
1,660 → 1,659, TypeResolution 7,010 → 7,039. Within noise, and slightly worse in places.

## The defect that produced the fake win

`GetTcoListCopyOutKind` classifies a list argument by its element type; `EmitListDeepCopy` emits the
copy. They must answer the same question. Switching the *classifier* to the recursion-tolerant
predicate without switching the *emitter* broke that:

- classifier: `IsRecursiveDeepCopyOutSafeType(element)` → `DeepAdt`, so the loop qualifies for the
  back-edge reset and the argument counts as copyable;
- emitter: `IsDeepCopyOutSafeType(element)` → false, so it falls through and **returns the original
  pointer unchanged** ("unsupported element types stay shallow");
- the back edge then restores and reclaims the arena believing it holds a self-contained clone.

Isolated to one loop by binary search over `ASHES_TCO_ALLOW_FIRST`: site 415,
`emitPerformEvidenceSave` in `CoreCapabilityLowering.ash:136`. Its six arguments classify as

```
arg 0 Int                     pass=False canReset=True
arg 1 Int                     pass=True  canReset=True
arg 2 Int                     pass=False canReset=True
arg 3 Int                     pass=True  canReset=True
arg 4 List<Int>               cons=True  kind=Shallow    -> 16-byte single-cell copy, emitted
arg 5 List<IrInstructionKind> fresh=True kind=DeepAdt    -> promised, NEVER EMITTED
```

and the emitted back edge contains only arg 4's copy:

```
CopyOutArena   DestTemp=86 SrcTemp=57 StaticSizeBytes=16 Purpose=ArenaTcoCompaction
RestoreArenaState CursorLocalSlot=12 EndLocalSlot=13 PreRestoreEndSlot=36
CopyOutArena   DestTemp=88 SrcTemp=86 StaticSizeBytes=16 Purpose=ArenaTcoCompaction
StoreLocal     Slot=4 Source=88     <- arg 4, compacted
StoreLocal     Slot=1 Source=75     <- arg 5, stored RAW
ReclaimArenaChunks SavedEndSlot=13 PreRestoreEndSlot=36
```

`arg 5`'s successor is `append(instructions)([nextSave])`, a spine rebuilt entirely above the
watermark, so the reclaim frees it while the loop parameter still points at it. The observed fault
was far downstream: `EmitRuntimeRcDrop`'s count load on a null value (`rax = -16`) in `buildProgram`,
whose own IR is byte-identical to a pristine build.

## The standing design constraint

`GetTcoListCopyOutKind` and `EmitListDeepCopy` must ask the element question with the same predicate.
A classifier that promises `DeepAdt` against an emitter that silently declines does not fail loudly —
it produces a reset over live data. The emitter's fall-through is deliberate for genuinely
unsupported shapes, which is exactly why the classifier must never promise more than it can do.

## What is worth keeping

The `RcIsUnique` empty-value guard, which is independent of all of the above: `RcDup` and `RcDrop`
both carry a `MayBeEmpty` flag and guard the header access, `RcIsUnique` carried neither and read the
count at `value - 16`. An empty value owns no cell to reuse, so "not unique" is the honest answer,
and since a missed site faults rather than mis-answers the guard is unconditional.

## Refuted along the way

The two-pass overlap (already implemented, and a third Phase A clone does not help); nullary
constructors; the bare recursive self-reference (a real defect, fixed here, unrelated to the crash);
instantiation-keyed recursion paths; the non-fresh accumulator downgrade; restricting recursion types
to the advancing watermark; and every "admit fewer types" workaround, since the win and the fault came
from the same instructions.

## Reproducing

```bash
dotnet run -c Release --project src/Ashes.Cli -- compile --project selfhost/tests/semantics/ashes.json --debug
./selfhost/tests/semantics/out/ashes-selfhost-semantics-tests
```

Scaffolding on this branch, all env-gated and inert by default: `ASHES_TCO_DENY_RECURSIVE` refuses
every recursion-admitted back-edge copy-out; `ASHES_TCO_ALLOW_FIRST=<n>` keeps only the first n, for
binary search; `ASHES_TCO_LOG_SITES` and `ASHES_TCO_DUMP_SITE=<n>` print the sites and one site's
per-argument classification; `ASHES_LOG_COPIERS` lists every synthesized copier.
