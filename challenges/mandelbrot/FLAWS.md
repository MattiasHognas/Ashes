# mandelbrot — flaws found (all fixed)

The escape-time inner loop is the whole benchmark. It originally needed two `Float` type-inference
workarounds and could not emit its real binary output. All three are now fixed, and `mandelbrot.ash`
is written in its natural form.

## Flaw 1 — `Float * Float` of annotated parameters defaulted to `Int` (FIXED)

`*` on two operands whose `Float` type came from a **parameter annotation** resolved as `Int * Int`
and then clashed, because the overload was picked before the annotation was applied:

```ash
let mul : Float -> Float -> Float = given (a) -> given (b) -> a * b   // used to ASH002 Float vs Int
```

Fixed by seeding a definition's parameter types from its annotation before the body is lowered (both
`let` and `let recursive`). `zr * zr` now resolves as `Float` with no `1.0 *` lead. Regression test:
`tests/float_annotated_param_operators.ash`.

## Flaw 2 — recursive numeric accumulator took its type from the first operand (FIXED)

A recursion argument like `escape(cr + zr2 - zi2)…` mis-inferred when it led with a still-unresolved
parameter. Same seeding fix: the annotated parameter (`cr`) is `Float` before the body is lowered, so
`cr + zr2 - zi2` resolves without reordering to `zr2 - zi2 + cr`.

## Flaw 3 — no raw-bytes stdout write (FIXED, feature)

`Ashes.IO.write` takes a UTF-8 `Str`, so the binary `P4` PBM was not expressible and the benchmark
reported a pixel count. Added `Ashes.IO.writeBytes : Bytes -> Unit`, which writes a raw `Bytes` buffer
to stdout verbatim. `mandelbrot.ash` now packs one bit per pixel (8/byte, MSB first, rows padded to a
byte) and emits the real image. Regression test: `tests/io_write_bytes.ash`.

## Residual (memory-model, not mandelbrot-specific)

The bit-packing is a single **flat** cons loop on purpose. A helper that *returned* the growing byte
list would be deep-copied out of its arena scope on every call, making the build O(rows²) — the same
growing-accumulator limitation tracked as FLAWS #2 (ownership / in-place reuse). Flat threading keeps
mandelbrot's build proportional to the output image.
