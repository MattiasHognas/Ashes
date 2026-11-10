# Heap classification plateau

Compile with `ASHES_RC_LEAKCHECK=1` and run the same binary with `1000` and `10000`.
Both must print `done` and have equal final live-cell counts. Each iteration creates
an indexed environment and classifies a shared graph with either a resource, an
unresolved variable, or scalar leaves; it checks both containment facts.

Run with stage 0 plain and poisoned, then stage 1 plain. Stage 1 currently has no
poison-code-generation implementation. Keep compiler exit leak counts separate
from the generated program's measured plateau.
