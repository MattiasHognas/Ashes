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
a record, the system a fixed five-body `System` record rebuilt by `advance` every step, energy
printed to 9 dp via `Ashes.Number.Math.sqrt` + `Ashes.Text.formatFloat`. Output matches the reference
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

Measured 2026-08-25 (`main` at `cf16de07`) on a 32-thread AMD Ryzen 9 9950X3D, Linux x64
(single-threaded), `-O2`; hyperfine three-run means (50M: single timed run), GNU `time` peak RSS
(8.0 MB is the runtime's fixed floor):

| N (steps) | Time | Peak RSS |
|-----------|------|----------|
| **50,000,000** (standard) | **1.80 s** | **8.0 MB** |

Against the same program in other languages at N=50,000,000 (see
[`../xlang/`](../xlang/README.md)): Rust 1.19 s, **Ashes 1.80 s**, OCaml 1.80 s, .NET 10 1.92 s,
Go 2.46 s. Every output is byte-identical.

One caveat on that ranking: this program writes its ten pair interactions out, while those ports
loop over a fixed array. An OCaml port unrolled the same way runs 1.61 s, so like-for-like Ashes
is about 9% slower than OCaml rather than ahead of it -- third behind Rust and OCaml. That is
still close for a program allocating a fresh record per step against one mutating five in place.

Two changes took this from 14.3 s. The system is a fixed five-body `System` record instead of a
`List(Body)`, and each pair is evaluated once rather than twice.

The list cost more than its allocation. Rebuilding five cons cells and five records per step is
the obvious part; the less obvious part is that computing accelerations needs the whole list live
while the new one is being built, which is real aliasing, so in-place reuse could not fire on it
(`--explain ownership` reported `updatePos.bodies` unique yes but `updateVel.remaining` unique no).
Declining there was correct, not a compiler gap. A fixed record has neither problem.

Evaluating each pair once halves the square roots, twenty per step to ten. In a pure setting this
needs both bodies of a pair updated from one computed magnitude, which the fixed record makes
natural — each velocity component is a sum of the four interactions that body takes part in.

**Constant resident memory at every N**, unchanged by either change.

The previous formulation is kept as `n-body-list.ash`. It is no longer the benchmark entry, but it
is the shape that exercises the arena/reference-counted list path — the whole-list deep clone
across the fixed-watermark reset (changelog CO-32) and the amortized compaction (CO-35), which
before that arc grew 4.27 GB per 1e6 steps — so it stays runnable.
