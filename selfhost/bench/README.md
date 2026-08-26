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
