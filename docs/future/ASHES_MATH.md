# Ashes.Math — Status & Roadmap

**Status:** In progress. Provisioning (source-build script + vendored linux payloads + csproj
wiring) and the **Layer-1 hermetic core are implemented** — the integer surface, the Float helpers
(`absF`/`signumF`/`minF`/`maxF`/`clampF`, constants), the `sqrt`/`floor`/`ceil`/`round`/`trunc`
intrinsics (via `llvm.*`), and the `toFloat`/`*ToInt` conversions (sitofp/fptosi). Remaining: the
Layer-2 openlibm-backed transcendentals (embed + gated link) and the win-x64 payload.

A standard-library math module. It is delivered in **two layers**: a fully self-contained *hermetic
core* implemented without any native library, and a *native-backed* layer of floating-point
transcendentals provided by a **vendored `libm` implementation — openlibm** (the recommended choice;
see step 2). It reuses rustls's no-runtime-dependency *principle* (vendor compile-time, ship nothing
extra to the end-user) but **not** rustls's embedding *mechanism* — see "Embedding mechanism" below.
No layer introduces a runtime dependency.

> **Terminology:** *`libm`* is the generic name for the C math-library interface (the symbols
> `sin`, `cos`, `exp`, …), not a specific product. **openlibm** is the concrete implementation of
> that interface this proposal vendors. Below, "`libm`" refers to the interface/symbols and
> "openlibm" to the payload we ship.

## The "no runtime dependencies" invariant

Ashes ships standalone native executables with no GC and no runtime (Ground Rule 6). Math must not
break that. It doesn't, because the only external dependency is **compile-time** and gets baked into
the output. Compare the three native dependencies the project already manages:

| Dependency | Kind | In the user's executable? | End-user needs it? |
|---|---|---|---|
| LLVM | Build-time, compiler-internal | No — runs on the compiler's machine only | No |
| rustls | Vendored; whole `.so`/`.dll` embedded, extracted + `dlopen`'d at startup, **gated on TLS use** | Yes, baked in when TLS used | **No** |
| openlibm (this proposal) | Vendored; whole `.so`/`.dll` embedded, **gated on transcendental use** | Yes, baked in when a transcendental is used | **No** |

openlibm shares rustls's *outcome* — a vendored payload, baked in by `LlvmImageLinker` only when a
transcendental is actually used, with the end-user running a self-contained binary and never a
dynamic link against a system `libm.so`. (A dynamically-linked-against-the-host option was rejected:
the image linker emits static images and a runtime dependency on a system `libm.so` would violate
Ground Rule 6.)

### Provisioning — no upstream prebuilt, so build from source

Unlike rustls-ffi (which publishes prebuilt release zips for `linux-x64` and `win-x64`), **openlibm
publishes no prebuilt shared libraries** — its GitHub releases ship source only. So the provisioning
script builds openlibm from source for every target, the way `download-rustls-ffi.sh` already builds
the `linux-arm64` rustls payload from source. See "Provisioning script" below. The output per target
is a `.so`/`.dll` staged under `runtimes/<rid>/`, plus an `openlibm.version` marker, exactly
mirroring the rustls asset layout.

### Conditional embedding — gated exactly like rustls

Whichever link mechanism is chosen (below), the *gate* is the same and is **not** a new capability:
`LlvmCodegen` already computes `ProgramUsesTlsRuntimeAbi(program)` and only pulls in the rustls asset
when the IR references a TLS/HTTP instruction (`LoadLinkedTlsRuntimeAsset`). openlibm mirrors this
with a `ProgramUsesMathRuntimeAbi(program)` gate (true when the IR references any Layer-2
transcendental intrinsic). Layer-1-only and math-free programs pull in nothing.

### Link mechanism — two viable hermetic options (decision pending)

A feasibility spike (clang / llvm-link / llvm-ar are on the build host) established that there are
**two** hermetic mechanisms, and they diverge enough to be an explicit decision, not an
implementation detail:

**Option A — embed the `.so`, `dlopen`+`dlsym` at runtime (the rustls mechanism).**
Vendor `libopenlibm.so` / `openlibm.dll` (already provisioned for linux-x64/arm64), embed the blob
via a `HermeticMathRuntimeAssets` helper analogous to `HermeticTlsRuntimeAssets`, and at first
transcendental use extract-and-`dlopen` it and `dlsym` the referenced functions into pointers the
math intrinsics call through. Pros: reuses the shipped `.so` provisioning verbatim; matches the
original "download `.so`/`.dll`" direction. Cons: the rustls `dlopen` path is **not** cleanly
reusable — it is entangled with TLS runtime init, a per-module ABI, and the Windows
`LoadLibrary`/`__imp_` plumbing — so this needs a fresh (if simpler, stateless) per-function
resolve-and-cache shim on all three targets; ships the whole `.so` (no dead-strip).

**Option B — link openlibm as LLVM bitcode into the program module (recommended).**
Provision openlibm as a single `libopenlibm.bc` per target; when the math gate fires, parse it and
`LLVMLinkModules2` it into the program module before codegen, so `sin`/`cos`/… become ordinary
internal functions in the one object LLVM emits — no dynamic import, no `dlopen`, no runtime
dependency, and `internalize` + `GlobalDCE` dead-strips to only the referenced functions (small
binaries). Because these are stateless leaf functions this avoids all the rustls runtime machinery.
Cons/unknowns established by the spike: (1) a naive "compile every `src/*.c`" build has **symbol
conflicts** (complex `c*` and long-double `ld80`/`ld128` variants redefine symbols such as `ccos`),
so bitcode must be produced from openlibm's **own curated source selection** (drive it from the
Makefile's object list, or build with `clang -flto`); (2) bitcode is target-specific, so one `.bc`
per target (`--target=x86_64-linux-gnu` / `aarch64-linux-gnu` / `x86_64-windows-gnu`); (3) two new
LLVM interop entry points are needed (`LLVMParseIRInContext` + `LLVMLinkModules2`); (4) **the key
untested risk** is whether the in-house ELF/PE linker handles openlibm's larger object — its rodata
lookup tables and whatever relocation types llc emits — since it currently links a single
compiler-emitted object.

Both options keep Ground Rule 6 (no runtime dependency): the payload is baked into the executable and
never a dynamic link against a system `libm.so`. Option B is recommended (cleaner, dead-stripped, no
runtime shim), contingent on confirming the in-house linker copes with openlibm's object; Option A is
the fallback that reuses the already-provisioned `.so`.

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

## Layer 2 — native-backed transcendentals (vendored openlibm)

Provided by the embedded openlibm. Bit-accurate, full-range, the standard C99 surface:

- Powers / roots: `powF(x, y)`, `cbrt(x)`, `hypot(x, y)`
- Exponential / logarithmic: `exp(x)`, `expm1(x)`, `ln(x)`, `log2(x)`, `log10(x)`, `log1p(x)`
- Trigonometric: `sin(x)`, `cos(x)`, `tan(x)`, `asin(x)`, `acos(x)`, `atan(x)`, `atan2(y, x)`
- Hyperbolic: `sinh(x)`, `cosh(x)`, `tanh(x)`
- Remainder: `fmod(x, y)`

## Accuracy and semantics

- Layer 2 inherits openlibm's accuracy (typically `< 1 ulp`); this is documented per function rather
  than guaranteed bit-identical across targets unless the same openlibm source builds for all three.
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

2. **Provision the openlibm payload per target (build from source).** Add `scripts/download-openlibm.sh`
   (see "Provisioning script" below) and pin `<OpenlibmVersion>` in `Directory.Build.props`. Because
   openlibm ships no prebuilt shared library, the script clones the pinned source tag and builds it
   per target, staging the result under `runtimes/<rid>/libopenlibm.so` (Linux) /
   `runtimes/<rid>/openlibm.dll` (Windows) plus an `openlibm.version` marker — the same layout and
   `.gitignore` allowlist treatment as the vendored rustls payloads. `Ashes.Backend.csproj` copies the
   staged asset to the build output (conditioned on host/RID) and validates its presence and version,
   exactly like the rustls asset targets.

   openlibm is the chosen source: a portable, standalone `libm` (originally from the Julia project),
   permissively licensed, that builds for x86-64/AArch64 on Linux and Windows, is compact, and is
   designed for embedding with minimal libc coupling. musl's `libm` is Linux-centric (no first-class
   Windows build) and so is only a Linux fallback. The main integration risk is satisfying the handful
   of libc symbols openlibm references (e.g. `memcpy`) from the Ashes runtime rather than a real libc.

3. **Embed and link on demand, gated on use.** Register the Layer-2 functions as intrinsics that
   resolve to openlibm symbols, add a `HermeticMathRuntimeAssets` helper (analogous to
   `HermeticTlsRuntimeAssets`) that loads the vendored `.so`/`.dll`, and gate embedding on a new
   `ProgramUsesMathRuntimeAbi(program)` check (analogous to `ProgramUsesTlsRuntimeAbi`) so the payload
   is embedded only when the IR references a transcendental. Hermetic-only programs (Layer 1) and
   math-free programs embed nothing. (The static-archive dead-strip variant is the deferred size
   optimization noted under "Embedding mechanism".)

4. **Tests + examples** per Ground Rule 3: numeric-accuracy tests with tolerance bounds, NaN/inf
   edge cases, a binary-size check confirming hermetic-only programs pull in no openlibm, and an
   example exercising both layers.

## Provisioning script

`scripts/download-openlibm.sh` mirrors `scripts/download-rustls-ffi.sh` in structure (target flags
`--all` / `--linux-x64` / `--linux-arm64` / `--win-x64`, `--version` override, version pinned in
`Directory.Build.props`, writable-dir checks, per-target `openlibm.version` markers). The difference
is that **every** target is built from source, since openlibm publishes no prebuilt binaries:

- **linux-x64** — `make USE_GCC=1` with the host toolchain, staging `libopenlibm.so`. (Validated:
  the 0.8.7 source builds to a ~225 KB `libopenlibm.so.4.0` / ~700 KB `libopenlibm.a` exporting
  `sin`/`cos`/`exp`/`log`/`pow`/`atan2`.)
- **linux-arm64** — cross-built with `aarch64-linux-gnu-gcc` (openlibm's `ARCH=aarch64` +
  `TOOLPREFIX`); the script auto-installs the cross-compiler on apt/pacman systems, as the rustls
  script does for its arm64 build.
- **win-x64** — cross-built with the MinGW-w64 toolchain (`x86_64-w64-mingw32-gcc`), staging
  `openlibm.dll`. Requires `mingw-w64` on the build host (documented prerequisite; the script errors
  with an install hint when it is absent).

## Open questions

- **Which `libm`** — **decided: openlibm.** It builds for x86-64 **and** AArch64 on **both** Linux
  and Windows and links freestanding against the Ashes runtime; musl's Linux-only story rules it out
  as the primary choice. (Dead-stripping to only the referenced functions is deferred; see "Embedding
  mechanism".)
- **Conversion home** — `toFloat` / `*ToInt` in `Ashes.Math` or in the language core (they are
  generally useful beyond math).
- **Domain-error policy** — IEEE `NaN`/`inf` returns (proposed) vs a checked `Result`-returning
  variant; and whether to expose `isNaN` / `isInf` / `isFinite`.
- **Naming for Float vs Int overloads** — suffix style (`absF`, `minF`) as proposed, vs a single
  overloaded name resolved by type (depends on how far operator/overload resolution is taken).
- **Cross-target bit-reproducibility** — whether to guarantee identical results on all targets by
  building one openlibm source everywhere, or document per-target ulp bounds.

## Ground rules touched

- **Rule 1 (spec first):** the module surface lands in `STANDARD_LIBRARY.md`, and the new conversion
  primitives in `LANGUAGE_SPEC.md`, before implementation.
- **Rule 6 (no GC / no runtime deps):** preserved. openlibm is a **compile-time vendored asset
  statically linked and dead-stripped** into the executable, never a dynamic runtime dependency;
  programs that don't use transcendentals link nothing.
