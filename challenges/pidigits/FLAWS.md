# pidigits — findings

## Summary

pidigits was the one benchmark in this suite blocked on a *capability* gap rather than a
performance one: the unbounded spigot needs arbitrary-precision integers, and Ashes `Int` is a
fixed 64-bit machine word. That gap is now closed by the native `BigInt` type (see
[the architecture notes](../../docs/md/internals/architecture.md#bigint-arbitrary-precision-integers)),
so `pidigits.ash` expresses the canonical algorithm directly and correctly. The first digits match
pi (`3141592653 5897932384 6264338…`).

## What it exercised in the compiler

Implementing it drove the whole `BigInt` surface to completeness:

- **Arithmetic + operators.** The spigot is written with `+`, `-`, `*`, `/` and `<` on `BigInt`
  and `N` literals (`10N`, `3N`), so it reads like the mathematical recurrence. This is what
  motivated overloading the numeric operators for `BigInt` and adding the `%` operator.
- **Growth / arena churn (the perf probe).** `q`, `r`, `t` grow without bound; every step
  allocates fresh normalized `BigInt` values in the arena. A long run (`./pidigits 10000`) is a
  direct stress test of the immutable-value + bump-arena model under a numeric hot loop — exactly
  the cost the memory model predicts. No leak or corruption surfaced; throughput is bounded by
  allocation volume and by the division algorithm. **Update:** memory is now **constant** (0.25 MB at
  every `N`, down from `O(N²)`). Two changes: (1) a `BigInt` is a self-contained buffer, so it is
  copied out across the TCO reset like a `String` and the reset fires, freeing the intermediate values;
  (2) a loop threading only non-sharing whole-value accumulators (`q, r, t` `BigInt`s + the `String`
  output, no cons-lists) resets to a *fixed* loop-entry watermark, so each grown accumulator overwrites
  the previous one rather than being stranded below an advancing watermark. Only **time** remains
  super-linear (~`O(N³)`) — the binary long-division cost, a bignum-algorithm follow-up (Algorithm D /
  Karatsuba), not a memory-model issue.
- **Division cost.** The runtime currently uses binary long division (O(bits·limbs)); it is the
  hot operation in the digit-extraction step. Knuth Algorithm D is the documented performance
  follow-up. This benchmark is the natural place to measure whether that upgrade is worth it.

## Not flaws

Everything the benchmark needs is present and correct. The remaining items are performance
follow-ups (Algorithm D division, Karatsuba multiply, reuse/in-place bignum), not defects — the
output is exact at any digit count.

## Run

```bash
dotnet run --project src/Ashes.Cli -- compile challenges/pidigits/pidigits.ash -o challenges/pidigits/pidigits
./challenges/pidigits/pidigits 10000
```
