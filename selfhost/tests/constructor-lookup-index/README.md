# Raw constructor lookup index regression

Run `bash scripts/selfhost-dev/render-constructor-lookup.sh`. It extracts the current
`CoreConstructorLayout` and monomorphic index declarations from `CoreLowering.ash`
into an ignored project at `work/constructor-lookup-index/ashes.json`. The generator
checks boundary uniqueness and records source hashes; it does not keep a frozen
copy of production index logic.

Compile with leak checking under the repository's scoped resource limits. Validate
s0 plain, s0 compile-time poisoned, and s1 plain (s1 does not implement poisoning).
Keep compiler stderr, emitted program counters and time files separate.

Arguments:

- `check`: 1,296 four-layout sequences compare the first constructor-name lookup and
  ordered result-name and field-name buckets with linear reference scans.
  Field cases include duplicate field names in one layout and shared fields across layouts.
  Extracted production record, first-layout, zero-cost payload, and nullary-count queries
  compare whole-list and result-bucket answers. Nested zero-cost ownership queries cover
  shadowed constructors, variable payloads, and the Function runtime-name distinction. Cases include duplicate
  names with different payloads/results, case-sensitive names, duplicate result names
  with different IDs, unknown names, non-named results, and generic raw schemes.
  Separate substitutions applied after lookup must produce different concrete fields;
  the index itself retains unresolved declarations. Empty/fresh indexes are checked.
- `old` / `new`: 20,000 lookups over 2,000 declarations, printing `60000` in both cases.
- `old1000` / `old10000` / `new1000` / `new10000`: rebuild fresh declarations and
  indexes per iteration, printing `3000` / `30000`. The new query is called after the
  source declaration list's builder returns, checking retained names and payloads.

All three modes pass the expanded equivalence checks. Both old and new fresh-index
repetition tests are flat at one live cell, including temporary field-entry allocation.
Stage 1's one-off `check` mode reports 293 live cells; this is not the repeated fresh-index
plateau metric. Stage 0 plain and poisoned checks report one.
Measured lookup time: s0 1.49 seconds versus below 0.01 seconds; s1 1.17 seconds
versus below 0.01 seconds. These are isolated lookup timings, not compiler timings.
Fixture compile times were 4.34 seconds (s0), 4.33 seconds (s0 poisoned), and 9.45
seconds (s1), with maximum RSS 309, 308, and 321 MiB respectively.

The field map consumes a temporary nominal entry list in a separate loop with one
insertion per back edge. Its final map is attached outside both index-building loops.
This shape avoids stale normalized map-result retains observed with nested whole-index
updates and a phase-dependent map accumulator; neither failed representation is used.

The driver tests the actual monomorphic index implementation and raw payloads, but
not the complete Core inference/frame lifecycle. Core integration must separately
verify that initial state creation and each constructor-layout replacement rebuild
all three maps; normal field instantiation and substitution must remain at query time.
