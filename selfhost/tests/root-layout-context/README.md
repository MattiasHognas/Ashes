# Root layout input equivalence

Compile with leak checking and run `check`, `1000`, and `10000`. The full
classifier uses ordinary root entries without precomputed inputs; narrow views
use one context for their resolved named root. Compare every view field and the
complete ordered child descriptors, including generic nonregular recursion,
resource and unresolved leaves, same-name/different-ID types, swapped environments,
constructor order, missing types, zero-cost erasure, and opaque types.

The implementation was additionally checked against a frozen classifier from
75c59182 with independent original helper bodies. That ignored validation harness
under work/root-layout-context namespaces only its types and exported values to
coexist with the candidate; ReferenceHeapLayoutClassification.ash.inc retains the
byte-identical original. Its Oracle.ash explicitly compares all fields and child
attributes across the two nominal type families. This standalone durable fixture
keeps the ordinary-versus-context input equivalence check without vendoring an
entire old classifier into the test tree.

The `regular-1000` / `regular-10000` and `nonregular-1000` /
`nonregular-10000` modes create fresh generic roots and environments on each
iteration. Run them in pairs; a successful single invocation is not a plateau.

Validation used the independent frozen oracle in plain and poisoned stage 0.
The larger all-field benchmark with fresh environments was flat at 3 live cells
for both old and indexed queries at 1,000 and 10,000 calls under stage 0, plain
and poisoned, after the generic-loop ownership correction. Under unchanged
bootstrap22 stage 1, old counts were 53,003 / 530,003 and indexed counts were
19,003 / 190,003 for both regular and nonregular fixtures. These are existing
stage-1 slopes reduced by the candidate, not a claim of stage-1 flatness.

A matched debug benchmark of 10,000 shared-branch queries consumed every view
flag and child descriptor: old 137.09 seconds, indexed 75.67 seconds, identical
checksum 1,310,000 and final live count 3. These debug timings are not comparable
to earlier optimized measurements.

The durable fixture itself passes `check` in plain and poisoned stage 0. Its
fresh simple queries are flat at 5 / 5 live cells, and fresh regular/nonregular
generic queries are flat at 4 / 4 at 1,000 / 10,000 calls. All `.ash` sources were
canonically formatted before the final runs.
