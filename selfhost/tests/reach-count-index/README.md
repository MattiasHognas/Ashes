# Result-reach count merge index

Render with `bash selfhost/tests/reach-count-index/render.sh`. The project is
written to `work/reach-count-index`. The frozen oracle is ResultReachSummaries
from beaa9a30. Its local types and exported names are renamed only to permit a
side-by-side import; unchanged original bytes are also retained. CountQueries
extracts exact old/new private merge helpers, with old helper names prefixed.

The index keeps the first count for each exact string name. Ordered result lists
are still constructed by walking the original lists. A negative first count is
not equivalent to an absent entry in every operation; the old default-minus-one
checks remain identical. Duplicate detection also has a subtle rule: for each
name, the old suffix scan tests the next occurrence's count. Equivalently, while
scanning forwards, a repeated occurrence triggers only if its count is
nonnegative. The entry-by-entry sum/max fallback is unchanged.

`check` compares 157 by 157 exhaustive list pairs over two names and counts
-2/-1/0/1/2/3, plus triple duplicates, Unicode and ancestor-token names. It compares
exact ordered count lists, duplicate decisions and complete reachSum/reachJoin
state fields against the frozen whole module.

Old/new 100/1000 modes repeatedly build fresh names and consume both sum and max
results. Small-table and 512-entry disjoint/overlapping benchmarks compare the
index construction cost as well as merging. Stage0 plain, compile-time poison,
and stage1 plain are required. Run through the session's memory scopes and heavy
job coordinator; no full suites are included.

Stage0 plain, compile-time-poison and unchanged bootstrap23 stage1 checks pass.
Full compiler suites remain deferred to integration acceptance.

The first unconditional map version passed differential and plain/poison lifetime
checks, but regressed small tables. A 5,000-merge disjoint crossover experiment
measured old/new seconds as 0.34/0.46 at64 entries, 1.32/1.40 at128,
and 6.38/5.86 at256. The candidate therefore preserves the original list merge
unless both inputs contain at least256 entries. Duplicate detection independently
keeps its original scan below256 entries. Paired stage1 measurements also support this choice. It is an implementation
choice, not a semantic limit.

The padded special cases exercise indexed merges with negative counts and
repeated names. `old-index10`/`old-index100` and corresponding `new-` modes create
fresh256-entry inputs on every query so the map lifetime is checked independently
of the16-entry small-path plateau. Unconditional results are preserved under
`work/reach-count-index/unconditional`; hybrid stage0 and stage1 validation both pass.

For the scoped matrix, set `ASHES_COUNT_TEST_CLI` and `ASHES_COUNT_TEST_S1` to
validated absolute compiler paths, render, then run
`bash selfhost/tests/reach-count-index/validate.sh plain poison s1`.
The runner records wall/CPU/RSS/exit status separately from leak-counter stderr.
The stage1 mode is plain: stage1 does not implement the poison environment switch.

Hybrid stage0 results (seconds, old/new): size1 .01/.01; size2 .02/.02;
512-by-2 .10/.10; 2-by-512 .04/.01; 512 disjoint .57/.46;
512 overlapping .13/.07. Fresh small and indexed queries both remain live1 at
the two round counts in plain and poison runs. These are focused diagnostic
measurements, not a clean committed compiler census or full-suite acceptance.

Stage1 old/new seconds: size1 .01/.01; size2 .02/.02; 512-by-2 .10/.10;
2-by-512 .11/.01; 512 disjoint .93/.48; 512 overlapping .33/.09.
Both fresh plateaus remain live1 at both round counts. The stage1 fixture compile
completed in3:26.10 wall/205.01 CPU seconds, peak2,065,276KiB, exit0.
The single differential invocation ends at live2 in all three modes; repeated
fresh query modes, rather than that one-shot counter, establish the flat lifetime.
