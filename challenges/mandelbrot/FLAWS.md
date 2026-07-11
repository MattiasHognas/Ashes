# mandelbrot — flaws found

The escape-time inner loop is the whole benchmark, and it worked once two `Float` type-inference
weaknesses were worked around. Both also affect the other float challenges (`n-body`,
`spectral-norm`).

## Flaw 1 — `Float * Float` of annotated parameters defaults to `Int`

`*` on two operands whose `Float` type comes from a **parameter annotation** resolves as `Int * Int`
and then clashes. It reproduces with no recursion:

```ash
let mul : Float -> Float -> Float = given (a) -> given (b) -> a * b   // ASH002 Type mismatch: Float vs Int
```

`+`/`-` on the same annotated params are fine — only `*` mis-resolves. The overload is picked before
the parameter's annotated type is applied. It only bites when the operands are *bare params*: when at
least one operand is a known-`Float` expression — a `Float` literal or a function result — `*`
resolves correctly (e.g. `Priced.cost(d) * math.toFloat(q)` in the README hero works).

**Workaround:** lead the product with a `Float` literal so `*` sees a `Float` operand first:
`1.0 * zr * zr` instead of `zr * zr`.

## Flaw 2 — recursive numeric accumulator takes its type from the first operand

A tail-recursive accumulator like `escape(cr + zr2 - zi2) …` mis-infers when the recursion argument
*leads with a parameter* (`cr`, still-unresolved) rather than a known-`Float` sub-expression: the
`+` defaults off the leading operand. Writing the same value with the resolved sub-expression first —
`zr2 - zi2 + cr` — resolves it. (Same root cause seen for the `Float` accumulator in the README order
demo, and for `Int`-vs-`Float` in `pidigits`.)

## Net

With both workarounds (`1.0 *` before each square, and squares-first in the recursion arguments) the
pure-`Float` escape loop compiles and runs; it is deterministic and constant-memory. The output is a
pixel **count** rather than the reference binary PBM: `Ashes.IO.write` takes a UTF-8 `Str` and there
is no raw-bytes stdout write, so the exact `P4` image is not expressible today — the compute (the
point of the benchmark) is identical. Both flaws are pure type-inference issues, not missing
features; fixing Flaw 1 in particular would let `n-body` and `spectral-norm` be written naturally.
