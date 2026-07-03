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

- **No math library — `sqrt` is missing.** There is currently no `sqrt`/trig/`pow`/`exp`
  intrinsic in the backend or stdlib, so the distance term cannot be computed directly. The
  benchmark is *blocked* on the math lib; writing it now would only measure a hand-rolled
  Newton's-method `sqrt`, not the real primitive.
- Once `sqrt` lands: float throughput in a long `N`-step loop, float formatting to fixed
  precision (`Ashes.Text.formatFloat(value)(9)` — shipped), and arena
  behaviour of a fold that rebuilds the small body list each step.

## Dependencies / blockers

**BLOCKED on the math lib (`sqrt`).** Defer until `Ashes.Math.sqrt` (or equivalent
intrinsic) exists. Fixed-precision 9-dp formatting is covered by `Ashes.Text.formatFloat`.

## Status

**Scaffold only.** `n-body.ash` and the `FLAWS.md` writeup are deferred — blocked on math lib.

## Build & run (once written, after math lib)

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/n-body/n-body.ash -o challenges/n-body/n-body
./challenges/n-body/n-body 50000000
```
