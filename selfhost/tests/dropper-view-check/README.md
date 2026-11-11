# Dedicated dropper view regression fixture

Compile with leak checking, then run `cached 1000`, `cached 10000`, `miss 1000`,
`miss 10000`, `synthesis 1000`, and `synthesis 10000`. Each round creates fresh
names and constructor environments. `cached` populates the dedicated view cache;
`synthesis` builds nested droppers through shared begin/finish bodies. Compare live
counts between round counts; constant process residuals are not per-round leaks.
Stage 0 supports compile-time poisoning; stage 1 poison coverage is not implied by
setting the environment variable.

The `copiers` mode prints complete instruction text, labels, and counters for both
arena and to-space copier synthesis over records, recursive ADTs, lists, and tuples.
Compare its stdout between baseline and candidate compilers/source snapshots.

`DropperViewTests` compares dedicated facts with full-classifier projections;
`DropperViewCacheTests` checks authoritative full-cache entries and exact generic
keys. Both are registered in the semantics suite. Existing dropper instruction
expectations must remain unchanged.

Validated with bootstrap15 stage 1: fresh nested synthesis reports 3003/30003 live
cells at 1000/10000 rounds, versus 3004/30004 with the baseline full classifier.
The existing three cells per round remain; this fixture is not flat on stage 1.
Dedicated-cache and miss modes remain flat at 2/2. Stage 0 plain and poisoned
nested synthesis is flat (candidate 3/3, baseline 4/4).
