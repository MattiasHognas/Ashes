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
  ordered result-name buckets with linear reference scans. Cases include duplicate
  names with different payloads/results, case-sensitive names, duplicate result names
  with different IDs, unknown names, non-named results, and generic raw schemes.
  Separate substitutions applied after lookup must produce different concrete fields;
  the index itself retains unresolved declarations. Empty/fresh indexes are checked.
- `old` / `new`: 20,000 lookups over 2,000 declarations, printing `40000` in both cases.
- `old1000` / `old10000` / `new1000` / `new10000`: rebuild fresh declarations and
  indexes per iteration, printing `2000` / `20000`. The new query is called after the
  source declaration list's builder returns, checking retained names and payloads.

All three modes pass. Both old and new repetition tests are flat at one live cell.
Measured lookup time: s0 0.90 seconds versus below 0.01 seconds; s1 0.72 seconds
versus below 0.01 seconds. These are isolated lookup timings, not compiler timings.

The driver tests the actual monomorphic index implementation and raw payloads, but
not the complete Core inference/frame lifecycle. Core integration must separately
verify that initial state creation and each constructor-layout replacement rebuild
both maps; normal field instantiation and substitution must remain at query time.
