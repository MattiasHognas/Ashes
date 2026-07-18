# n-body — Ashes Benchmarks Game challenge

Part of the `challenges/` flaw-finding suite (same ground rules as
[`../1brc/README.md`](../1brc/README.md): **not** run by CI; format `.ash` manually with
`dotnet run --project src/Ashes.Cli -- fmt <file> -w`).

> Source: [Benchmarks Game — n-body](https://benchmarksgame-team.pages.debian.net/benchmarksgame/performance/nbody.html)

## The benchmark

Simulate the Jovian planets (Sun, Jupiter, Saturn, Uranus, Neptune) under Newtonian gravity
for `N` timesteps with a symplectic integrator. Print the system energy before and after,
to 9 decimal places. Five bodies, a tight `N`-step loop, heavy float arithmetic.

## Intended Ashes approach

Each body is a `(x, y, z, vx, vy, vz, mass)` tuple; the system is a small fixed list. The
advance step computes pairwise distances and updates velocities/positions. The energy
calculation and the `1/distance³` force term both require a **square root**.

## What it probes (expected flaws)

- **Float throughput** in a long `N`-step loop: the distance term and the energy both use
  `Ashes.Number.Math.sqrt` (hardware `llvm.sqrt`, now shipped), so the real primitive is exercised
  rather than a hand-rolled Newton's-method approximation.
- **Fixed-precision formatting** to 9 dp via `Ashes.Text.formatFloat(value)(9)` (shipped).
- **Arena behaviour** of a fold that rebuilds the small body list each step.

## Dependencies / blockers

**None.** `Ashes.Number.Math.sqrt` (hardware square root) and `Ashes.Text.formatFloat` (9-dp
fixed-precision formatting) have both shipped — the benchmark is ready to implement.

## Status

**Implemented + benchmarked.** [`n-body.ash`](n-body.ash) is the intended pure solution: each body
a record, the system a fixed 5-element `List(Body)` rebuilt by `advance` every step, energy printed
to 9 dp via `Ashes.Number.Math.sqrt` + `Ashes.Text.formatFloat`. Output matches the reference
(`-0.169075164` / `-0.169059907` at the standard workload).

## Build & run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/n-body/n-body.ash -o challenges/n-body/n-body -O2
./challenges/n-body/n-body 50000000
```

## Benchmark

```bash
challenges/bench.sh n-body 50000000
```

Measured on a 32-thread AMD Ryzen 9 9950X3D, Linux x64 (single-threaded), `-O2`:

| N (steps) | Time | Peak RSS |
|-----------|------|----------|
| 1,000,000 | 0.43 s | 0.2 MB |
| 10,000,000 | 4.29 s | 0.2 MB |
| **50,000,000** (standard) | **21.4 s** | **0.2 MB** |

**Constant 0.2 MB at every N** — the `List(Body)` accumulator takes the whole-list deep clone
across the fixed-watermark reset (changelog CO-32; licensed because `advance(dt)(bodies)` rebuilds
the list every step), and the amortized compaction (CO-35) keeps the per-step copy cost sub-linear.
Before that arc this loop grew 4.27 GB per 1e6 steps. Time is ~0.43 us/step of pure Float
arithmetic; the mutable reference is ~6x faster per step, the price of rebuilding an immutable
5-record list per iteration.
