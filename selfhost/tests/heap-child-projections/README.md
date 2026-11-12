# Heap child projections and authoritative cached facts

Generate the test project with:

```bash
bash scripts/selfhost-dev/render-heap-child-projections.sh
```

The script extracts the current `heapFactsOf`, `heapChildrenOfNamed`, and
`heapChildTypesOfNamed` query boundaries from CoreLowering. The prelude injects a
small state, an environment, a fact table, and a resolver override; it does not
reimplement inference. The fixture compares the old full-fact projection with both
narrow queries, including deliberately different cached children, a generic type
that must bypass the monomorphic cache, a resolver override, and list self edges.
The generated source hashes are saved beside the project.

Compile `work/heap-child-projections/ashes.json` with `ASHES_RC_LEAKCHECK=1`, then
run `check`, `1000`, and `10000`. The last two create fresh state and require equal
live-cell counts. Repeat stage 0 plain and with compile-time `ASHES_RC_POISON=1`,
then stage 1 plain. Use the Step F systemd memory scopes, heavy-job coordination,
and the 1 GiB native stack for stage 1.

Also run `heap-shallow-copy-equivalence` for scalar, aggregate, generic, resource,
unknown, recursive and zero-cost wrapper parity, and `heap-classification-plateau`
for fresh graph comparisons. Those use the real TypeEnvironment and classifier;
the extracted fixture additionally proves cached facts remain authoritative.
