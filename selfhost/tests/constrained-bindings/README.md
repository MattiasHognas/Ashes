# Constrained Core and final-stage1 boundary checks

`cases.tsv` maps all sixteen positive driver5 cases plus a source-copy shadow-identity regression to reusable sources, preferring existing end-to-end regressions. The final row is a required negative: a captured monomorphic function must reject Int/Str arguments. New cases print the exact marker in the manifest only if their Boolean result is true.

The actual-Core driver5 accepted all sixteen original positive sources and rejected the captured monomorphic case. This establishes lowering/IR acceptance only. The programs here add a runtime assertion around the same final expression so final-stage1 validation can check execution as well. Native execution and lifetime acceptance are pending; do not infer them from Core success. Some alias/partial cases expose separate existing stage0 defects and are intentionally kept in this focused suite rather than declared passing in the standard stage0 suite.

Build each positive with the new stage1 compiler and run its generated executable under the required memory scope. Require exact stdout, exit0 and a live-cell counter of1 for the scalar results. The negative must fail compilation with the original Int/Str mismatch. Follow the companion `mixed-constrained-bootstrap` strict plateau runner for repeated snapshot lifetime checks; one successful scalar run does not prove a plateau.
