# Path-sensitive type graph regression

This project compares the production path-cut search with the previous recursive
name-cut oracle. Compile with stage 0 plain/poisoned and stage 1 plain, then run:

- `graphs`: all 65,536 directed four-node graphs, four roots each. The four nodes
  represent two named types at two different generic instantiations. Expected stdout
  is `262144 comparisons ok`, exit 0.
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
