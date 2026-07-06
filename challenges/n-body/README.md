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
  `Ashes.Math.sqrt` (hardware `llvm.sqrt`, now shipped), so the real primitive is exercised
  rather than a hand-rolled Newton's-method approximation.
- **Fixed-precision formatting** to 9 dp via `Ashes.Text.formatFloat(value)(9)` (shipped).
- **Arena behaviour** of a fold that rebuilds the small body list each step.

## Dependencies / blockers

**None.** `Ashes.Math.sqrt` (hardware square root) and `Ashes.Text.formatFloat` (9-dp
fixed-precision formatting) have both shipped — the benchmark is ready to implement.

## Status

**Scaffold only.** `n-body.ash` and the `FLAWS.md` writeup are not written yet, but the
benchmark is **unblocked** — nothing is missing to implement it.

## Build & run (once written)

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/n-body/n-body.ash -o challenges/n-body/n-body
./challenges/n-body/n-body 50000000
```
