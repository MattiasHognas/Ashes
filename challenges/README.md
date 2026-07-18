# challenges/

Stress-test programs that probe the edges of Ashes — written to **find the language's
flaws**, not to ship. Nothing here is part of the test or example suites: it is **not**
discovered or run by CI (`ci/jobs.sh`, `scripts/verify.sh`), and the `.ash` files are **not**
format-checked by any gate. Format them manually:

```bash
dotnet run --project src/Ashes.Cli -- fmt <file> -w
```

Every defect the suite surfaced is triaged in **[BUGS.md](BUGS.md)** — 18 found, 17 fixed, one
partial remainder. The analysis and measurements for each fix live in
[`docs/md/internals/changelog.md`](../docs/md/internals/changelog.md), each fix ships with a
regression test under `tests/`, and every benchmark below is written in its natural,
workaround-free form.

## The suite

- [`1brc/`](1brc/README.md) — the One Billion Row Challenge; the full 1e9-row workload in
  **8.4 s** on 32 threads (~120 M rows/s), byte-identical to a sequential fold.
- [`server/`](server/README.md) — TCP + HTTP echo servers benchmarked against .NET baselines
  (own `bench.sh`); ~320k TCP / ~239k HTTP req/s at c=64, ~2-2.7x the dotnet equivalents.
- The folders below are the [Benchmarks Game](https://benchmarksgame-team.pages.debian.net/benchmarksgame/)
  set. [`bench.sh`](bench.sh) is the shared harness for the compute-bound ones
  (`bench.sh <name> [args]` — compiles at `-O2`, reports hyperfine time + peak RSS; stdin
  fixtures via `BENCH_STDIN=<file>`). Each folder's README has the full table and analysis.

## Benchmarks Game results

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64, `-O2`, single-threaded. All outputs match
the reference implementations.

| Challenge | Standard workload | Time | Peak RSS | Note |
|---|---|---|---|---|
| [pidigits](pidigits/README.md) | N=10,000 | 3.50 s | 1.2 MB | Algorithm D bignum division (was O(N^3) bit-division; N=1000 went 3.46 s -> 0.029 s) |
| [binary-trees](binary-trees/README.md) | N=21 | 1.41 s | 192 MB | arena reclaims tens of millions of discarded nodes; RSS tracks the long-lived tree |
| [mandelbrot](mandelbrot/README.md) | N=16,000 | 13.5 s | 1.7 GB | real P4 PBM output; RSS is the packed-bitmap cons list |
| [fannkuch-redux](fannkuch-redux/README.md) | N=11 | 27.5 s | 0.2 MB | constant memory at every N; time-bound only (N! enumeration) |
| [n-body](n-body/README.md) | N=50,000,000 | 21.4 s | 0.2 MB | constant memory: whole-list clone of the rebuilt `List(Body)` across the reset |
| [spectral-norm](spectral-norm/README.md) | N=5,500 | 4.72 s | 1.5 MB | clean O(N^2) scaling, 9-dp output exact |
| [fasta](fasta/README.md) | N=25,000,000 | 17.4 s | 786 MB | natural `acc + ch` accumulator; affine reservation growth made it amortized O(1)/byte |
| [reverse-complement](reverse-complement/README.md) | fasta 1M input | 0.58 s | 944 MB | linear, but ~96 B/base constant — the one open BUGS.md item (cons-cell reuse) |
| [k-nucleotide](k-nucleotide/README.md) | fasta 1M input | 11.3 s | 123 MB | persistent-Map counting; gap to reference = immutable map vs mutable hashtable |
| [regex-redux](regex-redux/README.md) | fasta 5M input | 63.7 s | 1.3 GB | correct + bounded memory; superlinear time from per-pass subject materialization |

Two compiler bugs were found and fixed during this very benchmark rerun — the suite doing its job:

- **Large-list copy-out stack overflow (CO-37):** the scope-exit list copier cached heads in an
  unbounded dynamic stack alloca; mandelbrot's packed-bitmap list (N^2/8 cells, then reversed —
  two copies in one entry frame) segfaulted at N >= 2500. Large caches now spill to OS memory.
- **Whole-list DeepAdt clone gating:** the `List(ADT)` back-edge clone (CO-32) was licensed by
  TYPE, so 1brc's merge loops — which walk a `List(tuple)` by pattern tails — deep-copied the
  remainder every iteration (~400x time, ~27x memory, OOM on the full file). The clone is now
  licensed per argument, only for freshly rebuilt lists (n-body's shape).

## Math-lib coverage

`Ashes.Number.Math` (see [the standard library](../docs/md/reference/standard-library.md#ashesmath))
plus `Ashes.Text.formatFloat` (fixed-precision formatting) and native `Ashes.Number.BigInt` cover the
whole set; **none of these benchmarks needs a Layer-2 transcendental** (`sin`/`cos`/`exp`/`ln`) —
only the hermetic core (`sqrt`, `toFloat`, `*ToInt`) is on any critical path.

| Challenge | Math dependency |
|---|---|
| binary-trees, fannkuch-redux, reverse-complement, regex-redux | none |
| fasta, mandelbrot, k-nucleotide | `toFloat` (+ `formatFloat` percentages) |
| n-body, spectral-norm | `sqrt` + `formatFloat` (9 dp) |
| pidigits | native `Ashes.Number.BigInt` |
