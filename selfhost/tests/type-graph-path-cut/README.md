# Path-sensitive type graph regression

This project compares the production path-cut search with the previous recursive
name-cut oracle. Compile with stage 0 plain/poisoned and stage 1 plain, then run:

- `graphs`: all 65,536 directed four-node graphs, four roots each. The four nodes
  represent two named types at two different generic instantiations. Expected stdout
  is `262144 comparisons ok`, exit 0.
- `buckets`: exact nominal-key collision guards (same ID/different name,
  same name/different ID, and same nominal key/different generic arguments).
  Expected stdout is `bucket collisions ok`.
- `old` / `new`: a changing generic ancestor forces fallback, followed by repeated
  sibling edges. Count `visit` output lines: 32,768 old versus 16 new.
- `fork-old` / `fork-new`: a diamond chain with incomparable ancestor-name sets.
  Both visit 4,094 nodes. The new visited-context scan adds overhead here: measured
  stage-1 execution was below 0.01 seconds old versus 0.28 seconds new. This is not
  a generally linear DAG traversal; whole compiler measurements determine its benefit.
- `1000` / `10000`: repeated changing-instantiation queries. Both print `done` and
  have equal live-cell counts when compiled with `ASHES_RC_LEAKCHECK=1` (validated
  new-helper baseline 1/1 in all three compiler modes).

The oracle and visit-count fixtures intentionally allocate test graphs. Their
stage-1 single-run live counters are not a whole-compiler census or a claim that
old test helpers are leak-free. `type-graph-path-cut-plateau` separately measures
new work-state retention using prebuilt immutable children.

Nominal bucket validation (bootstrap16): all modes above pass with stage 0 plain,
stage 0 poisoned, and stage 1. The changing-instantiation plateau stays 1/1 in
all three modes; the separate prebuilt plateau stays 1/1 in stage 0 and 25/25 in
stage 1, identical to the original helper baseline. The stage-1 fork fixture
retains 4,094 visits and measured 0.23 seconds indexed versus 0.28 seconds with
the original visited list. This microbenchmark does not predict whole-compiler
speedup. Oracle fixture compilation took 2.86/2.94/6.45 seconds respectively;
prebuilt plateau compilation took 2.60/2.62/5.91 seconds. Maximum compile RSS was
267,244 KiB. Use a 1 GiB stack limit (`ulimit -s 1048576`) for stage-1-generated
fixtures, as for the self-hosted compiler itself.
