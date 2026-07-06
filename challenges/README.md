# challenges/

Stress-test programs that probe the edges of Ashes — written to **find the language's
flaws**, not to ship. Nothing here is part of the test or example suites: it is **not**
discovered or run by CI (`ci/jobs.sh`, `scripts/verify.sh`), and the `.ash` files are **not**
format-checked by any gate. Format them manually:

```bash
dotnet run --project src/Ashes.Cli -- fmt <file> -w
```

Where a challenge surfaces compiler flaws, they are recorded in its `FLAWS.md`.

- [`1brc/`](1brc/README.md) — the One Billion Row Challenge; a full 1e9-row stress test that runs
  in ~8.3 s (every flaw it originally surfaced has since been fixed).
- The folders below are the [Benchmarks Game](https://benchmarksgame-team.pages.debian.net/benchmarksgame/)
  set — **scaffolds for now** (README only; `.ash` + run + `FLAWS.md` deferred).

## Benchmarks Game — math-lib coverage

`Ashes.Math` has landed (see [STANDARD_LIBRARY.md](../docs/md/reference/standard-library.md#ashesmath)), and
this is where each benchmark stands. The math lib's real unlock for this set is the **Int↔Float
conversions** (`toFloat`, `*ToInt`) it introduces plus the hermetic `sqrt`. Notably **none of
these need a Layer-2 transcendental** (`sin`/`cos`/`exp`/`ln`); only the hermetic core is on the
critical path. Two needs fell **outside** the math lib: a **fixed-precision float formatter**
(`fromFloat` hardcodes 6 fractional digits; n-body and spectral-norm require 9 dp) — now shipped
as `Ashes.Text.formatFloat(value)(decimals)` — and **bignum** (pidigits), still open.

| Challenge | Covered by math lib? | Remaining gap |
|---|---|---|
| [binary-trees](binary-trees/README.md) | n/a (no math) | — |
| [fannkuch-redux](fannkuch-redux/README.md) | n/a (no math) | pure-solvable; probes arena churn #2 |
| [fasta](fasta/README.md) | ✅ `toFloat` | — |
| [mandelbrot](mandelbrot/README.md) | ✅ `toFloat` | — |
| [reverse-complement](reverse-complement/README.md) | n/a (no math) | — |
| [regex-redux](regex-redux/README.md) | n/a (no math) | regex-engine perf at scale |
| [k-nucleotide](k-nucleotide/README.md) | ✅ `toFloat` | — (3-dp percentages via `formatFloat`) |
| [n-body](n-body/README.md) | ✅ `sqrt` | — (9-dp via `formatFloat`) |
| [spectral-norm](spectral-norm/README.md) | ✅ `sqrt` + `toFloat` | — (9-dp via `formatFloat`) |
| [pidigits](pidigits/README.md) | ❌ | ❌ **bignum** (out of scope of the math lib) |

Net: `Ashes.Math` plus the (now shipped) fixed-precision float formatter fully serves 9 of 10;
pidigits needs a separate bignum decision. Each folder's `README.md` has the per-benchmark detail.
