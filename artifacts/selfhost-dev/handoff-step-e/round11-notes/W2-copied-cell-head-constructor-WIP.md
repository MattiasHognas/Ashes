# WIP: copied-cell head constructor (GRC_NO_COPIEDCELLHEADCONSTRUCTOR)

Pre-existing class, both stages: a tail-recursive rewrite loop matching `Some(label)` out of a lookup's owned
result and consing `Known(label)` (or an instruction record holding `CallKnown(dest, label)`) onto its
accumulator leaks one label per rewrite. The head constructor takes a transfer retain of `label`; the back edge
copies the arena cell (and its head) onto the reference-counted heap with references of its own, so that retain
is never released. Reproducers: tests/loop_successor_constructor_head_borrows_lookup_part.ash,
tests/loop_successor_record_head_borrows_lookup_part.ash (stage 0 121/241 -> 1 live, plain and compile-time
poison). In the stage-1 probe this is the remaining +2/fn 40_9 growth with (C') on (IrOptimizer
devirtualizeReturnedClosureCallsOnce, named by a count watchpoint).

Status: stage 0 + stage 1 code in this commit. The real-code probe (dl tool) SEGFAULTS in an `__rcnorm` helper
with the rule on, both with (C') on and off; reproducers are clean. Narrowed: the late-copied half (retains
noted and released after the back-edge copy) causes it; the already-copied borrow half alone is safe but does
not fix the leak. First fix tried: commit a constructor's retains only when its own placement keeps it in the
arena (KeepArenaHeadConstructorRetains); probe still crashes. Next: watch the freed cell in the debug probe to
name which recorded retain is owned elsewhere (likely a retain moved into an RC parent or released by another
rule such as RecordTransferDuplicatedChild / KeepLoopResultCellOwnerDuplicateOwned). Stage-1 mirror not yet
built or measured.

Separate pre-existing finding: work/cp/rc.tpl (returned-closure-label fixpoint with generic Eq helpers) compiles
with GRC_NO_CONSTRAINEDSPECIALIZATION=1 to a binary that dumps core (stage 0, independent of these rules).
