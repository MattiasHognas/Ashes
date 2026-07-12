# Bugs & gaps surfaced by the challenge benchmarks

Running the full Benchmarks Game suite in its **natural spelling** turned up the items below. Each is
a real defect or gap hit while writing a benchmark, with a minimal reproducer where one exists. Each
item carries a status; **FIXED** items keep their entry (with the commit) so the analysis is not lost,
and notable optimizations among them are also recorded in
[`docs/md/internals/changelog.md`](../docs/md/internals/changelog.md).

Severity: **P1** blocks a benchmark or is a correctness/inference bug on valid code; **P2** hurts real
use (perf cliff, bad diagnostics, silent data loss); **P3** stdlib gap / minor / cosmetic.

Status legend: **[FIXED]** shipped (commit noted), **[PARTIAL]** main case shipped, a harder sub-case
remains, **[OPEN]** not yet addressed.

Reproduce any snippet with the prebuilt compiler:
`src/Ashes.Cli/bin/Debug/net10.0/ashes run <file.ash>`.

## Type inference & operators

### 1. (P1) Float operator defaults an unresolved operand to `Int` (annotation-free) — [PARTIAL]
**[PARTIAL]** main case FIXED (`c4556bd`): `+`'s left-operand resolution switch was missing the `Float`
case (it had `Str`/`BigInt`/`UInt`), so `acc + x * 2.0` froze the accumulator to `Int`; adding
`TFloat -> TFloat` fixes n-body / spectral-norm `avRow`. **Still open:** a bare `x * y` with *both*
operands unresolved defaults to `Int` (`Multiply` has no deferred-resolution path like `Add`'s
`ResolveDeferredAdds`) — a pure polymorphic dot product still needs a signature.

A numeric operator whose result type is not yet grounded defaults an unbound operand to `Int`, so a
recursive Float accumulator on the **left** of `+`/`*` with a compound Float right operand is rejected.
The earlier fix seeds parameter types from a **declaration annotation**; the annotation-free /
recursion-accumulator case is still open. Surfaced by `spectral-norm` and `n-body`.
```ash
let recursive foo xs px =
    match xs with
        | [] -> px
        | h :: rest -> foo(rest)(px + h * 2.0)   // ASH002 '+' got Int and Float
```
`h * 2.0 + px` (Float-producing operand first) compiles; `px + h` (bare) compiles. Also bites a bare
`x * y` on two unconstrained operands (no numeric typeclass, so it picks `Int`). *Workaround:* lead
with the grounded operand, or give the function a full type signature.

### 2. (P2) Unary minus on a Float variable/expression lowers to `Int 0 - x` — [FIXED]
**[FIXED]** (`25428b7`): a literal `0` is the identity of every numeric type, so `LowerSubtract` now
re-lowers a literal-`0` left operand as the right operand's concrete numeric zero (`Float`/`BigInt`/
`UInt`). `-total` on a Float variable compiles; Int negation is unchanged.

`-x` desugars to `0 - x` with a synthesized **Int** zero, so negating a Float variable fails. Negated
Float *literals* were fixed (they fold into the literal); variables/expressions were not. Surfaced by
`n-body`.
```ash
let total = 5.0
Ashes.IO.print(Ashes.Text.formatFloat(-total)(2))   // ASH002 '-' got Int and Float
```
*Workaround:* `0.0 - total`. *Fix direction:* a type-directed negation (an `Expr.Negate` node, or a
polymorphic numeric zero) instead of the `0 - x` desugar.

## Memory model (extends the fixed-watermark / deep-copy-out work)

### 3. (P1) Fixed-shape pointer-bearing accumulators that are TUPLES or `List(ADT)` are not reclaimed — [PARTIAL]
**[PARTIAL]** tuple half FIXED (`747adc4`): a deep-copy-safe `TTuple` accumulator now takes the
`DeepAdt` copy-out and the fixed watermark, so fasta's `(seed, out)` is bounded. **Still open:**
`List(fixed-shape-ADT)` (n-body's `List(Body)`) needs a synthesized recursive list-of-ADT deep-copier
(`EmitDeepCopy`'s `TList` branch handles only copy/String/List-of-copy heads).

The recent work made whole-value (`String`/`BigInt`) and fixed-shape **ADT** accumulators reset to a
fixed watermark (constant memory). Two more fixed-shape shapes are still excluded and grow:
- **Tuple accumulator** — `fasta` threads `(seed, out)`; the tuple is not in the copy-out path, so its
  growing `String` field grows O(N^2) (N=20000 -> 3.97 GB).
- **`List(record)` accumulator** — `n-body` threads a fixed-size `List(Body)` (5 records) rebuilt each
  step; `List(ADT)` is not copy-outable, so it grows O(N) (N=1e6 -> 4.27 GB) for a constant-state loop.

Both are fixed-shape and deep-copyable, exactly like the `State(perm, count)` case already handled —
extend `CanDeepCopyOutAdt` / the copy-out to **tuples** and to **lists whose element is a fixed-shape
(non-recursive) ADT/tuple**, then admit them to the fixed-watermark qualification. *Workaround:*
stream output per line (fasta) instead of accumulating.

### 4. (P2) Growing **cons-list** accumulator is O(N^2) memory
A list that grows by more than one fresh cons cell per iteration (or a whole-program list built via a
returned accumulator) cannot use the fixed watermark because its shared tail must stay below an
advancing mark. `reverse-complement`'s list-of-single-char-`Str` (~117 bytes/base) and any
list-building fold hit this. Needs ownership / in-place reuse (the FLAWS #2 milestone), not a point fix.

### 5. (P2) A helper that **returns** a growing list deep-copies it out of its arena scope per call
Nesting a list-builder helper inside a loop (`outer` threads a list through `inner` that returns it)
makes each call deep-copy the whole growing list -> O(N^2). Found while writing `mandelbrot` (worked
around by keeping the bit-packer a single flat loop). Same ownership milestone as #4.

### 6. (P3, perf) pidigits is O(N^3) **time** (memory is now constant)
Binary long-division dominates; needs Knuth Algorithm D / Karatsuba multiply. Not a memory issue.

## Standard library

### 7. (P1, perf) `Ashes.String.substring` is superlinear (character-indexed) — [FIXED]
**[FIXED]** (`984a13c`): rewrote `substring` from `take(drop(...))` (O(start + count^2), per-char
concatenation) to a single codepoint->byte offset walk + one `Bytes.subText` — O(start + count), no
concatenation, still UTF-8 correct. The 8000-char sliding window dropped ~63 s -> 0.03 s (~2000x). It
is still O(position) per call (strings are not byte-indexable); the doc steers repeated indexed slicing
to `Bytes.subText`.

`substring` walks from byte 0, so a sliding k-mer window is worse than O(N^2) (8000 bases -> 63 s).
Surfaced by `k-nucleotide`. *Workaround:* `Ashes.Bytes.fromText` once + `Ashes.Bytes.subText` (byte-
indexed, O(k)). *Fix direction:* make `substring` byte-backed, or document it as O(N) and steer k-mer
work to `Bytes`.

### 8. (P1) Chained `Ashes.Regex.replace` / `substituteAll` OOMs (~28 GB)
Feeding a `substituteAll` result larger than ~1.5 MB into another `substituteAll` allocates ~28 GB and
is killed. The second call OOMs even with a **non-matching** pattern, so the blowup is in *accepting*
the large subject, not in substitution work. Surfaced by `regex-redux` (its canonical substitution
chain); no user workaround. Minimal repro:
```ash
let recursive grow s n = if n == 0 then s else grow(s + s)(n - 1)
let seq = grow("acgtWacgtWacgtWacgtW")(17)              // ~2.6 MB
in let s1 = Ashes.Regex.replace(...W...)(seq)("(a|t)")
   in let s2 = Ashes.Regex.replace(...c...)(s1)("(x|y)")  // ~28.8 GB, OOM
      in Ashes.IO.print(Ashes.Text.byteLength(s2))
```
(The constant ~28 GB across runs suggests a size miscalculation in the substitute buffer sizing.)

### 9. (P3) Missing `Ashes.String.toUpper` / `toLower` — no case normalization primitive. — [OPEN]
An efficient version needs a `Bytes` byte-map primitive (only `subText` exists for `Bytes -> Str`) plus
an ASCII-vs-Unicode design decision, so deferred.

### 10. (P3) Missing `Ashes.List.sort` — [FIXED]
**[FIXED]** (`7dcad83`): added `Ashes.List.sortBy : (a -> a -> Bool) -> List(a) -> List(a)`, a stable
O(n log n) comparator merge sort (the language has no ordering typeclass, so the caller supplies the
comparator).

## Diagnostics & formatter

### 11. (P2) Bogus diagnostic locations for flat / stitched top-level programs — [OPEN, diagnosed]
Propagated/secondary type errors are reported at coordinates **past EOF** (e.g. line 71 in a 66-line
file, columns ~2000+ that look like byte offsets into the internally stitched single line), and
sometimes blame an unrelated later declaration. Primary errors locate correctly. Surfaced by
`spectral-norm` and `n-body`; makes multi-error files very hard to debug. **Diagnosis:** compilation
runs on `layout.Source` (the stitched **combined** source), so diagnostic spans are offsets into *it*,
but `PrintCompilerDiagnostics` renders them against the **original** file text (`Program.cs` passes
`source`, not `prepared.Layout.Source`). For a flat top-level file the combined source is not the
original text — `TryShapeFlatModule` **reconstructs** the module from extracted binding-value fragments
(imports consumed, type/`external`/`provide` decls hoisted, body parenthesized), so offsets shift by
the stripped prefix and the two texts no longer line up. **Fix direction:** carry a combined→original
source map (extend `ModuleOffsets` with each region's original offset, tracked as fragments are
extracted) and map spans back before rendering; the non-flat path already blanks hoisted decls to keep
line numbers, but the flat reconstruction loses them entirely. Needs care — real source-map
infrastructure, not a point fix.

### 12. (P2) `fmt` silently strips all non-leading comments
`fmt -w` deletes every `//` comment that is not in the leading header block (and reshapes one-line
curried `given` chains). Output still compiles, but inline documentation is lost with no warning.

### 13. (P3) Formatter emits trailing whitespace after `=`, `->`, `in`, `else` (idempotent; cosmetic). — [OPEN]
The formatter fix is small (a per-line `TrimEnd` of the final output — safe because string literals are
emitted single-line with escaped `\n`, so a line never ends inside a literal). But it changes the
canonical output of **~391 committed `.ash` files** (all of `lib/Ashes`, `tests`, `examples` currently
carry the trailing whitespace), so it must land with a coordinated repo-wide `fmt -w` reformat in the
same change — deferred as a deliberate bulk commit rather than bundled here.
### 14. (P3) `import M.binding` selector renders as `import M as binding` under `fmt` (rendering-only).

## Records & syntax

### 15. (P2) Record dot-access fails on a parameter receiver — [FIXED]
**[FIXED]** (`4292936`): when the receiver is a value binding whose type is still an unbound variable,
`b.x` now resolves structurally — if exactly one record type in scope declares field `x`, the receiver
is unified with a fresh instance of it. Ambiguous/unknown falls through to a clear, field-access-
oriented diagnostic (annotate the parameter) instead of the misleading module-export error.

`let f b = b.x` (b a record param) is parsed as module-member access -> "Module 'b' does not export
'x'". Re-letting the param does not help; a direct `let r = Rec(...) in r.x`, a positional
`match b with | Rec(x, ...) ->`, or a full type signature `let f : Rec -> T = given (b) -> b.x` all
work. Surfaced by `n-body`.

### 16. (P3) Inline parameter type annotation `(b: Body)` is unsupported (`ASH003`); annotate the whole
binding instead.
### 17. (P3) `given xs ->` requires parenthesized params `given (xs) ->`; the error (`ASH003 Expected
LParen`) is opaque.

## Open questions (not yet classified)
- Does `Ashes.IO.write` buffer, or issue a syscall per call? `fasta`/`reverse-complement` streaming
  emits one `write` per 60-char line (~2M+ syscalls at benchmark scale) — worth confirming the write
  path buffers.
