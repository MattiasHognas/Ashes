# Self-hosting phase benchmarks

Compares the .NET compiler (stage 0) with the Ashes-written compiler (stage 1) phase by phase over
the same corpus of `.ash` files. This is a standing benchmark: it grows a row every time the
self-hosted compiler gains a phase, and its results table is refreshed with each self-hosting
milestone so compile-time regressions in either implementation are visible early.

What is measured is the *implementation* of each phase: the C# one executing on .NET against the
Ashes one executing natively. The stage-1 bench binary is itself produced by the .NET compiler
(there is no other Ashes compiler yet), so the numbers also reflect how well that compiler
compiles the self-hosted sources; once stage 1 produces executables, a stage-2-built binary
becomes a third column of the same table.

## Running

```bash
selfhost/bench/run.sh                       # default corpus, 3 iterations
selfhost/bench/run.sh --iterations 5 --corpus tests --corpus lib/Ashes
ASHES_BENCH_OPT=--debug selfhost/bench/run.sh   # time an unoptimized stage-1 binary instead
```

`run.sh` compiles `selfhost/bench/stage1` (the Ashes-written driver) with the .NET compiler at
`-O2`, builds `selfhost/bench/Ashes.SelfhostBench` (the .NET driver) in Release, and runs the
latter. The .NET driver enumerates the corpus, times its own phases in-process, runs the stage-1
binary once per phase (a crash in one phase then only loses that row), and prints one table.
Each phase runs the requested number of iterations and the fastest run is reported. Every phase
returns a checksum (token count, bytes rendered, programs accepted, ...) so its work cannot be
optimized away; the checksums are printed next to the times and should agree between the two
sides where the phases are equivalent.

The default corpus is `tests/`, `lib/Ashes/`, `selfhost/packages/`, and `examples/` (every
`.ash` file, `out/` and `dist/` excluded). A file whose import header the .NET compiler rejects is
left out; a file either side fails to parse simply does not count toward that side's checksum.

## Phases

| Row | Stage 0 (.NET) | Stage 1 (Ashes) | Input |
| --- | --- | --- | --- |
| header | `ProjectSupport.ParseImportHeader` | `parseImportHeader` | whole file |
| lex | `Lexer.Next` until EOF | `tokenize` | body without the import header |
| parse | `Parser.ParseProgram` | `parseProgram` | body without the import header |
| format | `Formatter.Format` | `formatProgram` | programs that parsed cleanly |
| infer | `Lowering.Lower` (inference and lowering in one pass) | `inferProgramFromPackage` over the seeded standard environment (inference only) | import-free programs |
| optimize | `IrOptimizer.Optimize` | not yet: stage 1 has no whole-program lowering | lowered programs |

The `infer` row is deliberately not equivalent yet: stage 0 has no inference-only entry point,
and stage 1 has no whole-program lowering, so the row pairs inference alone against inference
plus lowering. It becomes a like-for-like comparison when the self-hosted core lowering accepts a
whole program; add a `lower` phase to `stage1/Main.ash` and to `Stage1Phases` in the driver at
that point, and move `optimize` to a two-sided row once `optimizeIrProgram` can run on that IR.

### Adding a phase

1. `selfhost/bench/stage1/Main.ash`: add a `count...` walker returning a checksum and a
   `runPhase` arm printing `phase<TAB>count<TAB>milliseconds`.
2. `selfhost/bench/Ashes.SelfhostBench/Program.cs`: add the stage-0 counterpart to `RunStage0`
   and the phase name to `Stage1Phases` (and to the report's phase list).
3. Add the row to the table above and refresh the results below.

## Results

Refreshed on the commit named in the heading; fastest of three runs on a Linux x64 workstation,
stage-1 binary compiled at `-O2`, .NET driver in Release. Times are milliseconds over the whole
corpus (822 files, 475 of them import-free).

### 2026-09-03, milestone 1 closed (resources, console)

Refreshed at the close of the file-system and process milestone: no phase regressed against the
previous entry, the formatter now renders every file the .NET formatter renders (the crashed
files of the earlier entries are gone), and the corpus grew to 879 files with the milestone's
regression tests. The parse row, lexing included, matches .NET; the header scan stays faster.

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 22 | 879 | 14 | 879 | 0.64x | |
| lex | 59 | 530078 | 72 | 530119 | 1.22x | |
| parse | 227 | 864 | 224 | 859 | 0.99x | |
| format | 85 | 3526464 | 98 | 2818700 | 1.15x | |
| infer (stage 1) vs infer+lower (stage 0) | 1130 | 388 | 8 | 37 | 0.01x | |
| optimize IR (stage 0 only) | 519 | 1412 | - | - | - | |

### 2026-08-26, after the per-token copy left the lexer

The lexer deep-copied every token before consing it onto the token list, a workaround from its
first port for a stage-0 ownership bug: a token's kind, returned as a reference-counted ADT and
handed to the curried `lexerToken` builder, was freed by the caller after the call (see the
compiler changelog). With the callee now taking ownership, the copy is gone. The lexer is within
a quarter of the .NET one and the parse row, which includes lexing, is now faster than .NET; the
corpus gained the fix's regression test.

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 16 | 823 | 8 | 823 | 0.50x | |
| lex | 37 | 360798 | 45 | 360811 | 1.22x | |
| parse | 146 | 808 | 133 | 803 | 0.91x | |
| format | 92 | 2337201 | 52 | 1493322 | 0.57x | 22 files crashed and were excluded |
| infer (stage 1) vs infer+lower (stage 0) | 1060 | 377 | 6 | 35 | 0.01x | |
| optimize IR (stage 0 only) | 502 | 1287 | - | - | - | |

### 2026-08-26, after the allocation-free header scan and lexer paths

Three self-hosted frontend changes, each removing per-unit allocations from code the finished
compiler runs on every file or token. The import-header pass split the whole file into lines,
trimmed each header line up to three times, and joined everything back to blank a few import
lines; it now walks header lines over the `Bytes` view, slices only the header lines, and takes
the body with one `subText`. Keyword recognition compared each identifier against 24 keywords by
allocating the keyword's bytes and hashing both sides per candidate; it now matches the token's
already-sliced text against the keyword literals. Operator scanning allocated a `subText` per
candidate spelling (`|?>`, `|!>`, `->`, ...); it now dispatches on the raw bytes. Header is now
faster than the .NET pass, parse (which includes lexing) is close to parity, and lex is the one
row left above 1.5x. `tests/regress_readline_loop_depth.ash` no longer crashes any phase because
the header pass no longer builds the per-line list that triggered the stage-0 lifetime bug; that
bug stays open on the checklist.

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 17 | 822 | 9 | 822 | 0.53x | |
| lex | 38 | 360166 | 74 | 360178 | 1.95x | |
| parse | 147 | 807 | 175 | 802 | 1.19x | |
| format | 99 | 2334590 | 52 | 1491022 | 0.53x | 22 files crashed and were excluded |
| infer (stage 1) vs infer+lower (stage 0) | 1192 | 376 | 5 | 35 | 0.00x | |
| optimize IR (stage 0 only) | 452 | 1261 | - | - | - | |

### 2026-08-26, after the closure-environment optimizer passes

No self-hosted source changed for this row; the .NET compiler gained two whole-program passes
(captured-closure devirtualization and currying-stage inlining, see the compiler changelog). A
stitched module's functions call each other through alias bindings captured in closure
environments, so the packages were almost entirely indirect calls, each saturated curried call
allocating a heap environment per stage. Lex drops from 7.6x to 3.1x and parse from 2.9x to 1.4x.
The optimize row grew because the passes append scalarized callee variants (more functions after
optimization) and cost about 0.85 s on the 31 s `-O2` build of the stage-1 binary itself.

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 16 | 822 | 30 | 821 | 1.88x | 1 file crashed and was excluded |
| lex | 38 | 360789 | 117 | 360757 | 3.08x | 1 file crashed and was excluded |
| parse | 143 | 807 | 203 | 801 | 1.42x | 1 file crashed and was excluded |
| format | 94 | 2346813 | 51 | 1457188 | 0.54x | 24 files crashed and were excluded |
| infer (stage 1) vs infer+lower (stage 0) | 1205 | 376 | 5 | 35 | 0.00x | 1 file crashed and was excluded |
| optimize IR (stage 0 only) | 472 | 1261 | - | - | - | |

### 2026-08-26, after the parser-state change

The parse row was one mechanism: the parser state tuple is rebuilt on every token, it stays an
arena shell because `List(Token)` is not admitted to RC placement, and stage 0 deep-copies every
`Str` element of an escaping arena tuple — the whole source per token. Carrying the source as its
`Bytes` view (which that path leaves alone) takes `parseProgram` on `TypeInference.ash` from
382 ms to 29 ms and the corpus row from 21x to 2.9x. Lex is now the largest real ratio.

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 16 | 822 | 32 | 821 | 2.00x | 1 file crashed and was excluded |
| lex | 39 | 360789 | 298 | 360757 | 7.64x | 1 file crashed and was excluded |
| parse | 152 | 807 | 441 | 801 | 2.90x | 1 file crashed and was excluded |
| format | 102 | 2346813 | 56 | 1493607 | 0.55x | 23 files crashed and were excluded |
| infer (stage 1) vs infer+lower (stage 0) | 1198 | 376 | 7 | 35 | 0.01x | 1 file crashed and was excluded |
| optimize IR (stage 0 only) | 279 | 1176 | - | - | - | |

### 2026-08-26, after the `Ashes.Text.split` rewrite

The first run's header row was almost entirely the stdlib `split`: its nested recursive closure
captured the whole text and copied it once per piece (see the compiler changelog). With the walk
rewritten to take the buffer as a parameter, the header phase over the corpus drops from 1142 ms
to 31 ms and the 190 KB `TypeInference.ash` from 195 ms to 2 ms. The parser is now the hotspot.

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 17 | 822 | 31 | 821 | 1.82x | 1 file crashed and was excluded |
| lex | 39 | 360776 | 230 | 360744 | 5.90x | 1 file crashed and was excluded |
| parse | 149 | 807 | 2781 | 801 | 18.66x | 1 file crashed and was excluded |
| format | 89 | 2346986 | 55 | 1493607 | 0.62x | 23 files crashed and were excluded |
| infer (stage 1) vs infer+lower (stage 0) | 1086 | 376 | 7 | 35 | 0.01x | 1 file crashed and was excluded |
| optimize IR (stage 0 only) | 285 | 1176 | - | - | - | |

### 2026-08-26 (first run, main after #616)

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 16 | 822 | 1142 | 821 | 71.38x | 1 file crashed and was excluded |
| lex | 38 | 360698 | 249 | 360666 | 6.55x | 1 file crashed and was excluded |
| parse | 145 | 807 | 2759 | 801 | 19.03x | 1 file crashed and was excluded |
| format | 105 | 2347041 | 54 | 1493607 | 0.51x | 23 files crashed and were excluded |
| infer (stage 1) vs infer+lower (stage 0) | 1285 | 376 | 10 | 35 | 0.01x | 1 file crashed and was excluded |
| optimize IR (stage 0 only) | 272 | 1176 | - | - | - | |

What the first run says, beyond the raw ratios:

- The self-hosted import-header pass and parser are the hotspots. Per file, the header phase
  spends 195 ms on the 190 KB `TypeInference.ash`, 180 ms on `CoreLowering.ash`, and 135 ms on
  `Parser.ash` (`Ashes.Text.split` and `join` are byte-based and tree-shaped, so the cost is in
  the per-line walk or the copies around it); the parser's 19x is spread over the corpus.
- The lexer's 6.5x is the cleanest comparison: identical work, matching token counts.
- The format row is not comparable yet: the 23 files the self-hosted formatter crashes on are the
  largest ones (the stdlib collections, the semantics package sources), so stage 1 renders 1.5 MB
  where stage 0 renders 2.3 MB. The crash is tracked on the self-hosting checklist (formatter
  corpus item).
- The infer row is a coverage number, not a speed number: the seeded standard environment has no
  builtin value bindings yet (`Ashes.IO.print` and friends), so stage 1 accepts 35 of the 475
  import-free programs where stage 0 lowers 376. It becomes meaningful once the standard-library
  environment is populated through module stitching (checklist: semantic foundations).
- The single file every stage-1 phase crashes on is `tests/regress_readline_loop_depth.ash`, a
  15 KB `// stdin:` directive line that exposes a stage-0 lifetime bug in a tail-recursive loop
  moving a large string from the list it consumes into its accumulator (checklist: ownership).

### 2026-09-04, milestone 2 batches 2 and 3 (RC runtime, copy-out, entry normalization)

Two entries in one: after batch 2 (PRs #829-#833: the backend RC runtime, runtime-managed
strings and the owned-scope copy-out, the explain reports, the tagless layout, the structural
droppers) and after batch 3 (PRs #838-#842: the backend copy and to-space family, match-arm
brackets and copy-out, entry argument normalization, the let/TCO ownership rules, the
call-window copy-out and argument retain). No phase regressed beyond the corpus growing with
each batch's regression programs; the stage-0 optimize row rose with the new fold-release rule
of #835 and is the one to watch.

Batch 2:

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 21 | 886 | 15 | 886 | 0.71x | |
| lex | 60 | 562613 | 82 | 562661 | 1.37x | |
| parse | 218 | 871 | 239 | 866 | 1.10x | |
| format | 92 | 3698326 | 104 | 2975634 | 1.13x | |
| infer (stage 1) vs infer+lower (stage 0) | 1107 | 388 | 8 | 37 | 0.01x | |
| optimize IR (stage 0 only) | 510 | 1412 | - | - | - | |

Batch 3:

| Phase | Stage 0 (.NET) ms | Stage 0 count | Stage 1 (Ashes) ms | Stage 1 count | Stage 1 / Stage 0 | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| header | 21 | 895 | 15 | 895 | 0.71x | |
| lex | 61 | 579794 | 85 | 579851 | 1.39x | |
| parse | 220 | 880 | 245 | 875 | 1.11x | |
| format | 113 | 3783639 | 111 | 3053880 | 0.98x | |
| infer (stage 1) vs infer+lower (stage 0) | 1084 | 391 | 8 | 37 | 0.01x | |
| optimize IR (stage 0 only) | 626 | 1428 | - | - | - | |
