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
  | [pidigits](pidigits/README.md) | **Benchmarked** — per-iteration BigInt garbage now reclaimed (N=1000 172 MB → 60 MB); residual accumulator growth is the memory-model milestone |
  | [binary-trees](binary-trees/README.md) | **Benchmarked** — N=21 in 1.5 s; per-iteration arena reset fires (no OOM) |
  | [mandelbrot](mandelbrot/README.md) | **Benchmarked** — both `Float` inference bugs fixed (natural spelling); emits the real P4 PBM via `writeBytes` |
  | [fannkuch-redux](fannkuch-redux/README.md) | **Benchmarked** — all 3 compiler bugs fixed; correct output (N=7→228/16 … N=10→73196/38); memory-bound at large N |
  | fasta, reverse-complement, n-body, spectral-norm, k-nucleotide, regex-redux | Scaffold (`.ash` deferred) |

## Compiler fixes made (surfaced by benchmarking)

Every bug the benchmarks above surfaced has been fixed; the benchmarks are written in their natural,
workaround-free form. Minimal reproducers live in each challenge's `FLAWS.md`, and each fix ships with
a regression test under `tests/`.

- **fannkuch-redux** ([FLAWS.md](fannkuch-redux/FLAWS.md)) — all three bugs fixed; benchmark runs:
  - [x] **Two threaded `List` accumulators + early ADT return miscompiled** — the shallow single-cell
    TCO copy-out only preserved a list's top cons cell; a multi-cell/rebuilt list left interior cells
    dangling, so the early return was dropped. Now the reset is disqualified for any list accumulator
    that is not a single fresh cons.
  - [x] **Back-edge use-after-reset (segfault)** — same root cause; the `State`-of-two-lists
    accumulator no longer takes the unsound shallow copy across the reset.
  - [x] **Spurious `ASH014`** — a non-recursive helper calling a recursive one is no longer rejected;
    the backward-reference reconstruction no longer requires the specialization path.
- **mandelbrot** ([FLAWS.md](mandelbrot/FLAWS.md)) — written naturally; emits the real image:
  - [x] **`Float * Float` of annotated parameters resolved to `Int`** — the operator overload was
    picked before the parameter's annotation applied. Annotated parameter types are now seeded before
    the body is lowered, so `zr * zr` and `cr + zr2 - zi2` resolve as `Float` with no `1.0 *` lead or
    operand reordering.
  - [x] **Recursive numeric accumulator typed off its first operand** — same seeding fix.
  - [x] **(feature) Raw-bytes stdout write** — added `Ashes.IO.writeBytes : Bytes -> Unit`; mandelbrot
    now emits the real binary `P4` PBM instead of a pixel count.
- **pidigits** ([FLAWS.md](pidigits/FLAWS.md)) — benchmark runs, per-iteration garbage reclaimed:
  - [x] **Per-iteration `BigInt` garbage now reclaimed** — a BigInt is a self-contained buffer, so it
    is copied out across the TCO reset like a `String`; the reset fires and reclaims the spigot's
    intermediate `BigInt`s (N=1000 172 MB → 60 MB).
  - [ ] **Residual O(N²) accumulator growth** *(memory-model milestone, still open)* — a *growing*
    heap accumulator (BigInt or String) threaded through a loop still accumulates: each iteration's
    whole-value copy is preserved below the advancing watermark, and a helper that *returns* a growing
    list deep-copies it out of its arena scope on every call. This is the same root as fannkuch's
    memory growth at large N, and needs ownership / in-place-reuse (FLAWS #2), not a point fix.
- **binary-trees** — no fix needed; the **positive baseline**: the per-iteration arena reset fires
  correctly for a discarded pointer-bearing ADT (constant memory, no OOM).

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
