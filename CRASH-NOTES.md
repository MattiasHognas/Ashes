# OPT-85 recursive reclamation: the open crash

This branch carries the change that cuts stage 1 compiling one semantics module from **7.35 GB to
0.90 GB** and from **10.7 s to 3.2 s**. It is not merged because it segfaults the self-hosted
semantics suite. Everything below is measured.

## Reproducing

```bash
dotnet run -c Release --project src/Ashes.Cli -- compile --project selfhost/tests/semantics/ashes.json --debug
gdb -batch -ex "set disable-randomization on" -ex run -ex "x/i \$pc" -ex "info registers rax" \
    ./selfhost/tests/semantics/out/ashes-selfhost-semantics-tests
```

A pristine build runs the same suite to completion, so the crash is this change. The run is
deterministic with randomization disabled: the faulting frame is always `$rbp = 0x7ffffff12240`.

## What the fault is, exactly

```
=> mov (%rax),%rax     rax = 0xfffffffffffffff0   (= -16)
```

Not a corrupted pointer — a dereference of **NULL − 16**. `RcHeader.SizeBytes` is 16 and the value
pointer is `base + 16`, so this is a reference-count read on a null value. Matching the surrounding
instructions (`cmp $1` / `sete`, then the `0x4000000000000000` immortal-sentinel compare) against
the emitters identifies it as **`EmitRuntimeRcDrop`**, not `RcDup` and not `RcIsUnique`.

At the IR level it is one instruction:

```
rc_handed_over_copied_59523:
    RcDrop SourceTemp=158 TypeName=...CoreProgramUses RuntimeManaged=true
```

in `buildProgram` (`CoreLowering.ash:20250`). Temp 158 is the `CoreProgramUses` accumulator passed
to `collectCoreFunctionUses`. `RcDrop` only emits a null guard when `instruction.MayBeEmpty` is set
(`EmitRuntimeManagedDropInstruction`); this one does not have it, so the header read is unguarded.

## Why it is not a local defect

- Temp 158 is defined by `LoadLocal Target=158 Slot=83` in the merge block
  `call_reclaim_owned_result_59518`, which **dominates** the drop. The IR is well-formed.
- `buildProgram`'s IR is **byte-identical** between this branch and a pristine build, as is the IR of
  all three functions that produce a `CoreProgramUses` (`collectCoreInstructionUses`,
  `collectCoreFunctionUses`, `includeCoreInstruction`), compared with lambda numbering normalised.
- At the fault the spill slot (`$rbp - 0x5b8`) holds 0, and a watchpoint shows its last writer is
  `lambda_14202 + 4` — the function's own prologue.

Identical IR with different behaviour means the zero is **damage done elsewhere**, and this frame is
only where it surfaces. The change enables an arena reset in **176 loops against 7** before it, so
the corrupting write is in one of the 169 newly-resetting loops.

## Bisection

`ExternalAbiType` is necessary and sufficient: denying that one recursive type name makes the suite
pass. It is also load-bearing for the win — denying it returns the probe to 7.35 GB, because it is
embedded in the large state records and excluding it makes everything containing it
non-deep-copyable again. A narrow "skip this type" workaround therefore buys nothing.

## Hypotheses tested and refuted

- **The DeepAdt two-pass overlap is missing.** It is not: `TcoBackEdgeEmitPhaseAUpCopies` already
  clones a `DeepAdt` argument twice specifically to keep Phase B's write disjoint from its read, with
  the argument written out in a comment.
- **`RcIsUnique` faults on the empty list.** A real hole — it had no `MayBeEmpty` flag and no guard,
  unlike `RcDup`/`RcDrop` — and it is fixed on this branch. It is not this crash; the fault is
  unchanged with the guard in place.
- **Nullary constructors mixed with payload ones** (`ExternalAbiType` has six). A minimal recursive
  ADT of that shape threaded as a loop accumulator compiles and runs correctly.
- **The bare recursive self-reference.** `Node(Int, MapTree, K, V, MapTree)` inside
  `type MapTree(K, V)` leaves `K`/`V` unsubstituted so `CopyFieldInsideCopier`'s pretty-name
  comparison misses the self type. Real defect, fixed on this branch, crash unchanged.
- **Keying the recursion path on the instantiation rather than `symbol.Name`.** `Maybe(A)` nested in
  `Maybe(B)` counts as re-entry and is answered true without checking the inner instantiation — a
  genuine hole, but not this crash.
- **Downgrading a non-fresh recursive accumulator to `None`**, mirroring the existing
  `FreshListRebuild` rule for lists. The `ExternalAbiType` loops do take a reset with `fresh=False`,
  but the downgrade leaves the suite crashing.
- **Restricting recursion-admitted types to the advancing watermark** instead of the fixed one.
  Still crashes.

## The next instrument

The symptom is reached long after the corrupting write, so watching the victim slot is too late.
Either bisect the 169 newly-resetting loops directly, or catch the write: find the arena range that
`buildProgram`'s `CoreProgramUses` lives in, and set a watchpoint on it *before* the loops run.

Instrumentation used, to recreate: an `ASHES_RECURSION_ONLY` / `ASHES_RECURSION_DENY` /
`ASHES_RECURSION_LOG` hook in `IsArenaDeepCopyAdtLayout`'s re-entry branch (17 recursive type names
in the semantics package), and a stderr line in `EmitTcoBackEdgeArenaBlock` printing each argument's
type beside `TcoBackEdgeArgCopyOutKind`.
