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
- The folders below are the [Benchmarks Game](https://benchmarksgame-team.pages.debian.net/benchmarksgame/)
  set — **scaffolds for now** (README only; `.ash` + run deferred).

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
