# fn.tpl (case 3, cons onto an arena accumulator) - parked 2026-11-09, not started

Reproducer: work/fn/fn.tpl (copy of stage1-post-guards/work/w1d/fn.tpl, from W1); substitute ROUNDS
(work/fn/fn_1.ash, fn_2.ash). Leaks 17 -> 33 live after rounds 1 and 2 on stage 0 plain, stage 0
poisoned and stage 1 (s1-cr1a), still on ff06d52e.

Shape: worklist loop `nodes (pending) (found)` conses the matched head `call` (an as-binder of the
head of `pending`) onto `found` (`call :: found`), while `pending` is rebuilt as arena cons cells
(`inner :: rest`, `root :: append(arguments)(rest)`). `found` stays an arena slot, so each arena
cell holds an RC head without a reference of its own; the predecessor cannot be released.

k25 (B) (d81fc353, GRC_NO_EXITPARAMOWNFIELDS) does NOT cover it: identical counts with the switch on
and off. (B) handles a single constructor successor built where the parameter was matched nullary
(one cell replacing an empty one); `call :: found` is a cons chain onto the parameter itself, which
(B)'s shape walk declines (ExprCons is kind -1).

Plan (Perceus route, agreed with integration): `found` is the list-accumulator shape that is
normally placed RuntimeRc (TcoListShape / grown cons onto the parameter). First find which rule keeps
it in the arena here: suspects are (1) a sibling block from the arena successor of `pending`
(BlockingSiblingNotProfitable / adtSlotBlockedBySibling), (2) `call` being an as-binder of the
borrowed head of `pending`, so the cons head counts as a pattern part of a sibling, or (3)
ReuseAccumulator via _linearReuseNames. Tools: --emit-ir final on fn_1.ash (lambda_37 is the loop),
TcoPlacementReason in Lowering.Types.cs, a temporary stderr print of the placement decision per slot.
Then extend the RC placement fail-closed (no arena sibling that can receive a part of `found`), mirror
in stage 1, add a leak.* census template that leaks with the new switch set, measure with
work/k25/ab.sh (stage 0 on/off/poison) and the stage-1 binary. Only if the block is load-bearing,
fall back to an exit spine walk (per-cell head release) and ask integration first.
