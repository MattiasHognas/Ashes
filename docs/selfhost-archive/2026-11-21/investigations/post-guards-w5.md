# 32_16..100 (leaked small Int lists), W1 round 11, stopped 21:0x 11-03 (usage limit)

- Census (s1-nte, loops10 -> 11): leaked roots grow by about 284 per function. Most are singleton `[n]` and
  two-element Int lists, about 2 per temp number per function (work/w1/iv10, iv11, ar10, ar11).
- Count-word watch (work/w1/watchskip.sh <payload> 700 500 on the base debug stage 1) of a leaked `[54]`:
  - allocated in IrControlFlowGraph blockSuccessorsOf (:158), 1
  - retained into IrCfgBlock by assembleAllBlocks (:198), 2
  - the successors tuples released in buildCfgBlocks (:208), 1
  - retained again by blockSuccessorsOfIndex for computeImmediateDominators/reversePostorder (:224 via :244, :339), 2
  - the block's own reference released with the blocks list (dropper inlined at :197), 1, never released after that.
  The extra reference is the one reversePostorder's `pending: List(List(Int))` takes. Lead: the inner cell of
  `(successor :: more) :: pendingRest` (or the leftover pending at `| _ -> order`) is never released.
- Reproducers: r1.tpl (reversePostorder over Block records + listAt) leaks +10/round in both stages, and no
  GRC_NO_* switch reduces it (switch sweep in swres.txt). r2/r3 (the same pattern over fresh lists, no records)
  are flat, so the record field read through listAt (blockSuccessorsOfIndex) is part of it.
- Ruled out: upsertStr eager default (u1), CFG-building shapes (a2, a3, a4). a4 leaks +6/round in stage 1 only.
- Next: reduce r1 (drop the visited check, the second total, the Block fields), read the stage-0 IR of postorder for
  the release of the destructured inner cell and of a successorsOfIndex result pushed onto pending, then fix both
  stages.
