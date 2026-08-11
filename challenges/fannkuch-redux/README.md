# fannkuch-redux — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — fannkuch-redux](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/fannkuchredux.html)

## The benchmark

Generate every permutation of `[1..N]` (in a specific successive order), and for each one
repeatedly "flip" the prefix of length `p[0]` (reverse the first `p[0]` elements) until the
first element is `1`, counting the flips. Print the maximum flip count and a checksum.

## Intended Ashes approach

**Fully pure — no mutation required.** Mutable arrays in the reference solutions are a
*speed* convenience (this is a performance benchmark), not a correctness need. Represent the
permutation as a plain `List(Int)`: reading `p[0]` is `head` (O(1)), and the inner "reverse
the first `k` elements" operation is a single-pass `flipPrefix k p` that conses the first `k`
onto the tail — **O(k), the same asymptotic cost as an in-place array reverse**. Permutation
generation uses the standard counting/rotation scheme, also pure. A plain `List` matches the
access pattern (O(1) head); `Ashes.Collection.Array` (persistent tree, O(log N)) is the *wrong* choice
here.

> Implementation note: do **not** build the flip from stdlib `take`+`reverse`+`append` —
> `take` is O(count²) (it concatenates), which would make each
> flip quadratic. Hand-write `flipPrefix` as one pass to stay O(k).

## What it probes (expected flaws)

This benchmark is *fully expressible purely* — it is **not** blocked on any missing
capability. What it stresses:

- **Allocation churn / arena reclamation.** Every flip allocates a fresh list, so
  a tight combinatorial loop (millions of permutations × flips) produces garbage the non-GC
  bump arena must reclaim. The headline probe: does the per-iteration arena reset fire, or
  does memory grow toward OOM under a hot integer loop with no IO?
- **Constant-factor throughput vs the mutable reference.** Asymptotics match the array
  version (O(k) flip); this measures how close *pure immutable + arena* gets on raw speed —
  the whole point of the benchmark.
- Tail-recursion / TCO behaviour of the permutation-generation and flip loops.
- Data-parallel sharding of permutation ranges that the reference uses for its
  speed — inexpressible while `Ashes.Task.Parallel` is sequential.

## Dependencies / blockers

None — pure integer + `List`. No math lib and **no mutation** needed. The interesting
findings are allocation/throughput and the `take` quadratic gotcha, not any missing language
feature.

## Status

**Implemented + benchmarked.** [`fannkuch-redux.ash`](fannkuch-redux.ash) is the intended fully-pure
solution (List-based permutation, one-pass O(k) `flip`, faithful factorial-order enumeration). Writing
it surfaced **three distinct compiler bugs** — exactly the flaw-finding this challenge exists for —
**all now fixed**:

1. a self-recursive function threading **two** `List` accumulators with an early ADT return dropped
   the early return — the TCO shallow copy-out only preserved a list's top cons cell;
2. a spurious `ASH014` for a non-recursive helper that calls a recursive helper;
3. a **use-after-reset** segfault of a pointer-bearing accumulator across the TCO back-edge (same root
   as (1)).

Output is correct against the reference at every N (checksum / `Pfannkuchen(N)`). Resident memory was
constant for a period (the `State(perm, count)` accumulator carried across the TCO reset by a recursive
deep copy, reset to the fixed loop-entry watermark), but the pattern-binding Perceus cutover
(`docs/md/internals/changelog.md`'s "Pattern-binding Perceus cutover" entry, PR #357) superseded the
ownership machinery that deep-copy relied on, and peak RSS now scales with `N!` again — a known,
already-documented tradeoff of that cutover, explicitly deferred to a future memory-model milestone
(see `fannkuch-redux.ash`'s own header comment), not a new regression. Larger N is therefore bounded
by both *time* and *memory* (`N!` enumeration).

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/fannkuch-redux/fannkuch-redux.ash -o challenges/fannkuch-redux/fannkuch-redux
./challenges/fannkuch-redux/fannkuch-redux 7
```

## Benchmark

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`. All outputs match
the reference. The Time/Peak RSS columns below are current (single-run wall-clock + GNU `time -v`,
not the original hyperfine-averaged flat-memory figures they replace — see the note above):

| N | checksum / Pfannkuchen | Time | Peak RSS |
|---|---|------|----------|
| 9 | 8629 / 30 | 0.26 s | 99 MB |
| 10 | 73196 / 38 | 2.90 s | 1.1 GB |
| 11 | 556355 / 51 | 37.3 s | 13.4 GB |

Constant resident memory at every N (the `State(perm, count)` accumulator deep-copies across the
fixed-watermark reset); larger N is bounded only by time (`N!` enumeration). Permutation-range
sharding over `Ashes.Task.Parallel` (still sequential here) is what would close the speed gap to the
reference at N=12.
