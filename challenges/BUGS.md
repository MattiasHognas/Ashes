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

Current tally: **15 FIXED**, **1 PARTIAL** (#4). (#5, the helper-returns-list report, was removed:
it measures linear in time and memory on the current compiler and no reproducer exists.)

Reproduce any snippet with the prebuilt compiler:
`src/Ashes.Cli/bin/Debug/net10.0/ashes run <file.ash>`.

## Type inference & operators

### 1. (P1) Float operator defaults an unresolved operand to `Int` (annotation-free) — [FIXED]
**[FIXED]** in two parts. Main case (`c4556bd`): `+`'s left-operand resolution switch was missing the
`Float` case (it had `Str`/`BigInt`/`UInt`), so `acc + x * 2.0` froze the accumulator to `Int`; adding
`TFloat -> TFloat` fixes n-body / spectral-norm `avRow`. Second case: a bare `x * y` with *both*
operands unresolved defaulted to `Int` because `Multiply` had no deferred-resolution path. `*` now
mirrors `+` — both-unconstrained operands emit a provisional `MulInt` with the shared operand var kept
monomorphic (`_mulConstrainedVars`), patched to `MulFloat` / `BigIntBinary` / `MulInt` once inference
resolves it (`ResolveDeferredMuls`). A generic dot product (`… acc + x * y`) works at Float, Int, or
BigInt with no signature. (Like `+`, a generic multiply is monomorphic — using the *same* one at two
types in a single program still needs the non-recursive overload-generic path, tracked elsewhere.)

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

### 3. (P1) Fixed-shape pointer-bearing accumulators that are TUPLES or `List(ADT)` are not reclaimed — [FIXED]
**[FIXED]** in two halves. Tuple half (`747adc4`): a deep-copy-safe `TTuple` accumulator takes the
`DeepAdt` copy-out and the fixed watermark, so fasta's `(seed, out)` is bounded. `List(ADT)` half
(`tco_list_of_adt_accumulator`): a synthesized recursive list deep-copier (`SynthesizeListDeepCopier`,
mirroring the ADT copier: nil passes through, each head deep-copies via `EmitDeepCopy`, the tail
recurses via the self-closure at env[0]) clones the list whole, so `GetTcoCopyOutKind` classifies
`List(deep-copyable-element)` as `DeepAdt` and the loop takes the fixed watermark. **n-body's
`List(Body)` loop: 3M iterations at 0.25 MB max RSS** (was 4.27 GB at 1e6).

The earlier attempt at this was reverted for a "List(ADT)+async" miscompile (readme_showcase priced
41.00 instead of 12.50). Root-causing it this time found the real bug — **not async at all**: the
two-pass back-edge copy-out's disjointness argument has a hole for `DeepAdt`. Phase B writes its
down-copy at `[W, W+S)` while reading the Phase-A up-copy at `[W+B, W+B+S)` (B = the loop body's
allocations this iteration) — overlapping whenever `B < S`. Shallow kinds are safe (the fresh
accumulator itself was body-allocated, so `B >= S`), but a deep clone's size includes copier
env/closure overhead beyond the raw value, and a list-tail argument may not be body-allocated at all.
With `B = 0` the copy self-overwrites at zero skew — accidentally benign, which is why optimized
builds "worked"; the test runner compiles **unoptimized** IR where a dead 24-byte `MakeClosure`
skewed the overlap and corrupted the clone. Fix: `DeepAdt` Phase A clones **twice** (clone of the
clone), so the down-copy's destination end `W+S` never reaches its source start `W+B+S` — disjoint
for any `B >= 0` and any number of DeepAdt args. This also closes the same latent hazard for the
already-shipped ADT/tuple DeepAdt copy-outs. Verified on all three targets; the readme_showcase
capability+async case now passes under the test runner's unoptimized pipeline.

Original report: whole-value (`String`/`BigInt`) and fixed-shape **ADT** accumulators reset to a
fixed watermark (constant memory), but two more fixed-shape shapes were excluded and grew:
- **Tuple accumulator** — `fasta` threads `(seed, out)`; the tuple was not in the copy-out path, so
  its growing `String` field grew O(N^2) (N=20000 -> 3.97 GB).
- **`List(record)` accumulator** — `n-body` threads a fixed-size `List(Body)` (5 records) rebuilt each
  step; `List(ADT)` was not copy-outable, so it grew O(N) (N=1e6 -> 4.27 GB) for a constant-state loop.

(A first attempt's "the deep-copy-out interacts badly with the per-task arena under async" diagnosis
was wrong — the miscompile reproduced with no async at all on unoptimized IR; see the overlap analysis
above.)

### 4. (P2) Growing accumulators: quadratic-memory holes — [PARTIAL, re-scoped]
Re-measured after the CO-28..32 arc: the base shapes this entry originally described are **linear
today** (single-fresh-cons string builders ~31 B/elem, two-cons ~23 B/elem, and `reverse-complement`
~96 B/base — all scale 2x memory for 2x N). What actually still blew up were two subtler holes, both
now **FIXED** (CO-34):
- **A loop-invariant heap argument disqualified the fixed watermark.** fasta's `randomFasta` threads
  the `table` CLOSURE unchanged through the loop; `TFun` was not in the fixed-mark whitelist, so the
  loop kept the ADVANCING mark and stranded every iteration's copy of the growing `out` string —
  **6.8 GB at N=20000, 27 GB at N=40000** (despite the entry above saying the fasta case was fixed —
  the tuple leaf was, but the real loop never qualified). A pass-through arg (the param's own
  unchanged Var at every tail self-call, per `LoopInvariantParams`) lives below the loop-entry
  watermark, needs no copy-out at all, and is now exempt from both the copyability scan and the
  fixed-mark qualification. **fasta: constant 3.8 MB at every N**, output identical.
- **A late-typed accumulator silently lost its reset entirely.** The back-edge copy-out decision
  dispatches on argument types — but an accumulator constrained only by a deferred `+` (or by the
  caller) is still an unresolved inference variable when the back-edge lowers (e.g. whenever the
  stable `[] -> acc` leaf lowers before the cons arm), so `GetTcoCopyOutKind(TVar)` = None declined
  the whole block: **1.76 GB for a 60k-iteration string fold**. The back-edge now emits a
  `TcoResetPending` placeholder and `ResolveDeferredTcoResets` splices the real block at the end of
  lowering, once the deferred-operator resolutions have grounded the types. **0.25 MB constant.**

**Update (CO-35):** the back-edge copy cost is AMORTIZED — copy-out + reset skipped while arena
growth <= 2x the last compacted live size (+4 KB slack): total copy work linear in bytes allocated
(the `List(Body)` 3M loop 0.289 s -> 0.054 s at unchanged memory).

**Update (CO-36): the concat O(N^2) TIME is FIXED by reservation-based affine string growth.** The
affine analysis (accumulator consumed at most once along every loop-continuing path, only as the
leftmost leaf of its own tail-call `+` chain) plus the watermark boundary (values >= W are
loop-created, unaliased by the caller) prove unique ownership. Each affine accumulator carries a
reservation (start/end slots): `ConcatStrTip` extends in place while the append fits the reserved
headroom — cursor untouched, so interleaved per-iteration views/scratch cannot break the fast path
— and the fallback reallocates with 2x headroom (doubling, amortized O(1)/byte). Reservation spans
are netted out of the CO-35 compaction trigger, and the compaction's own down-copy of an affine
accumulator re-reserves in place (fixes the every-back-edge-copies cliff once the accumulator
outgrows the watermark chunk's remainder). **Measured: 3M-iteration string fold (6 MB result):
>120 s/OOM -> 0.003 s; 960k closure-call append fold: >120 s -> 0.002 s; fasta N=80000: 67 s ->
0.013 s, N=320000 0.050 s, N=1280000 0.53 s at 41 MB RSS — all linear.** **Still open (the last
remainder):** the ~96 B/base *constant* of list-of-small-`Str` representations (needs in-place
cons-cell reuse — the FLAWS #2 milestone).

### 6. (P3, perf) pidigits is O(N^3) **time** (memory is now constant) — [FIXED]
**[FIXED]** (`bigint_divmod_algorithm_d`): `bignum_divmod` rewritten from bit-by-bit binary long
division (one compare/subtract pass over the divisor per DIVIDEND BIT) to **Knuth Algorithm D in
base 2^32** — the Hacker's Delight `divmnu64` formulation, one quotient DIGIT per outer iteration.
Digits are the 32-bit halves of the 64-bit limbs, so every intermediate (two-digit dividends, digit
products, signed borrows) fits native i64 arithmetic — no i128 division, hence no `__udivti3`
libcall in the freestanding binary. Single-digit divisors take a short-division path (one native
64/32 divide per digit). The caller passes a scratch buffer (normalized divisor + working dividend,
`la+lb+4` words). **pidigits N=1000: 3.46 s -> 0.027 s (~128x); N=500: 0.41 s -> 0.007 s; N=2000 now
0.11 s** — output byte-identical to the old implementation. Edge coverage includes the canonical
add-back trigger, the qhat correction loop, odd digit counts, exact/equal/short/negative cases,
verified on all three targets. Karatsuba multiply remains unimplemented (schoolbook `mul` is now the
next asymptotic ceiling, but no benchmark currently hits it).

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

### 8. (P1) Chained `Ashes.Regex.replace` / `substituteAll` OOMs (~28 GB) — [FIXED]
**[FIXED]** (`regex_large_subject_chain`): the root cause was not regex-specific. The substitute output
buffer (`2*subject + 256`) is allocated from the main arena; a heap chunk was a **fixed** 4 MiB, and
`EmitHeapEnsureSpace` looped `grow`→`recheck` — but `EmitHeapGrow` always allocated exactly one 4 MiB
chunk, so a single allocation larger than 4 MiB never fit and grew one chunk per iteration forever until
the OS refused (the "~28 GB constant"). Any >4 MiB allocation (large string/`Bytes` too) hit this, not
just regex. Chunks are now **variable-sized**: `EmitHeapGrow` sizes an oversized chunk to
`max(4 MiB, request + overhead)`. To keep the reclaim linked-list walk working without a fixed size, each
chunk carries a header (previous chunk's end) and a footer at its usable end (its own base), so the walk
recovers a chunk's base from its end pointer. All chunk sites (main init/grow/reclaim and the parallel
worker setup + worker-chunk free walk in `Lowering`/`LlvmCodegenParallel`) share one format via
`EmitHeapChunkSetup`. Verified: the ~5 MiB chain completes in bounded memory, and 1000 iterations of a
>4 MiB reclaimed temporary stay under a 2 GB cap (no leak/corruption) on all three targets.
Feeding a `substituteAll` result larger than ~1.5 MB into another `substituteAll` allocated ~28 GB and
was killed. The second call OOMed even with a **non-matching** pattern, so the blowup was in *accepting*
the large subject, not in substitution work. Surfaced by `regex-redux` (its canonical substitution
chain). Minimal repro:
```ash
let recursive grow s n = if n == 0 then s else grow(s + s)(n - 1)
let seq = grow("acgtWacgtWacgtWacgtW")(17)              // ~2.6 MB
in let s1 = Ashes.Regex.replace(...W...)(seq)("(a|t)")
   in let s2 = Ashes.Regex.replace(...c...)(s1)("(x|y)")  // ~28.8 GB, OOM
      in Ashes.IO.print(Ashes.Text.byteLength(s2))
```
(The constant ~28 GB across runs suggests a size miscalculation in the substitute buffer sizing.)

### 9. (P3) Missing `Ashes.String.toUpper` / `toLower` — no case normalization primitive. — [FIXED]
**[FIXED]** (`string_ascii_case`): shipped as **ASCII-scoped** case conversion, decided after surveying
the ecosystem (UTF-16/dual-encoding rejected — Ashes strings are UTF-8 like Rust/Go/Swift/text-2.0, and
case mapping is O(N) regardless of encoding, so a fixed-width representation buys nothing). Named
explicitly for the scope, following OCaml `uppercase_ascii` / Rust `to_ascii_uppercase`:
`Ashes.Text.asciiUpper` / `asciiLower` intrinsics (`TextAsciiCase` IR) — a single O(N) byte pass that
flips bit `0x20` on ASCII letters; every byte of a multibyte UTF-8 sequence is `>= 0x80` and passes
through byte-identical, so the transform is UTF-8-safe without decoding. Exposed on `Ashes.Text` only
(no `Ashes.String` wrappers — one public spelling, per review); `toUpper`/`toLower` stay free for a
possible future Unicode-aware (tabled) version. The old blocker (no `Bytes` byte-map primitive) was
sidestepped by doing the map in the intrinsic itself.

### 10. (P3) Missing `Ashes.List.sort` — [FIXED]
**[FIXED]** (`7dcad83`): added `Ashes.List.sortBy : (a -> a -> Bool) -> List(a) -> List(a)`, a stable
O(n log n) comparator merge sort (the language has no ordering typeclass, so the caller supplies the
comparator).

## Diagnostics & formatter

### 11. (P2) Bogus diagnostic locations for flat / stitched top-level programs — [FIXED]
**[FIXED]** (CLI): `ProjectSupport.MapDiagnosticsToOriginal` maps diagnostic spans from combined-source
offsets (what compilation runs on) back to the entry file's own coordinates before rendering. The key
insight making this a contained fix: the **entry region** of the combined source is
**line/column-preserving** with respect to the user's file (imports are blanked keeping newlines,
hoisted declarations are blanked via `BlankSpans`, alias preludes overwrite blank import lines) — so
entry-region spans map exactly by line/column arithmetic, no byte-offset bookkeeping. Hoisted entry
`type`/`capability`/`provide` declarations map **exactly** through a new fragment table
(`CombinedCompilationLayout.EntryTypeDeclFragments`, recorded as `TryShapeFlatModule` extracts them —
the "extend ModuleOffsets with original offsets" direction). Spans inside a stitched (reconstructed)
non-entry module region can't be positioned — they render header-only, attributed to the **owning
file** via `ModuleOffsets`, instead of garbage coordinates in the entry file. Verified: the
`x + "oops"` repro behind a stitched `Ashes.List` import went from `9:2568` (past EOF, blank caret
line) to the exact `6:9` with the right line text and underline; the simple builtin-imports case went
from `1:22` to the exact `5:9`; multi-error files locate every error. Unit tests cover all three
mapping paths (`ProjectSupportTests.MapDiagnosticsToOriginal_*`). Remaining (minor): selector-import
renames can drift columns on the renamed line, and LSP/TestRunner still use their own (header-only or
approximate) paths.

Propagated/secondary type errors were reported at coordinates **past EOF** (e.g. line 71 in a 66-line
file, columns ~2000+ — byte offsets into the internally stitched single line), sometimes blaming an
unrelated later declaration. Surfaced by `spectral-norm` and `n-body`; made multi-error files very
hard to debug. Root cause: compilation runs on `layout.Source` (the stitched **combined** source), so
diagnostic spans are offsets into *it*, but `PrintCompilerDiagnostics` rendered them against the
**original** file text.

### 12. (P2) `fmt` silently strips all non-leading comments — [FIXED]
**[FIXED]** (standalone comment lines): the anchor-based comment reinsertion the LSP's format-document
path already had (`ReinsertStandaloneCommentLines`) was formatter-domain logic living in the LSP —
moved to `Ashes.Formatter.CommentReinserter` (per the boundary rule: LSP consumes compiler logic,
never implements it) and wired into CLI `fmt`, so both format paths now behave identically. Every
standalone `//` comment line is re-anchored to the surrounding significant lines by a
whitespace-insensitive token signature and reinserted after formatting; comments whose anchor
disappears fall back to the previous anchor, then the top of the file — never silently dropped.
Verified: between-declaration, multi-line, and inside-expression comment lines all survive `fmt -w`,
the result is idempotent, and a repo-wide `fmt -w` over `lib`/`tests`/`examples`/`challenges` is a
no-op. **Remaining (minor):** trailing same-line comments (`let x = 1 // note`) still need real trivia
in the AST — the reinserter is line-based.

### 13. (P3) Formatter emits trailing whitespace after `=`, `->`, `in`, `else` — [FIXED]
**[FIXED]**: `Formatter.FinishOutput` strips trailing spaces/tabs from every line before applying the
configured newline — safe because string literals are emitted single-line with escaped `\n`, so a
physical line never ends inside a literal. Landed with the coordinated repo-wide `fmt -w` reformat
(~400 `.ash` files across `lib`, `tests`, `examples`, `challenges`; verified whitespace-only via
`git diff --ignore-space-at-eol` = empty, and idempotent — a second `fmt -w` pass changes nothing).
Formatter/LSP unit-test expectations updated to the trimmed output. Gotcha found en route: `fmt -w`
itself does NOT honor `// fmt-skip:` (only `scripts/verify.sh`'s format check does), so the four
fmt-skip fixtures were restored after the bulk pass — a bulk `fmt -w` over `tests/` must exclude them.
### 14. (P3) `import M.binding` selector renders as `import M as binding` under `fmt` — [FIXED]
**[FIXED]** (verified, no longer reproduces): `ExtractImports` (`Program.cs`) already classifies the
lowercase `.binding` selector as its own regex group (`ImportModulePattern` group 2) and renders it as
`import M.binding` (with an optional ` as alias`), distinct from the `as`-alias group. `import
Ashes.List.map` and `import Ashes.List.filter as keep` both round-trip through `fmt -w` unchanged and
compile/run. (An uppercase `M.Type` selector is absorbed into the module-path group and likewise
renders identically.)

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

### 16. (P3) Inline parameter type annotation `(b: Body)` is unsupported — [FIXED]
**[FIXED]** (`param_inline_annotation`): both forms now parse and type-check — `given (x: Type) -> ...`
(per parenthesized lambda parameter) and the parenthesized annotated sugar parameter
`let f (b: Body) = ...` (desugars to a `given` layer carrying the annotation; parens required exactly
when an annotation is present). `Expr.Lambda` gained `ParamAnnotation`, unified with the parameter's
type variable before the body is lowered — so record dot-access on the parameter and Float operator
selection resolve without annotating the whole binding. The formatter renders both forms canonically
and round-trips them. Gotcha fixed en route: the stitcher's two text-based flat-let header scanners
(`TryScanFlatLetHeader`, `TrySplitLeadingTopLevelBinding`) only accepted bare-ident sugar params, so an
annotated parameter in a file importing a source stdlib module (paren-wrapped flat entry) broke shaping
with a misleading `<std:...>` ASH003 — both now capture `(name: Type)` verbatim. Spec updated
(language.md sections 7 and 7.2).
### 17. (P3) `given xs ->` requires parenthesized params `given (xs) ->` — [FIXED]
**[FIXED]** (`lambda_bare_param`): `ParseLambda` now makes the parentheses optional for a single
parameter — `given x -> body` parses the same as `given (x) -> body`. The parenthesized form (and its
multi-parameter `given (x, y) ->` desugaring) is unchanged, and remains the canonical form the
formatter emits (it re-parenthesizes the bare input).

## Open questions (not yet classified)
- Does `Ashes.IO.write` buffer, or issue a syscall per call? `fasta`/`reverse-complement` streaming
  emits one `write` per 60-char line (~2M+ syscalls at benchmark scale) — worth confirming the write
  path buffers.
