# challenges/

Stress-test programs that probe the edges of Ashes — written to **find the language's
flaws**, not to ship. Nothing here is part of the test or example suites: it is **not**
discovered or run by CI (`ci/jobs.sh`, `scripts/verify.sh`), and the `.ash` files are **not**
format-checked by any gate. Format them manually:

```bash
dotnet run --project src/Ashes.Cli -- fmt <file> -w
```

The interesting output of each challenge is its `FLAWS.md` — what broke, where, and why.

- [`1brc/`](1brc/README.md) — the One Billion Row Challenge; the original flaw-finder
  (surfaced and drove fixes for IO streaming, string ordering, hashing, the `take`/`uncons`
  cost models, and the still-open arena leak #2 / parallelism #5).
- The folders below are the [Benchmarks Game](https://benchmarksgame-team.pages.debian.net/benchmarksgame/)
  set — **scaffolds for now** (README only; `.ash` + run + `FLAWS.md` deferred).

## Benchmarks Game — coverage after `Ashes.Math` lands

[`docs/future/ASHES_MATH.md`](../docs/future/ASHES_MATH.md) is planned. After it lands, this
is where each benchmark stands. The math lib's real unlock for this set is the **Int↔Float
conversions** (`toFloat`, `*ToInt`) it introduces — Ashes has none today — plus the hermetic
`sqrt`. Notably **none of these need a Layer-2 transcendental** (`sin`/`cos`/`exp`/`ln`); only
the hermetic core is on the critical path. Two needs fall **outside** the math doc: a
**fixed-precision float formatter** (`fromFloat` hardcodes 6 fractional digits; n-body and
spectral-norm require 9 dp) and **bignum** (pidigits).

| Challenge | Covered by math lib? | Remaining gap |
|---|---|---|
| [binary-trees](binary-trees/README.md) | n/a (no math) | — |
| [fannkuch-redux](fannkuch-redux/README.md) | n/a (no math) | pure-solvable; probes arena churn #2 |
| [fasta](fasta/README.md) | ✅ `toFloat` | — |
| [mandelbrot](mandelbrot/README.md) | ✅ `toFloat` | — |
| [reverse-complement](reverse-complement/README.md) | n/a (no math) | — |
| [regex-redux](regex-redux/README.md) | n/a (no math) | regex-engine perf at scale |
| [k-nucleotide](k-nucleotide/README.md) | ✅ `toFloat` | percentages want 3-dp format (6-dp `fromFloat` is close) |
| [n-body](n-body/README.md) | ✅ `sqrt` | ❌ **9-dp float formatting** |
| [spectral-norm](spectral-norm/README.md) | ✅ `sqrt` + `toFloat` | ❌ **9-dp float formatting** |
| [pidigits](pidigits/README.md) | ❌ | ❌ **bignum** (out of scope of the math lib) |

Net: `Ashes.Math` **plus a fixed-precision float formatter** fully serves 9 of 10; pidigits
needs a separate bignum decision. Each folder's `README.md` has the per-benchmark detail.
