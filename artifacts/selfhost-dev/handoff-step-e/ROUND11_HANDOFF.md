# Step E handoff after round 11 (2026-11-09)

This document is a prompt for an agent that has no background in this work. Read all of it before
starting.

## The project in three paragraphs

Ashes is a compiler for a small, pure, strictly evaluated functional language in the ML family. It
compiles `.ash` source to standalone native executables through LLVM. There is no garbage collector
and no runtime. Read `CLAUDE.md` (identical to `AGENTS.md`) at the repository root first: it lists the
build, test and format commands every change must pass, and the project's coding rules.

There are two compilers. **Stage 0** is the C# compiler under `src/` (lowering lives in
`src/Ashes.Semantics/Lowering.*.cs`, lifetime placement in `PerceusLifetimePlacement.cs`). **Stage 1**
is the self-hosted compiler written in Ashes under `selfhost/packages/` (lowering lives in
`selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash` and `TcoRuntimeManagedParams.ash`;
the IR optimizer in `IrOptimizer.ash`). Stage 0 compiles stage 1. The goal of the whole arc is a
bootstrap fixpoint: stage 1 compiles itself into stage 2, stage 2 into stage 3, stage 2 == stage 3.

`docs/md/future/SELF_HOSTING.md`, section "Work order", is the plan: a lettered list A to T, worked top
to bottom. A to D are done. **We are in step E: make stage 1 leak-free.** Then F to J (the fixpoint).
Milestone 3 (trait dictionaries, let-generalization of constrained bindings) is step P, not E.

## What step E means and how it is measured

Memory model: reference-counted Perceus for escaping heap values, arenas (regions) for values the
compiler proves scoped. Ownership is inferred, never written by the user. A leak is a reference-counted
cell still live at process exit, or resident memory that grows without bound in a loop that should
plateau.

**The metric (the definition E is judged by):** stage 1 compiling one more function adds zero
reference-counted cells live at exit. Concretely, the full tally compiles two tiny inputs with a
leak-counting stage 1, `loops10` and `loops11` (one added function), and reports per added function:

- **NET**: live cells at exit, loops11 minus loops10.
- **GROWTH**: cells allocated and not freed, by class, summed over growing classes.

A **class** is named `<cell size>_<first word>`, e.g. `32_28` is a 32-byte cell whose first word is 28
(an Int list cell with head 28), `32_ptr` a 32-byte cell whose first word is a pointer.

The target is NET 0 and GROWTH 0. Mattias's standing order: the round keeps going until zero.

### Numbers over time

| Point | NET cells/fn | GROWTH cells/fn |
|---|---|---|
| Start of 11-09 | +44 | +56 |
| PR #1152 (merged as bad7f5e9) | +27 | +39 |
| Tip of PR #1153 (590baff3 era, last tally on s1-k25b) | +20 | +32 |

Remaining growing classes at the tip: `32_ptr` +5, `32_3` +2, `32_28` +2, `32_29` +2, then +1
singletons. Every remaining item is listed below.

## Hard rules (read before running anything)

These come from earlier incidents, including a machine-wide OOM on 11-09 that killed the Claude app.

- **Work on the local machine** (mattias-cachyos), never in the cloud.
- **Memory budget.** Every dotnet build/test/format/run, every compile, every gdb session and every
  compiled program runs inside a capped scope:
  `systemd-run --user --scope -q -p MemoryMax=<cap> -p MemorySwapMax=0 <command>`.
  Caps: 8G for dotnet build, format and e2e; 12G for a C# test suite; 4G for any compiled program;
  16 to 20G for the stage-1 semantics suite; **20G for a stage-1 build** (peaks are 15.8 to 17 GB).
  Export `UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0` first.
- **At most two heavy jobs at once** (stage-1 builds, semantics suite, full tallies). Count them
  yourself: `pgrep -fa 'quickstage1.sh|debugstage1.sh|fulltally.sh|compile --project selfhost'`, and
  require `free -g` to show at least 25 GB available before starting one. `~/.claude/jobs/heavy2.sh`
  hands out three slots and does not enforce the two-job rule.
- A capped run that was killed or produced no output is **never rerun bare**. Stop only your own job,
  by exact PID or scope. Never restart the `ashes-memwatch` unit.
- Deletes and kills may be blocked by permissions: write a script into `.claude/worktree-archive/` for
  Mattias to run instead.
- **Mirror rule:** a stage-0 lowering change ships with its stage-1 counterpart in the same PR, unless
  stage 1 demonstrably handles the shape (its leak counter flat at two round counts on the reproducer).
- **Fix the compiler, never rewrite source around a leak.** Reproducers are test input, so they may be
  rewritten; the shipped source may not.
- Each fix: its own `GRC_NO_<NAME>` environment switch that turns the rule off; an e2e test under
  `tests/`; a leak-census template under `src/Ashes.Fuzzing/Combinations/Leak*CombinationTemplates.cs`
  (registered in `LeakCombinationTemplates.cs`) that leaks with the switch set; where memory is the
  point, a plateau test in `src/Ashes.Tests/LinuxBackendCoverageTests.cs`; a paragraph in
  `docs/md/internals/architecture.md`; and a **poisoned negative**: a variant where the release must not
  happen, run under poison, to show the rule fails closed.
- Commits and PRs: no attribution lines of any kind (no Co-Authored-By, Claude-Session, "Generated
  with", "Requested by"). The commit-msg hook rejects some of them. Squash merge with a hand-written
  message. Never delete `main` or `release/*`.
- After editing any `.ash` file: `dotnet run --project src/Ashes.Cli -- fmt <path> -w`.
- After editing `lib/Ashes/`, rebuild the CLI before testing.

## Tools

Paths below are relative to the integration worktree
`/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free` unless they name another
worktree. `artifacts/` is gitignored: those tools exist only on this machine.

| Tool | What it does |
|---|---|
| `scripts/selfhost-dev/quickstage1.sh <tag> [ENV=val...]` | Rebuilds the CLI, builds stage 1 with the given env (e.g. `ASHES_RC_LEAKCHECK=1`, a `GRC_NO_*` switch), copies it to `artifacts/selfhost-dev/s1-<tag>`, runs the smoke programs t0/t4 and the stage-1 miscompile guards. Run it under heavy2.sh plus a 20G cap. |
| `scripts/selfhost-dev/debugstage1.sh` | The same with debug info, for gdb watchpoints with real frames. |
| `post-prefix-capture/work/q40/fulltally.sh <s1-binary> <tag>` | **The census.** Full tally of loops10 vs loops11 with private gdb dumps; writes `tally-<tag>-diff.txt` with NET and GROWTH. Heavy: heavy2.sh plus 16G. Recipe notes at the bottom of `work/q40/NOTES.md` there. Do not use `tinycensus.sh` / `rccensus2` for numbers: they print only 25 rows. |
| `post-prefix-capture/work/q41/dlrun.sh <tag>` | The **dl probe**: stage 1's lowering of one file, built by stage 0 with the leak counter, then full-tallied. It reproduces stage-1 classes in about 4 minutes without a full stage-1 build, so stage-0 switches can be tested quickly. |
| `post-prefix-capture/work/cp/do*` | W2's real-code probe: stage 1's lowering plus `optimizeIrProgram`, built by stage 0 (3.5 min). |
| `ASHES_RC_LEAKCHECK=1` | Set when building stage 1 or a program: prints `ashes-rc-live N` at exit. A flat N across round counts means no leak. |
| `ASHES_RC_POISON=1` | Set when **compiling** (the backend reads it): freed cells are poisoned, so a use-after-free crashes. Setting it only at run time does nothing. Compile twice, plain and poisoned. |
| `dotnet run --project src/Ashes.Fuzzing -- leak-census --seeds 3 [--poison] [--stage1 <s1>]` | Runs every leak template. |
| `dotnet run --project src/Ashes.Fuzzing.Tests -- --progress off` | Fuzzing tests, including every template forced as a valid program through the IR verifier. Green (107/107) at the tip. |
| `scripts/selfhost-dev/suites.sh` | The six self-hosted suites (semantics needs a 16 to 20G cap). |
| `scripts/selfhost-dev/regenfixtures.sh` | Regenerates the stage-0 lowered-IR parity fixtures and verifies (207/207 at the tip). Any changed fixture must be explained in the commit. |
| `scripts/selfhost-dev/parityonly.sh` | Whole-program IR parity between stages: 30 of 95 differ at the tip, the same set as the base. |
| `artifacts/selfhost-dev/handoff-step-e/tools/integpar.sh [--final]` | The full combined pass (both lanes, everything). Use for the final pass before a PR. |
| `--explain <ownership\|rc\|reuse\|memory>`, `--emit-ir <lowered\|final>` | CLI flags that report ownership decisions and IR to stderr. |
| gdb watchpoint on a leaked cell | The way to name an owner. In a debug stage-1 build: find a leaked cell in the dump, watch its count word, account each retain/release to an instruction. At the last event, search all rw mappings for the cell's address (`post-prefix-capture/work/q41/findptr.c`). |

**Method.** Name the instruction that takes the leaked reference (watchpoint) before writing code.
Narrow by toggling `GRC_NO_*` switches over a reproducer, not by reading assembly. A reproducer of a
stage-1 shape must have no effect handler (a handler turns general RC off). Put an identical earlier
copy of a function first when cross-function state matters.

## Where the code is

- Branch `selfhost/step-e-round11-cont` in the worktree `stage1-leak-free`. Draft PR #1153 holds all
  picks since PR #1152 was merged as `bad7f5e9`. Mattias merges it.
- The round log, with every pick, decision and number, is
  `artifacts/selfhost-dev/handoff-step-e/work/ROUND11_LIST.md` (about 540 lines, newest at the end).
- Worker worktrees, each with its own `work/` folder of reproducers and `NOTES.md` files:
  `stage1-post-guards` (W1), `post-prefix-capture` (W2), `exit-24-0` (W3), `exit-40-0` (W4).
- Memory: `~/.claude/projects/-home-mattias-Documents-source-Ashes/memory/MEMORY.md` indexes the
  standing rules and one note per fixed class.

## Remaining items

### In the census (blocking zero)

1. **`32_28` + `32_29` (+2/fn): the leaked list `[28, 29]`** (W4, ends as notes). It is the `visited`
   accumulator of `withdrawCellRetains` in stage 1's `ConstructionRetains.ash:191`
   (`cellTemp :: visited`). It's a stage-0 codegen leak: stage 0 lowers the mutual recursion group in
   dispatch mode (`MutualRecursionTcoLowerDispatchAndWrappers`). The tail call's argument is a part of an
   owned RC tuple returned by a non-tail call, taken with `RcDup` and stored into dispatch parameter slot
   `visited`. That slot is arena-placed with a Mixed shape, so no back-edge or exit drop releases it.
   Skipping the dup would dangle, and copying into the arena is O(n^2) (rejected). Fix direction: place
   that dispatch slot on the RC heap as an ordinary loop does. A single-function version (`a3`) is flat,
   so find the rule that handles it there and why Mixed misses it. Notes: `round11-notes/W4-l28-dispatch-visited-NOTES.md` (copy of `exit-40-0/work/l28/NOTES.md`),
   reproducers `a2.tpl`, `a3`, `s4.tpl`. Unlanded WIP: `exit-40-0` branch `leak/dispatch-borrowed-call` at
   `21e1c07f` (`GRC_NO_DISPATCHBORROWEDCALL`: a flagless call in the dispatch body joins
   `_borrowedArgumentCalls`; correct with its negative, but it does not remove [28, 29]). Branch
   `leak/visited-cons` has notes only.
2. **`32_3` (+2/fn): a `(3, 2)` scalar pair** (W1). Owner site: a `remap` entry in
   `elideTrivialOwnershipCopies` (`IrOptimizer.ash:307`, `setAssociation`). The remap list `buildRemap`
   returns is never released after `applyRemap`. The dl probe reproduces +2/fn, so it is IrOptimizer as
   compiled by stage 0; small reproducers are still flat. Branch `stage1-post-guards`
   `leak/round11-32-3` (no commits). Notes: `round11-notes/W1-32_3-NOTES.md` beside this file.
3. **`32_ptr` (+5/fn)** (W2). Part of it is the (C') work below, and part is 40_9 (item 5).
4. **(C'), lowering constrained generic loops at concrete caller types** (W2; Mattias chose this to
   fix `Linux_backend_dictionary_call_key_in_local_loop_memory_should_plateau`, which now passes). Its
   copies exposed a pre-existing stage-0 leak in IrOptimizer `reduceLoop`'s `savedInts`/`knownInts` (arena
   loop parameters receiving RC values on two edges). Rule 1 `GRC_NO_ARENASUCCESSORJOIN` (a join that
   becomes an arena loop parameter copies its RC arms into the arena) and rule 2 (RuntimeRc placement
   for those parameters, fail-closed) were in progress with stage-1 ports. A third rule,
   `GRC_NO_COPIEDCELLHEADCONSTRUCTOR`, segfaults the real-code probe in an `__rcnorm` normalizer and must
   not land as is. Status: the (C') commits and rules 1+2 (`GRC_NO_CALLPARTACCUMULATOR`, `GRC_NO_ARENASUCCESSORJOIN`, both stages) ARE in PR #1153. Rule 3 is unlanded: branch `leak/c-prime-on-d1ec`, commit `1249450e` (based on `d1ec8b6e`; rebase with `git rebase --onto <tip> d1ec8b6e`, with a possible textual conflict in `lowerTailSelfCallArguments`); notes: `round11-notes/W2-copied-cell-head-constructor-WIP.md`. Probe after rules 1+2: +29/fn with them on and +27/fn with them off; the remaining +2 is 40_9 (item 5), which is rule 3's target. Not yet done for these picks: full suites, a stage-1 tally and the plateau tests.
5. **40_9 remainder** (W2, pre-existing in both stages): `devirtualizeReturnedClosureCallsOnce`
   (`IrOptimizer.ash` about line 1456) matches `Some(label)` out of an owned lookup result inside a
   tail-recursive rewrite loop and never releases the scrutinee (the other caller near 1426 does). A
   monomorphic reproducer leaks with no (C') involved. Rule 3 (above) is the attempt.

### Reproducer classes (not in the census, still part of "every known leak")

6. **`fn.tpl`: cons onto an arena accumulator (case 3)**. `found` grows by `call :: found` as arena
   cells holding RC heads with no reference of their own. It leaks 17 then 33 live in both stages. The
   plan is the Perceus route: find which rule keeps `found` in the arena (a sibling block, the as-binder,
   or `_linearReuseNames`) and extend RC placement, fail-closed. Fall back to an exit spine walk only if
   that block is load-bearing. Notes: `round11-notes/W3-fn-NOTES.md`; reproducers in `exit-24-0/work/fn/`.
7. **d2**: a handler program, +8/round on stage 1 only (stage 0 clean). Handlers turn general RC off.
   `exit-40-0/work/r40/d2.tpl`.
8. **g9**: stage 1 only; RSS grows 12 to 24 MB from 1k to 100k rounds with correct output, stage 0 flat.
   Not narrowed. `stage1-post-guards/work/w6/`.
9. **A constant 21-cell exit residue** in both stages on `c.ash` (W3, 11-09 15:04). It is not growth,
   but the target is zero.

### Parity and correctness items (stage 1 vs stage 0)

10. **As-pattern store order**: stage 1 places an as-pattern binding's store after the tag checks, stage
    0 after the field load. This affects parity only.
11. **`next` binder**: stage 1 retains and then releases it at the back edge, stage 0 does neither.
    Balanced, so parity only.
12. **30 of 95 whole-program parity diffs** (`parityonly.sh`): the same set as main. Compare them with
    main before treating any as new.
13. **Stage-1 selector-alias reach gap**: stage 1's `recordReachOf` resolves neither selector import
    aliases nor direct qualified calls. The stage-0 side is `GRC_NO_IMPORTALIASSCOPE`.
14. **Shared walker blind spot** (W2): `IsArenaLoopSuccessorLet` / `SelfCallOrdinalPassing` see only a
    self-call's LAST argument, because `EnumerateChildren` does not descend into `Call.Func`. Every
    let-successor rule misses the other positions. Fix it as its own change, then run the full poisoned
    e2e and the semantics suite: it may unmask leaks or crashes.

### After step E

15. **Audit rules whose switch no longer reproduces a leak** (for example `GRC_NO_CURRIEDCHAINRESULT`,
    which later rules made redundant).
16. **Remove the dead stage-1 exports** `findBlockingSibling` and `evaluateTcoPlacementProfitability`.
17. **Stage-1 ASH002 on a constrained helper used at two key types** (e.g. `lookupAssociation` at Int
    and Str): stage 1 does not let-generalize constrained bindings. That is the milestone-3
    trait-dictionary keystone, step P. In step E, use monomorphic reproducer variants instead.

## Before declaring step E done

- `Linux_backend_dictionary_call_key_in_local_loop_memory_should_plateau` must pass (it does at the
  tip).
- The full tally must show NET 0 and GROWTH 0 per function, and the exit census must be flat at N=80.
- Then the final combined pass (`integpar.sh --final`, stage-1 cap 20G), including stage-1 build time
  and peak RSS against main, with no clear regression (the milestone gate).
- Record the lasting lessons in memory and close E in `SELF_HOSTING.md`.
