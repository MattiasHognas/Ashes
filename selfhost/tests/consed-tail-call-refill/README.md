# Consed-tail / helper-refill composition

This focused self-hosted shape-analysis fixture checks the narrow `GRC_NO_CONSEDTAILCALLREFILL` stage0 rule's stage1 mirror.

A loop mixing a consumed-tail edge, a proven cons onto that same consumed tail, and a helper-call refill must classify the list as a choice accumulator. A let-bound helper result is equivalent. At least one proven consed-tail edge remains required: a pop/refill-only loop stays walked-or-refilled. A head that reads the loop parameter, a cons onto the wrong tail, or an unclassified successor keeps the mixed shape conservative.

This is classification only; the actual ownership regression and escaping-head/sibling guards are in `../owned-call-result-worklist`. Stage0 runtime validation must compare the new rule on/off, including poisoned execution; stage1 runtime validation requires a compiler built with this mirror.
