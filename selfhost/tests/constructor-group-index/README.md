# Constructor group indexing regression

This standalone driver compares production `heapConstructorFieldGroups` with the
previous list-based grouping algorithm kept locally as a reference. The reference
is test code only; production does not select it. Explicit field comparisons avoid
requiring an Eq instance for `ConstructorFieldGroup`.

Arguments:

- `check`: checks empty input and all 1,296 four-element selections from six fixture
  definitions, including repeated constructors, the same ID with distinct names,
  the same name with distinct IDs, mixed generic/non-generic fields, nullary shapes,
  and non-named constructor results. Compares group order and every field/argument.
- `old` / `new`: builds 2,000 unique type groups 20 times; both print `40000`.
- `old1000` / `old10000` / `new1000` / `new10000`: rebuilds fresh dynamic-name
  definitions and groups on every iteration; prints `12000` / `120000`.

Compile with `ASHES_RC_LEAKCHECK=1`, using the repository's scoped resource limits
and separate timing files. Run s0 plain, s0 compile-time poisoned, and s1 plain.
Stage 1 does not implement poison instrumentation. All modes passed the comparison
and returned one live cell in both plateau counts for both implementations.

Measured grouping benchmark: s0 old 0.68 seconds versus new 0.01 seconds; s1 old
0.74 seconds versus new 0.02 seconds. Preserve
wall time, CPU time, and peak memory when rerunning; this fixture measures group
construction, not whole-compiler throughput. The production environment still
receives the same ordered list, and later lookups retain their existing behavior.
