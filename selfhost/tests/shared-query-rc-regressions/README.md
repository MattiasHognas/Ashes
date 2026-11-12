# Shared-query ownership regressions

The permanent end-to-end cases are `tests/normalized_constructor_heads_preserve_shared_paths.ash`
and `tests/visited_paths_survive_pending_work.ash`. Their matching leak templates
are registered last and have paired plain/poison plateau tests.

The first case checks the field references held by an arena constructor normalized
as a TMC list head. Stage1 requires the mirrored correction. The second isolates
the visited-map owner from TMC by directly constructing its pending list cells.
With stage0's additional generic-loop exit proof disabled it crashes; enabled it
is flat at1/1000 queries, both plain and poisoned. Unchanged bootstrap24 stage1
is flat at1/2/1000 queries for this isolated scalar-result loop. A second case
returns `(count, visited)` and reads the returned path list after the loop: both
stage0 plain/poison and unchanged stage1 are flat at1/1000 queries. Preserved
`ScalarVisitedMap.ash.inc` and `ReturnedVisitedMap.ash.inc` accept those round
counts as their single command-line argument (copy to `.ash` before compiling).
These use whole-module imports because standalone stage1 compilation does not
resolve the selector-import spelling used by the original stage0 fixture.
This exemption
does not claim all shared-query paths or the full compiler are leak-free.

`UnannotatedVisitedMap.ash.inc` preserves the known unannotated reducer limitation
from the investigation. Its inferred loop placement failed before the explicit
`List(Work)` and `MapTree(Int,List(Path))` annotations used by the isolated
regression. It is retained as diagnostic input, not silently converted into a
passing acceptance case. The original failing executables/IR are preserved in
`work/shared-graph-queries/resumed/visited-map*` in the investigation worktree.

Detailed command logs, wall/CPU/RSS measurements and counters are in
`work/shared-graph-queries/acceptance`. These are focused checks, not a clean
committed census or full-suite acceptance. Full suites remain deferred.

Final acceptance on the trace-free candidate: four end-to-end cases passed plain
and poison (the two new cases plus nested generic map versions and exit guards);
both new paired plateau/poison tests passed. The adversarial graph oracle reports
`equivalent`; repeated poisoned queries remain live1 at1000/10000. Its one-shot
whole oracle ends live7, which is not used as a plateau claim. The unannotated
reducer still exits139 with the candidate and is explicitly unresolved.

The TMC stage1 mirror is formatted and uses the existing construction-retain
proof, but has not yet been executed through a newly rebuilt stage1. That runtime
validation belongs to the next integration rebuild. No stage1 flat exemption is
claimed for TMC constructor heads.
