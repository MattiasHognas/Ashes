# Heap classification plateau

Compile with `ASHES_RC_LEAKCHECK=1` and run the same binary with `1000` and `10000`.
Both must print `done` and have equal final live-cell counts. Each iteration creates
an indexed environment and classifies a shared graph with either a resource, an
unresolved variable, or scalar leaves. It checks both containment facts and compares
the minimal admissibility view with the full classifier, including the ordered
child types and drop kinds.

Run with stage 0 plain and poisoned, then stage 1 plain. Stage 1 currently has no
poison-code-generation implementation. Keep compiler exit leak counts separate
from the generated program's measured plateau.
