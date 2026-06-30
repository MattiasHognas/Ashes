# Ashes.Math — Status & Roadmap

**Status:** Planned (design converged; not started).

A standard-library math module. It is delivered in **two layers**: a fully self-contained *hermetic
core* implemented without any native library, and a *native-backed* layer of floating-point
transcendentals provided by a **vendored `libm`**. It reuses rustls's no-runtime-dependency
*principle* (vendor compile-time, ship nothing extra to the end-user) but **not** rustls's embedding
*mechanism* — see "Embedding mechanism" below. No layer introduces a runtime dependency.

## The "no runtime dependencies" invariant

Ashes ships standalone native executables with no GC and no runtime (Ground Rule 6). Math must not
break that. It doesn't, because the only external dependency is **compile-time** and gets baked into
the output. Compare the three native dependencies the project already manages:

| Dependency | Kind | In the user's executable? | End-user needs it? |
|---|---|---|---|
| LLVM | Build-time, compiler-internal | No — runs on the compiler's machine only | No |
| rustls | Vendored; whole `.so`/`.dll` embedded, extracted + `dlopen`'d at startup | Yes, baked in | **No** |
| `libm` (this proposal) | Vendored; **static archive linked with section-GC**, only into programs that use transcendentals | Yes, baked in | **No** |

`libm` shares rustls's *outcome* — a vendored payload, baked in by `LlvmImageLinker` only when a
transcendental is actually used, with the end-user running a self-contained binary and never a
dynamic link against a system `libm.so`. (A dynamically-linked option was rejected: the image linker
emits static images and a runtime dependency would violate Ground Rule 6.) But it uses a **different
embedding mechanism**, deliberately, to meet the size requirement.

### Embedding mechanism — why `libm` differs from rustls

rustls is consumed as a **prebuilt shared library**: `HermeticTlsRuntimeAssets` embeds the entire
`librustls.so` / `rustls.dll` blob, and at startup the program extracts it to a file and `dlopen`s
it. The whole library ships — there is **no dead-stripping** (it can't be: a finished `.so` is not
relinkable, and a TLS handshake exercises most of it anyway).

`libm` must not copy that. Transcendental users typically need only a handful of functions, so the
whole-library approach would bloat every binary. Instead, `libm` is vendored as a **static archive
(or LLVM bitcode)** and **statically linked into the image with section-level garbage collection**
(`--gc-sections` / LTO `internalize` + `GlobalDCE`), so only the functions actually referenced are
emitted, and there is no runtime extract-and-`dlopen` step. This is the concrete reason the candidate
must be buildable as a static archive (see implementation step 2), not just available as a shared
library.

(Retrofitting the same static-archive + dead-strip treatment onto rustls is possible but out of
scope here — it would mean building rustls from source as a static lib and is a much larger change
for a modest win.)

## Layer 1 — hermetic core (no library)

Everything here is implementable with existing Ashes arithmetic plus the `sqrt` hardware
instruction, so it needs no native payload and can ship as ordinary `lib/Ashes/Math.ash` (with a
small number of backend intrinsics for `sqrt` and Int↔Float conversion).

### Integer

- `abs(n)` returning `Int`
- `signum(n)` returning `Int` — `-1`, `0`, or `1`
- `min(a, b)` / `max(a, b)` returning `Int`
- `clamp(lo, hi, n)` returning `Int`
- `gcd(a, b)` / `lcm(a, b)` returning `Int`
- `divMod(a, b)` returning `(Int, Int)` — Euclidean quotient and remainder
- `pow(base, exp)` returning `Int` — exponentiation by squaring (`exp >= 0`)
- `isqrt(n)` returning `Int` — integer (floor) square root, `n >= 0`

### Floating-point (hermetic subset)

- `absF(x)` / `signumF(x)` / `minF(x, y)` / `maxF(x, y)` / `clampF(lo, hi, x)`
- `sqrt(x)` returning `Float` — backed by the hardware `sqrt` instruction (`llvm.sqrt`), no library
- `floor(x)` / `ceil(x)` / `round(x)` / `trunc(x)` returning `Float`
- Constants: `pi`, `e`, `tau` returning `Float`

### Conversions (new primitives this module introduces)

Ashes has no Int↔Float numeric conversion today (only `Ashes.Text.fromInt : Int -> Str`). Math
introduces:

- `toFloat(n)` returning `Float` — widen an `Int`
- `floorToInt(x)` / `roundToInt(x)` / `truncToInt(x)` returning `Int` — narrow a `Float`

(These may instead live in the language core; see open questions.)

## Layer 2 — native-backed transcendentals (vendored `libm`)

Provided by the embedded `libm`. Bit-accurate, full-range, the standard C99 surface:

- Powers / roots: `powF(x, y)`, `cbrt(x)`, `hypot(x, y)`
- Exponential / logarithmic: `exp(x)`, `expm1(x)`, `ln(x)`, `log2(x)`, `log10(x)`, `log1p(x)`
- Trigonometric: `sin(x)`, `cos(x)`, `tan(x)`, `asin(x)`, `acos(x)`, `atan(x)`, `atan2(y, x)`
- Hyperbolic: `sinh(x)`, `cosh(x)`, `tanh(x)`
- Remainder: `fmod(x, y)`

## Accuracy and semantics

- Layer 2 inherits the vendored `libm`'s accuracy (typically `< 1 ulp`); this is documented per
  function rather than guaranteed bit-identical across targets unless the same `libm` source builds
  for all three.
- Domain errors (`sqrt(-1.0)`, `ln(0.0)`) follow IEEE-754: return `NaN`/`-inf`, **not** a panic, so
  the functions stay total. (Whether to also expose `isNaN`/`isInf` classification is an open
  question.)
- The hermetic `sqrt` and Layer-2 `sqrt`-adjacent results must agree on shared inputs.

## Implementation plan

Per Ground Rule 1, `STANDARD_LIBRARY.md` (and `LANGUAGE_SPEC.md` for the new conversion primitives)
are updated before implementation.

1. **Hermetic core first.** Write `lib/Ashes/Math.ash` for the integer surface and the
   pure-arithmetic Float helpers. Add the small backend intrinsics it leans on — `sqrt` (`llvm.sqrt`),
   `toFloat`, and the rounding/`*ToInt` conversions — following the standard intrinsic pipeline
   (frontend registration → `BuiltinRegistry` → lowering → backend codegen → diagnostics → tests).
   This layer ships with no native payload and no `runtimes/` work.

2. **Vendor `libm`.** Add a vendored `libm` per target (`linux-x64`, `linux-arm64`, `win-x64`) under
   `runtimes/`, mirroring the rustls provisioning (`scripts/download-rustls-ffi.sh`,
   `Directory.Build.props` version pin). The candidate must satisfy three hard requirements:

   - **Builds for all three targets** — x86-64 **and** AArch64, on **both** Linux and Windows. A
     Linux-only `libm` is disqualified, since `win-x64` is a first-class target.
   - **Buildable as a static archive / bitcode** — not just a prebuilt shared library — so it can be
     statically linked and dead-stripped (see "Embedding mechanism" above). A `.so`-only distribution
     is disqualified, because it would force the rustls whole-library mechanism and miss the size
     target.
   - **Small** — embedding adds to every transcendental-using binary, so the payload must be compact;
     link only the referenced object code (`--gc-sections` / LTO + `GlobalDCE`) rather than the whole
     archive.
   - **Freestanding-friendly** — minimal libc coupling, so it links against the Ashes runtime rather
     than a real libc.

   **openlibm** is the recommended source: it is a portable, standalone `libm` (originally from the
   Julia project), permissively licensed, builds for x86-64/AArch64 on Linux and Windows, is compact,
   and is explicitly designed for embedding with minimal libc coupling — it meets all three
   requirements. musl's `libm` is small and clean but is Linux-centric (no first-class Windows
   build), which fails the cross-OS requirement, so it is only a fallback for the Linux targets. The
   main integration risk is satisfying the handful of libc symbols the chosen `libm` references (e.g.
   `memcpy`, `abort`) from the Ashes runtime rather than a real libc.

3. **Link on demand, dead-stripped.** Register the Layer-2 functions as intrinsics that resolve to
   `libm` symbols, and extend `LlvmImageLinker` to **statically link the vendored `libm` archive and
   run section-GC** so only referenced functions are emitted — and only when a transcendental
   intrinsic is referenced at all (the conditional trigger mirrors the rustls TLS gate, even though
   the link mechanism is static-archive rather than whole-`.so` embedding). Hermetic-only programs
   link nothing extra.

4. **Tests + examples** per Ground Rule 3: numeric-accuracy tests with tolerance bounds, NaN/inf
   edge cases, a binary-size check confirming hermetic-only programs pull in no `libm`, and an
   example exercising both layers.

## Open questions

- **Which `libm`** — openlibm (recommended) vs musl, decided by the three hard requirements above:
  it must build for x86-64 **and** AArch64 on **both** Linux and Windows, stay small (dead-stripped to
  only the referenced functions), and link freestanding against the Ashes runtime. musl's Linux-only
  story currently rules it out as the primary choice.
- **Conversion home** — `toFloat` / `*ToInt` in `Ashes.Math` or in the language core (they are
  generally useful beyond math).
- **Domain-error policy** — IEEE `NaN`/`inf` returns (proposed) vs a checked `Result`-returning
  variant; and whether to expose `isNaN` / `isInf` / `isFinite`.
- **Naming for Float vs Int overloads** — suffix style (`absF`, `minF`) as proposed, vs a single
  overloaded name resolved by type (depends on how far operator/overload resolution is taken).
- **Cross-target bit-reproducibility** — whether to guarantee identical results on all targets by
  building one `libm` source everywhere, or document per-target ulp bounds.

## Ground rules touched

- **Rule 1 (spec first):** the module surface lands in `STANDARD_LIBRARY.md`, and the new conversion
  primitives in `LANGUAGE_SPEC.md`, before implementation.
- **Rule 6 (no GC / no runtime deps):** preserved. `libm` is a **compile-time vendored asset
  statically embedded** into the executable (the rustls model), never a dynamic runtime dependency;
  programs that don't use transcendentals embed nothing.
