# challenges/

Stress-test programs that probe the edges of Ashes — written to **find the language's
flaws**, not to ship. Nothing here is part of the test or example suites: it is **not**
discovered or run by CI (`ci/jobs.sh`, `scripts/verify.sh`), and the `.ash` files are **not**
format-checked by any gate. Format them manually:

```bash
dotnet run --project src/Ashes.Cli -- fmt <file> -w
```

- [`1brc/`](1brc/README.md) — the One Billion Row Challenge; a full 1e9-row stress test that runs
  in ~8.3 s (every flaw it originally surfaced has since been fixed).
- [`server/`](server/README.md) — TCP + HTTP echo servers benchmarked against .NET baselines (own `bench.sh`).
- The folders below are the [Benchmarks Game](https://benchmarksgame-team.pages.debian.net/benchmarksgame/)
  set. [`bench.sh`](bench.sh) is a shared harness for the compute-bound ones (`bench.sh <name> [args]`
  — compiles at `-O2`, reports hyperfine time + peak RSS). Progress:

  | Challenge | State |
  |---|---|
  | [pidigits](pidigits/README.md) | **Benchmarked** — quantifies the BigInt arena churn (~O(N³) time, O(N²) RSS) |
  | [binary-trees](binary-trees/README.md) | **Benchmarked** — N=21 in 1.5 s; per-iteration arena reset fires (no OOM) |
  | [mandelbrot](mandelbrot/README.md) | **Benchmarked** — N=4000 in 0.7 s, constant RSS; found 2 `Float` inference bugs |
  | [fannkuch-redux](fannkuch-redux/README.md) | **Blocked** — surfaced 3 compiler bugs (FLAWS.md); crashes at N≥3 |
  | fasta, reverse-complement, n-body, spectral-norm, k-nucleotide, regex-redux | Scaffold (`.ash` deferred) |

## Compiler fixes to make (surfaced by benchmarking)

Each item is a real bug or gap the benchmarks above hit, with the current workaround (if any). Fixing
the two `Float`-inference items unblocks writing `n-body` / `spectral-norm` naturally; fixing the
`fannkuch` back-edge crash unblocks that benchmark. Minimal reproducers live in each challenge's
`FLAWS.md`.

- **fannkuch-redux** ([FLAWS.md](fannkuch-redux/FLAWS.md)) — blocks the benchmark:
  - [ ] **Two threaded `List` accumulators + early ADT return miscompiles** — the early (non-tail)
    return is dropped and the base case is taken. One list is fine; two is not. *Workaround:* pack
    both lists into one value threaded as a single argument.
  - [ ] **Back-edge use-after-reset (segfault)** — a `State`-of-two-lists accumulator threaded through
    a TCO loop is reclaimed by the arena reset while the next iteration still reads it (crashes at
    N≥3). A single list, and a list-of-records, do *not* crash — so it is specific to the nested
    pointer-in-ADT case. *No workaround yet; this is why fannkuch has no benchmark.*
  - [ ] **Spurious `ASH014`** — a non-recursive `let f x = … g …` that calls a recursive helper `g`,
    when `f` is itself called from a later recursive function, is wrongly rejected as "not yet
    declared" (and the error is mis-located). *Workaround:* mark `f` `let recursive`.
- **mandelbrot** ([FLAWS.md](mandelbrot/FLAWS.md)) — worked around; benchmark runs:
  - [ ] **`Float * Float` of annotated parameters mis-resolves to `Int`** — `*` picks its overload
    before the parameter's annotated type is applied. `+`/`-` are fine; only `*`, and only when both
    operands are bare params (a literal or function-result operand resolves correctly). *Workaround:*
    lead the product with a `Float` literal (`1.0 * zr * zr`).
  - [ ] **Recursive numeric accumulator types off its first operand** — a recursion arg like
    `cr + zr2 - zi2` mis-infers when it leads with a still-unresolved parameter. *Workaround:* lead
    with the resolved sub-expression (`zr2 - zi2 + cr`). (Same root cause as the `Int`-vs-`Float`
    accumulator defaulting seen in `pidigits`.)
  - [ ] **(feature) No raw-bytes stdout write** — `Ashes.IO.write` takes a UTF-8 `Str`, so the binary
    `P4` PBM output is not expressible; the benchmark reports an in-set pixel count instead. A
    `Bytes`-to-stdout write would let it emit the real image.
- **pidigits** ([FLAWS.md](pidigits/FLAWS.md)) — benchmark runs, but pathologically:
  - [ ] **Per-iteration `BigInt` garbage is not reclaimed within the loop** — every spigot step
    allocates fresh, growing-width `BigInt`s that the bump arena keeps resident, giving ~`O(N³)` time
    and `O(N²)` RSS and making the standard N=10000 infeasible. The arena reset that *does* fire for
    `binary-trees` does not fire here. (Memory-model / FLAWS #2 reclamation work.)
- **binary-trees** — no fix needed; included as the **positive baseline**: the per-iteration arena
  reset fires correctly for a discarded pointer-bearing ADT (constant memory, no OOM). Use it as the
  reference when fixing the pidigits/fannkuch reclamation bugs.

## Benchmarks Game — math-lib coverage

`Ashes.Math` has landed (see [STANDARD_LIBRARY.md](../docs/md/reference/standard-library.md#ashesmath)), and
this is where each benchmark stands. The math lib's real unlock for this set is the **Int↔Float
conversions** (`toFloat`, `*ToInt`) it introduces plus the hermetic `sqrt`. Notably **none of
these need a Layer-2 transcendental** (`sin`/`cos`/`exp`/`ln`); only the hermetic core is on the
critical path. Two needs fell **outside** the math lib: a **fixed-precision float formatter**
(`fromFloat` hardcodes 6 fractional digits; n-body and spectral-norm require 9 dp) — now shipped
as `Ashes.Text.formatFloat(value)(decimals)` — and **bignum** (pidigits), now shipped as the
native `Ashes.BigInt` type (see the [architecture notes](../docs/md/internals/architecture.md#bigint-arbitrary-precision-integers)).

| Challenge | Covered by math lib? | Remaining gap |
|---|---|---|
| [binary-trees](binary-trees/README.md) | n/a (no math) | — |
| [fannkuch-redux](fannkuch-redux/README.md) | n/a (no math) | pure-solvable; probes arena churn #2 |
| [fasta](fasta/README.md) | yes, `toFloat` | — |
| [mandelbrot](mandelbrot/README.md) | yes, `toFloat` | — |
| [reverse-complement](reverse-complement/README.md) | n/a (no math) | — |
| [regex-redux](regex-redux/README.md) | n/a (no math) | regex-engine perf at scale |
| [k-nucleotide](k-nucleotide/README.md) | yes, `toFloat` | — (3-dp percentages via `formatFloat`) |
| [n-body](n-body/README.md) | yes, `sqrt` | — (9-dp via `formatFloat`) |
| [spectral-norm](spectral-norm/README.md) | yes, `sqrt` + `toFloat` | — (9-dp via `formatFloat`) |
| [pidigits](pidigits/README.md) | n/a (integers) | — (native `Ashes.BigInt`) |

Net: `Ashes.Math` plus the fixed-precision float formatter serves the 9 float-oriented
benchmarks, and native `Ashes.BigInt` unblocks pidigits — the whole set is now expressible.
Each folder's `README.md` has the per-benchmark detail.
