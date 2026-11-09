# 32_28 + 32_29: the leaked `visited` list (step E round 11, W4, 2026-11-09)

Status: cause named, not fixed. Parked when the round was wound down.

## The class

The stage-1 exit tally at 34cce4c3 grows by +2 per added function in 32_28 + 32_29. Each pair is a two-cell
`List(Int)`, `[28, 29]`, with no referrer: a `32_28` cell (head 28, tail pointing at a `32_29` cell) and a
`32_29` cell (head 29, nil tail). The tally rows and dumps are in
`post-prefix-capture/work/q40/run-w4us4-*` and `run-w4us5dbg-*`.

A debug stage-1 build (`scripts/selfhost-dev/debugstage1.sh`, then `tinycensus.sh` and `tinywatch.sh` on
`tiny/loops10.ash`) shows one write to a leaked cell: its allocation, with count 1, at
`ConstructionRetains.ash:191`. That is the `cellTemp :: visited` cons in `withdrawCellRetains`, reached
from `withdrawCells` (`withdrawLoopParameterRetainsOfCopiedCells`). So the leak is the `visited`
accumulator of the `withdrawCellRetains` / `withdrawStoredRetains` group. The leak is in stage 0's code
for that stage-1 source: stage 0 compiles stage 1.

## Reproducers (stage 0, `ASHES_RC_LEAKCHECK=1`; run with `bash work/l28/run.sh <name>`)

Every template here prints its result plus `ashes-rc-live N`. N = 1 is flat; `run.sh` compiles each one
plain and poisoned.

- `a2.tpl`: the ConstructionRetains shape with the nested branch taken
  (`withdrawCells([28, 29])...([(28, 128 + n % 5), (29, 7)])`). It leaks exactly the visited cells:
  19 / 91 live at 2 / 10 rounds, and an exit dump has 32_28, 32_29 and 32_129/130 cells. **This is the
  reproducer for the class.**
- `a3.tpl`: the same `(nested, nestedVisited) -> withdrawStoredRetains(rest)(slots)(nestedVisited)(nested)`
  arm in an ordinary single-function `let recursive` loop. Here `helper` is a plain function returning the
  tuple. It is flat (1 / 1).
- `s4.tpl`: a group member tail-calls its sibling with a fresh call result. Fixed by GRC_NO_DISPATCHFACTS
  (74ab0111).
- `d.tpl`, `k.tpl`: other dispatch shapes that still leak 1 to 2 cells per round. The `values` /
  `slots` lists are handed into a dispatch slot whose shape is Mixed.
- `neg.tpl`, `neg2.tpl`: negatives. A borrowed argument is handed to the sibling and read again; a flagless
  callee keeps its argument in its result (`keep(values) = Some(values)`). Both are flat plain and poisoned.

## The instruction chain (a2, `--emit-ir final`, `a2.final.ir`)

A `let recursive ... and ...` group whose members tail-call each other is lowered by stage 0 into one
dispatch loop (`MutualRecursionTcoLowerDispatchAndWrappers`, `__recgroup_dispatch_N`; the loop body is the
unnamed ClosureHelper `lambda_19`).

1. The non-tail call `withdrawCellRetains(value)(slots)(visited)(instructions)` (a2 line 27) runs inside
   the dispatch loop and returns an owned reference-counted tuple. The callee's function-result
   normalization (`general_rc_result` arena copy in `lambda_13`) copied `visited` onto the RC heap. That
   copy is the cell the watch sees allocated.
2. The dispatch arm destructures the tuple `(nested, nestedVisited)`. For the tail-call arguments it takes
   `RcDup Target=133 SourceTemp=131` (nestedVisited, tuple field 8) and `RcDup Target=136` (nested).
3. It stores them into dispatch parameter slots 5 (`visited`) and 1 (`instructions`), then releases the
   tuple. That drops the children once and balances against the duplicates.
4. Dispatch slot 5 is arena-placed. No back-edge or exit drop ever releases the reference RcDup 133 took,
   so each `visited` list leaks once per call of the group.

So the reference is taken by the tail-call argument retain of a part of an owned call result, into an
arena slot of the dispatch loop. Skipping that duplicate would leave the slot dangling after the tuple
drop. Copying it into the arena was rejected: that is O(n^2) on an accumulator.

## Why ordinary loops are flat, and why the dispatch loop misses it

- In an ordinary loop, the parameter is placed from the move analysis' structural facts
  (`CreateRecursiveTcoContext`, `GetTcoParameterOrdinalFacts`). A list rebuilt from a call result's part
  is placed on the RC heap, and the existing back-edge predecessor drops and exit drops release it. The
  owned-slot rule `HandOverArgumentKeptByArenaResultToOwnedSlot` (GRC_NO_HANDEDSLOT) also holds a
  reference handed to a callee whose arena result keeps it. A switch scan over all GRC_NO_* switches on
  `a3` (`scan3.sh`, `scan3.log`) shows only GRC_NO_BACKEDGERC and GRC_NO_HANDEDSLOT make `a3` leak.
- The dispatch loop had no facts at all until 74ab0111 (GRC_NO_DISPATCHFACTS registers and summarizes the
  synthesized dispatch). With facts, the dispatch slots are shared between members by type
  (`MutualRecursionSlotLayout`). The facts for a2's dispatch are `0:FreshRebuilt;1:Mixed;2:FreshRebuilt;
  3:Mixed;4:FreshRebuilt` (s4 shown; a2 is the same kind). A Mixed slot gets no RC placement, so the
  retained part stays in an arena slot.
- Stage 1 is flat on all of these. Its lowering has no dispatch transform (`dumpir` of k shows no
  `__recgroup_dispatch`): it lowers groups as closure calls, which the ordinary call rules handle.

Fix direction, not tried: place a dispatch slot reference-counted when any back edge hands it a
reference-counted value its arm owns (a retained part of an owned call result, a fresh RC value), even if
the slot is Mixed. The existing back-edge predecessor drops and exit drops would then balance it. It needs
the result-normalization question checked: the dispatch loop's own result may borrow that slot, as
withdrawStoredRetains returns `(instructions, visited)`. Gate: a2, s4, d, k, neg, neg2 at 2/10 rounds
plain and poisoned; a stage-1 build with full tally showing 32_28 / 32_29 gone; the semantics suite;
regenfixtures.

## Unhanded rule: GRC_NO_DISPATCHBORROWEDCALL (branch leak/dispatch-borrowed-call, 21e1c07f on 1bc2a5d7)

`FlaglessCallInDispatchLoop` adds a CallClosure in a dispatch loop that passes no ownership flag to
`_borrowedArgumentCalls`, so `PerceusLifetimePlacement.AddCallDups` takes no duplicate for it.

- It removes a dangling call duplicate (`storedValuesOf(cell)(instructions)` in k's dispatch).
- Condition (a) evidence: `neg2.tpl`, where the flagless callee keeps its argument inside a result the loop
  carries, is flat plain and poisoned with the rule on and off. So no use-after-free.
- It did not move the class. The us7 build (sha1 8458267e, peak 16.48 GB) still has 32_28 / 32_29 at
  +2/+2 (NET +20, GROWTH +32), because the [28, 29] reference is the tail-argument retain above, not a
  call duplicate. It is not handed back. Integration's condition (b), why the Consumed-versus-flagless
  mismatch does not leak outside the dispatch, is unanswered.

## Tools used

- `work/l28/run.sh <tpl>`: stage-0 live counts at the given ROUNDS, plain (p0) and compiled with
  ASHES_RC_POISON=1 (p1). Everything runs capped by systemd-run.
- Stage-1 check of a template: `bash work/r40/n/s1one.sh <s1-binary> <file.ash>`.
- Exit dump of a stage-0 program: `gdb -x dj.gdb ./prog` (catch the exit syscall, then dump
  0x100000000000..+256 MB). Read it with `post-prefix-capture/work/q40/rccells` (`CELLS=1 rccells 0 0
  dump.bin` lists cells as size_firstword; `rccells <size> <word> dump.bin` buckets referrers).
- Watch a leaked cell: compile with `--debug` and ASHES_RC_LEAKCHECK, find its address in the dump,
  `watch *(long *)0x<addr>` in gdb, and `bt` on each stop. For stage 1, use `scripts/selfhost-dev/tinywatch.sh`
  on a debug build, with addresses from `tinycensus.sh` run with the same command line.
- Switch scan: `work/l28/scan.sh` / `scan3.sh` (every GRC_NO_* in Semantics and Backend, one compile each).
- Final gate for a fix: `work/us/final.sh <tag>`. It runs the stage-1 build at 20G with smoke and p/pa/r5a,
  ir-program-parity, the semantics suite at 20G, `post-prefix-capture/work/q40/fulltally.sh` and
  regenfixtures. Queue it through `post-prefix-capture/work/cp/waitslot.sh`, which allows at most two heavy
  jobs and needs at least 25 GB free.
