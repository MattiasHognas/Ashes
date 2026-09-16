# OPT-85 recursive reclamation: the open crash

This branch carries the change that cuts stage 1 compiling one semantics module from **7.35 GB to
0.90 GB** and from **10.7 s to 3.2 s**. It is not merged because it segfaults the self-hosted
semantics suite. Everything below is measured, so the next person starts from facts rather than
from the beginning.

## Reproducing

```bash
dotnet run -c Release --project src/Ashes.Cli -- compile --project selfhost/tests/semantics/ashes.json --debug
gdb -batch -ex run -ex "bt 4" ./selfhost/tests/semantics/out/ashes-selfhost-semantics-tests
```

A pristine build runs the same suite to completion, so the crash is this change.

## What is known

**The culprit type is `ExternalAbiType`**, found by bisecting which recursive types the reclamation
classifiers admit. Denying that one name alone makes the suite pass. It is a self-recursive sum
(`ExternalAbiPointer(ExternalAbiType)`, `ExternalAbiBuffer`, `ExternalAbiOut`) with six nullary
constructors among its payload ones, declared in `Semantics/ExternalAbi.ash`.

**Denying it also removes the entire win** (back to 7.35 GB), because `ExternalAbiType` is embedded
in the large state records, so excluding it makes everything containing it non-deep-copyable again.
A narrow "just skip this type" workaround therefore buys nothing.

**The symptom is a half-overwritten pointer.** The fault is a corrupted list-spine tail
(`rest=0x44c7480000000070`) inside the shipped `Ashes.Collection.List` walk, reached from
`CoreLowering.ash`'s program-uses collection. A pointer with a plausible low half and a garbage high
half is an overlapping write, not a freed cell.

**Most likely mechanism: the DeepAdt two-pass overlap.** `TryEmitScopeCopyOut` and the back-edge
path emit `RestoreArenaState` *before* the copy, so the copy allocates at the watermark while
reading source bytes above it. The back edge already defends against this with phase-A up-copies
followed by phase-B down-copies, but a synthesized *recursive* copier allocates unboundedly as it
walks, so "copy up, then copy down" does not obviously hold for it.

## Hypotheses already tested and refuted

- **Nullary constructors mixed with payload ones.** A minimal recursive ADT with nullary
  constructors threaded as a loop accumulator compiles and runs correctly.
- **The bare recursive self-reference.** The standard library writes `Node(Int, MapTree, K, V,
  MapTree)` inside `type MapTree(K, V)`, leaving `K`/`V` unsubstituted so `CopyFieldInsideCopier`'s
  pretty-name comparison misses the self type. That is a real defect and is fixed on this branch,
  but the segfault is unchanged by it.
- **Keying the recursion path on the instantiation rather than the bare name.** The guard uses
  `symbol.Name`, so `Maybe(A)` nested in `Maybe(B)` counts as re-entry and is answered true without
  checking the inner instantiation — a genuine hole, but not this crash.
- **Downgrading a non-fresh recursive accumulator to `None`**, the way the code already downgrades a
  non-fresh list (`FreshListRebuild`). The `ExternalAbiType` loops do take a fixed-watermark reset
  with `fresh=False`, but adding the downgrade leaves the suite crashing.

## Useful instrumentation

A temporary `ASHES_RECURSION_ONLY` / `ASHES_RECURSION_DENY` hook in `IsArenaDeepCopyAdtLayout`'s
re-entry branch bisects which type names are admitted; `ASHES_RECURSION_LOG` lists them (17 in the
semantics package). A `Console.Error.WriteLine` in `EmitTcoBackEdgeArenaBlock` printing each
argument's type beside `TcoBackEdgeArgCopyOutKind` shows which loops newly reset: 176 with the
change against 7 without.
