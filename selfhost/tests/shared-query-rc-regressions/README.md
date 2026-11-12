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

Bootstrap26 (`5804df059c0d72d54eef3f8becc31e2f8144052a`, compiler SHA256
`67a291527df338dbf83a99a18c96fe5d66354e8a26f91a5a523f02aa6ceb5fd0`) subsequently
validated the TMC mirror: the stage1-compiled minimal fixture reports live1 at10
and1000 rounds, with correct30/3000 output. Actual regular fresh graph queries are
also live1 at1000/10000. The nonregular query fixture exposed a separate callback
result ownership regression (+6/query), investigated by the annotated callback
regression under `tests/annotated_callbacks_release_recursive_results.ash`.

The callback regression checks ordinary, recursive, mutually recursive, nested,
and nested-recursive bindings. Each callback returns `Maybe(Value)` with a
recursive payload, and both `Some` and `None` arms are exercised. Bootstrap26
returns the correct results but reports live25001/250001 at1000/10000 rounds;
stage0 plain and poisoned report live1/1. The same bootstrap26 compiler becomes
flat when the equivalent type is written directly on the callback parameter,
which isolates the missing whole-binding annotation propagation.

The compiler change preserves only concrete signatures. Generic signatures keep
the previous lowering path because mutually recursive members do not yet retain
independent annotation-variable scopes. This does not claim to solve arbitrary
unannotated callback ownership or the previously recorded unannotated map reducer.
The backend suite checks this shared regression at two round counts with its
live-cell reporter; the normal end-to-end test also checks its output.

Validated candidate compiler SHA256
`6d49aebf382dab3cdc18f74a8480c138badc490fe2ca67d8d9a3b133599af925` compiles the
six-form callback regression to live1/1 at1000/10000, with correct6000/60000 output.
The actual regular and nonregular `TypeGraphQueries` fixtures are both live1/1 at
those counts: nonregular growth falls from +6 cells per query to zero. The mutual
generic probe retains its baseline `7after` result. Fixture compilation takes
0.39 seconds (199884 KiB peak RSS); the actual graph fixture compiles in82.96
seconds (701268 KiB), versus84.53 seconds (701932 KiB) on bootstrap26. At10000
queries, regular/nonregular runtime is0.04/0.05 seconds, both12832 KiB peak RSS.

These are focused runtime checks, not a clean compiler census or full-suite
acceptance. The compiler's own one-shot graph-fixture build ends with1436644 live
cells versus1432691 before; those totals include the existing compiler leaks and
changed generated IR, and do not establish a per-function leak slope. Stage0
poison checks passed; the stage1 backend does not currently implement
`ASHES_RC_POISON`, so no poisoned stage1-output claim is made. The newly registered
backend-suite check awaits the agreed full validation gate.
