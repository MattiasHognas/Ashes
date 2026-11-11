# Heap-drop graph equivalence

Run `bash scripts/selfhost-dev/render-heap-drop-worklist.sh` to generate a project in `work/heap-drop-worklist`. It combines the current classifier implementation with the original recursive reference and assertions in this folder. Compile the generated project with leak checking and the scoped memory limits documented for Step F.

- `check` compares 6,144 small graph roots plus changing generic instantiations, unknown types, tuple/list fields, functions, resources, nominal identity collisions, and zero-cost wrappers.
- `old` / `new` compare the repeated-child diamond benchmark.
- `old1000` / `old10000` and `new1000` / `new10000` rebuild fresh environments and queries; require identical live-cell counts at both sizes.

Run stage 0 plain and poisoned, then stage 1 plain. Stage 1 has no poison instrumentation. The production walker must retain its direct recursive form: a step/fold rewrite previously concealed a compiler ownership bug and is not an acceptable fix. The separate `owned-call-result-worklist` fixture isolates that compiler regression.
