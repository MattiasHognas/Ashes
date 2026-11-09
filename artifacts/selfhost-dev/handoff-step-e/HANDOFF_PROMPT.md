You are taking over work on Ashes: a compiler for a pure functional ML-family language, written in C#/.NET 10, that is being self-hosted. Stage 0 is the C# compiler (`src/Ashes.*`). Stage 1 is the self-hosted compiler written in Ashes (`selfhost/packages/*`), and stage 0 compiles it. Work autonomously: the user wants results, not questions. Stop and ask only before something destructive or hard to undo, or when a finding changes the task's direction.

## Where you are

- Worktree: `/home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free`. Stay in it; do not `cd` to the main checkout.
- It is checked out **detached at `origin/main` = 3f33fe2e** (#1140, the work-order reorder, on top of #1139). There is no working branch. Cut one fresh branch from `origin/main` per PR.
- The **local `main` ref is stale** (it sat at efba26eb while `origin/main` moved on). Always compare, diff and branch against `origin/main`, after `git fetch`.
- The `commit-msg` hook rejects `Co-Authored-By:` and `Claude-Session:` lines, so leave those out. PR descriptions carry no robot footer either.
- Your memory index is `~/.claude/projects/-home-mattias-Documents-source-Ashes/memory/MEMORY.md`. Read it first. The entries for this arc are `project_recursive_group_owned_results_wip.md` and `project_closure_field_in_stage1_state_breaks_contract.md`, plus the standing rules.
- This handoff's own folder is `artifacts/selfhost-dev/handoff-step-e/` (gitignored). Its `tools/` hold scripts written during the last PR; see Tools. The previous task's folder, `artifacts/selfhost-dev/handoff-recursive-group/`, keeps its reproducers and history. Its switch-based tools no longer work (see Tools).

## Where this fits in the plan

`docs/md/future/SELF_HOSTING.md`, section "Work order": one lettered list, A to T, worked top to bottom.
A to D have landed (#1114 to #1121). **You are still in step E: finish the leak work under the mirror
rule.** The fixpoint comes straight after E:

- **F:** clear the blockers the probe reports, starting from the three known stops of the stage-2 build.
- **G:** compile the whole self-hosted tree with stage 1.
- **H:** make stage 2 run.
- **I:** remove the sources of nondeterminism.
- **J:** establish the fixpoint: stage 2 == stage 3.
- **K:** bring fannkuch-redux back to its memory footprint.
- **L:** split `CoreLowering.ash` (about 24,900 lines) into `Lowering.<Concern>.ash` modules. This is a pure move, and parity stays byte-identical.
- **M:** verify the LSP and the DAP against the current codebase.
- **N to T:** full corpus, command-line completion, the rest of the port, more targets, editor tooling, then replacing the default toolchain.

**How this relates to the five bootstrap-fixpoint tasks.** Memory records a standing order
(`project_bootstrap_fixpoint_remaining_tasks.md`, `feedback_bootstrap_fixpoint_first.md`): the goal is a
bootstrap fixpoint. Stage 1 compiles its own source into stage 2, stage 2 compiles it into stage 3, and
stage 2 must equal stage 3 byte for byte. The work order now follows that order. Its five tasks map onto
the steps like this:

| Bootstrap task | Steps |
|---|---|
| 1. Stage 1's own memory (it ran out compiling itself) | **E**, this handoff |
| 2. The representation test | Done (#1108) |
| 3. The stage-1 defects that stop the self-compile during lowering | F, G |
| 4. Everything after lowering (IR optimizer, LLVM codegen, linking), never run at compiler size; expect at least one unknown blocker | G, H |
| 5. The fixpoint itself: stage 2 == stage 3 | I, J |

Step F's starting list in SELF_HOSTING.md is the current one:
- a `show(key)` in `IrCodegen.Support.lookupIndexedIn`, called with both `Str` and `Int` keys (split it into a `Str`-keyed and an `Int`-keyed version);
- `ASH002 Type mismatch: u64 vs u64` in a call to `IrCodegen.FloatText.floatTextConst`;
- past lowering, the list-marshalling part of CG-11.

The memory note's task 3 names older defects (a `CoreOperatorTypeMismatch("not", ...)` and SEM-22), so
part of step F's first job is to check which of them still occur.

**What "leak-free" means** (SELF_HOSTING.md, the definition E is judged by): compiling one more function with stage 1 adds no reference-counted cell that is still live when the process exits, and resident memory plateaus over a long compile.

The user's decisions for this arc:
- Leak-free first, then the fixpoint (steps F to J), then the rest of the list.
- The mirror rule: every stage-0 lowering change gets its stage-1 counterpart in the same PR.
- One fresh branch from `origin/main` per PR; self-merge with squash, then delete the branch.
- Never delete `main` or `release/*`.
- Work autonomously on this arc: take the option you would recommend, record the reasoning in the plan and memory, and continue.
- **Milestone gate** (memory `feedback_selfhost_milestone_gate.md`): no clear time or RSS regression against `main` on the `challenges/` programs or the probe. A regression must be root-caused, then either fixed or deferred with the user's explicit sign-off. For #1139 the user signed off on n-body's 4 to 5%; winning it back is item 2 below.

## What just landed: #1139

"Recursive-group records join the ownership contract (probe 902 MB to 658 MB), mirrored in stage 1."

A normalizable single-constructor record with a heap child is now a contract type, in both stages. It is not recursive and not already covered by the owned-child or recursive-copy paths (`IsInlineCopiedContractRecord`). Such a record is copied by its normalization helper and released by a per-type dropper, `__rcdrop_record_N`. The rest of the PR:

- A recursive-group sibling call withdraws its arena-result request once its result turns out to be a contract type (`CancelArenaResultRequest`).
- A `let` window's reload keeps the normalized-join fact.
- Owned slots record the match arm or if branch they were stored in, and a back edge skips a sibling arm's slots (`SharesIterationWith`). Stage 1 keys a branch by (join label, arm label).
- A loop exit's join with only freshly produced reaching arms is newly produced.
- A record successor built at the self call releases its retained children at the back-edge copy (`SuccessorOwnsItsChildren`).
- The TMC empty chain owns a borrowed base case.
- Lifetime placement uses immediate dominators.
- The #1045 view copy skips a reference-counted contract result.
- Stage 1 answers "is this a contract record" from a `contractRecordFacts` table of type names, filled once at program start, and never from a closure (see Lessons).

`architecture.md` has the section "Borrowed parameters, owned results", and SELF_HOSTING.md step E has the round's account ("Class C, closed for records").

Measured at merge:

| Measurement | Result |
|---|---|
| Module probe (`probepeak.sh`), stage 1 built from pre-PR `main` | 902 MB, 77.5 s |
| Module probe, stage 1 as merged | **658 MB, 74.7 s** |
| Class-C reproducers (`recursive_group_threads_record_state`, `group_sibling_result_passed_to_recursive_call`, `record_result_through_closure_parameter`) at 1,000,000 rounds | flat at 8 MB (were 700 to 1,220 MB) |
| Gate | six suites, parity 69/69, Ashes.Tests 2753/0, LSP 72/72, e2e 824 plain and poisoned, format clean |
| `challengesvsmain.sh` | same output and RSS everywhere; **n-body 2.46 s -> 2.57 s** |

Binaries kept for comparison in `artifacts/selfhost-dev/`: `s1-mainref` (stage 1 from pre-PR `main` c4421309) and `s1-table` (stage 1 as merged). The published stage-0 CLIs `cli-main` (c4421309) and `cli-rgrec` (the PR, same stage 0 as merged) are there too.

**Reachability census after #1139** (`reachat.sh artifacts/selfhost-dev/s1-table 60`, 2026-09-25; the
probe is snapshotted at 60 s, then the census walks the core for a few more minutes):
- reachable: 178.6 MB (37%);
- **leaked for certain: 306.5 MB (63%)**, 7.9 million cells (a lower bound);
- before #1139 the same census found about 75% leaked.

The largest leaked root groups, by shape (each root claims what it reaches first):

| Root shape | Roots | Claim |
|---|---|---|
| 200 bytes, first word a pointer (large records, probably lowering-state versions) | 11,641 | 71.1 MB |
| 32 bytes, first word a pointer | 291,851 | 54.1 MB |
| 32 bytes, first word an integer | 448,412 | 52.3 MB |
| 136 bytes, first word an integer | 23,874 | 25.9 MB |
| 64 bytes, first word an integer | 56,303 | 20.9 MB |
| 24 bytes, first word an integer | 669,337 | 15.3 MB |
| 48 bytes, first word a pointer | 6,667 | 15.0 MB |

The source sites behind these shapes are not identified yet: naming them (`REACH_SHAPE`, `rcrootsof`, the
census tools) is plan item 1.

## The plan, in order

1. **Measure a fresh stage 1 from `origin/main`** (`quickstage1.sh <tag>`, then `probepeak.sh` and `reachat.sh <bin> <secs>`). Everything below is re-ranked by this.
   - The leaked fraction is measured (63%, see "What just landed"). Name the source sites behind the largest root shapes (`REACH_SHAPE`, `rcrootsof`, `reachcensus`), starting with the 11,641 roots of 200 bytes (71 MB), and rank them.
   - Confirm the handoff-recursive-group task's original target is gone from the real probe: about 72 MB of leaked two-word reach-count lists from `ResultReachSummaries.ash`'s `reachOf` group (`substituteEntries`, `reachSum`).
   - Check the lexer finding that was never directly fixed. The lexer loop `lexerScan` (`selfhost/packages/frontend/src/AshesCompiler/Frontend/Lexer.ash`, near line 777) destructures the owned pair `(Token, Maybe(DiagnosticEntry))` from `lexerReadWithDiagnostic`. `EmitGeneralRcBackEdgeDrops` / `NoSuccessorCanHoldPartOf` (`Lowering.GeneralRc.cs`) skip an owned slot that has no fresh-flag slot. After #1139 a lexer-only probe (`handoff-recursive-group/lexloop.sh`) showed no extra leak, but the rule is unchanged. If the census still shows `Token` roots, this is an early item in step 5.
   - Run `s1diffprog.sh`, which the #1139 mirror did not run, and `s1survey.sh -b`. Compare the survey with a baseline survey from the same commit. The old baseline file is gone, so regenerate it. Known non-enforced gaps as of #1139:
     - `record_update_successor_of_loop_parameter`: stage 1 does not mark `scan` as `ReturnsRuntimeManaged`. This predates #1139.
     - `tco_parameter_kept_by_borrowing_callee_result`, `mutual_recursion`, `handle_match_arm_reset`: all predate #1139.
2. **Win back n-body** (the milestone-gate follow-up to #1139). Its `System` holds five `Body` cells, so it is a contract record now. Per iteration, `advance` copies its fresh result through `__rcnorm_N`, where the loop used to copy the arena successor inline at the back edge. The loop then retains that result for its parameter (the representation test's `RcDup`) and releases the call's owned slot, where a move would do. Two fixes, smallest first:
   - **(a) Back-edge move.** When a successor is a plain read of an owned slot this iteration stored, take the slot over instead of retaining the value and releasing the slot. The facts exist: `_generalRcBackEdgeArgumentSlots` / `OwnedSlotReadInto`, `OwnedSlotsTakenBySuccessors`, `EmitGeneralRcPredecessorDrops` in `Lowering.GeneralRc.cs`. Stage 1 has `argumentOwnedSlots` in `CoreTcoReset`. This changes core back-edge ownership, so the six suites are the gate.
   - **(b)** Build a fresh contract-record result on the reference-counted heap in place, instead of in the arena followed by a normalizer copy.
   - Measure with `tools/nbody.sh` (IR diff of n-body, main vs branch) and `challenge_ab.sh main <cli-main> <label> <cli> n-body n-body`. Mirror in stage 1, same PR.
3. **The `_borrowedArgumentCalls` identity bug** (stage 0, `Lowering.cs`, a `HashSet<IrInst.CallClosure>` with reference equality). `Emit` (`Lowering.SourceContext.cs`) re-creates each instruction with `with { Location = ... }`, so the object added to the set is never the one in `_inst`. `PerceusLifetimePlacement.Place(loweredProgram, _borrowedArgumentCalls)` therefore never sees a match. `_arenaResultRequestingCalls` had the same bug and was fixed by storing `_inst[^1]` after emitting. Fix it the same way, then measure the probe and the six suites: enabling a fact that has been silently off may move releases. Check whether stage 1 has a counterpart that works.
4. **Parked branches and housekeeping.**
   - `selfhost/record-update-retains` (10925162): the stage-0 `RetainUnchangedRecordUpdateField` reporting to `CallArgumentRetainFrame`. Stage 1's `CallArgumentRetainFrame` port landed in #1129, so it can probably be finished and mirrored now. Rebase it onto `origin/main` first.
   - `selfhost/leaked-lowering-states`, three WIP commits:
     - an in-place span update in `lowerCoreProgramItems`: take;
     - the `IrOptimizer` `countBranchRefsToLabels` map, with reachcensus reporting its top 4000 roots: take;
     - a stage-0 taint rule (`TempsComputedFromSlot` / `PropagateOwnedValueTaint`) with no stage-1 mirror (probe 903 -> 894 MB): drop unless the new census gives it a reason.
   - `selfhost/loop-parameter-placement` and `selfhost/patternwalk-callee-side` are fully merged. Delete them only after the user confirms (a deletion).
   - Add `group_sibling_result_passed_to_recursive_call` as an e2e test under `tests/` (the other two class-C reproducers already are), named for the scenario, with an `// expect:` directive and `fmt -w`.
   - Move `tools/s1at.sh` into `scripts/selfhost-dev/` with a README line. It is the bisection method that replaced the removed switches.
5. **The remaining leak classes from step 1, largest first.** Use the method that found every fix so far:
   - reduce the leak to a reproducer of a few dozen lines;
   - diff the census (`tinycensus.sh`, `progcensus2.sh`);
   - put a hardware watchpoint on one leaked cell's count (`tinywatch.sh`);
   - fix stage 0 and mirror it in stage 1 in the same PR.

   Known candidates:
   - `handoff-recursive-group/declrepro.ash`: a parser temporary token list, 264 MB. It leaked before #1139 too.
   - The two reproducers the owned-return guard excludes (SELF_HOSTING.md step E, the PatternWalk round).
   - About fifty self-hosted functions whose generic result type leaves owned values behind. These need type annotations in the stage-1 source, not a compiler change.

   Every fix passes the six suites, and `probepeak.sh` times it. Any change that adds a field to a stage-1 state record gets a probe run of its own (see Lessons).
6. **Show E is done.**
   - Run the exit census over two generated inputs (N and N+1 functions, `mktinyinputs.sh` / `tinycensus.sh`). The difference must be zero.
   - Show the memory plateau over a long compile with the step-D tools (`plateau*.sh`, `reachat.sh`).
   - Run the full gate: `gatepr.sh`, the six suites, `challengesvsmain.sh`, `probepeak.sh`.
   - Move E's entry to `docs/md/future/SELF_HOSTING_LOG.md`, then start step F.

Outside E but worth knowing: stage 1 still lacks stage 0's call-site element specialization (OPT-60). Since #1139, stage 0 uses it for any generic list producer over contract records, so more programs now differ from stage 1 there. The `generic_list_result_deep_copy` fixture was changed to bind its recursive call, which keeps its test comparable.

## Lessons from #1139 (read before changing lowering)

- **Never put a function-typed field into a stage-1 lowering-state record** (`CoreProgramState`, `DropperTypes`, `DropperBody` and the like).
  - Stage 0's `IsGeneralRcAdmissible` excludes closures, so the record and every record holding it leave the contract, and the probe went from 660 MB to 3.4 GB.
  - Neither the suites nor parity see this; only `probepeak.sh` does. Pass data, a `MapTree` table, instead.
  - It was found by bisecting stage-1 sources with a fixed stage 0 (`tools/s1at.sh`).
- **Only the probe measures stage 1's own memory.** Stage 1's memory depends on how stage 0 lowers stage 1's source, so a stage-1-only change can regress it while every suite and parity stays green.
- **Adapting shape tests.** When a stage-1 shape test fails after a lowering change, first check that stage 1 matches stage 0 on that test's source:
  - `tools/extractall.sh` pulls every `let ...Source = "..."` program out of the semantics tests;
  - `tools/cmpfn.sh <name> "<function header text>"` compares one function across the two stages, normalized.

  Only then update the counts. `TcoOwnershipRulesTests`' `loopLines` strips representation-test retains (`withoutGuards`), so its `RcDup` counts do not include them.
- **Test shapes can vanish.** A lowering change can remove the very shape a test was written for. In #1139 stage 1 began TMC-transforming a producer, and stage 0 began element-specializing a generic call. Change the test's source so it keeps exercising the original rule (binding the recursive call before the cons stops both), and say so in the test's comment.
- **Fixtures.** After a stage-0 change, `regenfixtures.sh` regenerates the shared lowered-IR fixtures. Then `parityonly.sh` checks the 69 enforced whole-program fixtures.
- **Stage 0 is the reference, but it can be wrong.** In #1139 stage 0 leaked where stage 1 did not (the view rule). A parity difference is a question, not an automatic stage-1 bug.

## Tools

The standard tools are in `scripts/selfhost-dev/` (see its `README.md`):
- `quickstage1.sh <tag>` rebuilds the CLI, builds stage 1 into `artifacts/selfhost-dev/s1-<tag>`, and runs two tiny programs. `debugstage1.sh <tag>` makes a debug build.
- `probepeak.sh <bins...>` runs the module probe under a 40G cap. Its normal end is exit 1 with `MissingCoreTraitEvidence`.
- `reachat.sh <bin> <secs>`: leaked vs reachable census of the RC heap.
- `tinycensus.sh`, `tinyrootshape.sh`, `tinywatch.sh <debug-bin> <src> <cell-address> ...`: exit census of stage 1 on a tiny input, and the refcount history of one cell. Pass the cell address, not the header.
- `progcensus.sh` / `progcensus2.sh <prog.ash>`: census of a program built by stage 0. `plateau.sh <prog.ash> <rounds...>`: RSS of a reproducer template at N rounds. Use a million rounds for class-C sized leaks; 1000 shows nothing.
- `suites.sh [names]`, `parityonly.sh`, `s1survey.sh -b`, `s1diff.sh [-b] <fixture>`, `s1diffprog.sh`, `regenfixtures.sh`, `gatepr.sh [changed .ash files]`, `challengesvsmain.sh <label>`, `challenge_ab.sh`, `publishcli.sh` / `publishref.sh`.
- Reproducer templates: `scripts/selfhost-dev/reproducers/`.

This handoff's `tools/` (work files go to `../work/`):
- `s1at.sh <commit> [stage0-cli]` builds stage 1 from a commit's `selfhost/` and `lib/` trees with a fixed stage 0 and probes it.
- `cmpfn.sh <name> "<function header text>"` compiles `work/<name>.ash` with stage 0 (`--emit-ir lowered`) and with the stage-1 dump tool, then diffs that function normalized. `cnt.sh <name> "<header>" <pattern> <pattern2>...` counts lines of the stage-1 function, for rewriting shape-test counts.
- `extractall.sh` writes every embedded test program of `selfhost/tests/semantics/*.ash` to `work/all/`. `fndiff.sh <a.ir> <b.ir>` lists which functions differ between two normalized IR dumps.
- `nbody.sh` diffs n-body's final IR between the `cli-main` and `cli-rgrec` compilers. Point it at the CLIs you are comparing.
- `plat3.sh` plateaus the three class-C reproducers.

The stage-1 dump tool is `artifacts/selfhost-dev/dumpir/out/stage1-dumpir`, rebuilt by `s1diff.sh -b` / `s1survey.sh -b`.

The handoff-recursive-group tools `runrepro.sh`, `inlbisect.sh`, `s1bisect.sh` and `suitegdb.sh` (with name files) depend on the `GRC_INLINE_RECORDS` switch, which #1139 removed. Use them for their shape only. `lexloop.sh`, `lexmatrix.sh`, `loopcensus.sh` and `loopwatch.sh` (the real lexer and parser run in a loop) still work.

The bash harness refuses several shell forms:
- loops over variables;
- `$(...)` passed into commands;
- variables inside `sed` programs;
- `let` in some forms;
- complex `git` forms.

Put multi-step logic in a script file and run `bash script`.

## Rules (CLAUDE.md, AGENTS.md and memory)

- **Validation before any change lands:**
  - `dotnet build Ashes.slnx`
  - `dotnet run --project src/Ashes.Tests -- --no-progress`
  - `dotnet run --project src/Ashes.Lsp.Tests -- --no-progress`
  - `dotnet run --project src/Ashes.Cli -- test tests`, plain and with `ASHES_RC_POISON=1`
  - `dotnet format Ashes.slnx --verify-no-changes`
  - `fmt -w` on every `.ash` file you touch. `fmt` takes one path per call.
- **Plus, for lowering changes:** the six self-hosted suites, `parityonly.sh`, `challengesvsmain.sh` and `probepeak.sh`.
- **Code:**
  - `[NotInParallel]` is banned.
  - No Python, no emojis, no decorated comment dividers; mermaid rather than ASCII diagrams.
  - Comments describe the code, not the reasoning history. No task IDs or markdown references in code.
  - C# methods stay at 60 lines or fewer (MA0051). Compare strings with `string.Equals` and an ordinal comparison.
- **Process:**
  - Update `docs/md/reference/language.md` before any behavior change.
  - Cap memory only with `systemd-run --user --scope -q -p MemoryMax=40G -p MemorySwapMax=0`; never `ulimit -v`. Run one heavy job at a time.
  - Never use bare `git stash`. Prefer a local WIP commit.
  - Narrow before fixing, and find the root cause before reverting.
  - Use gdb watchpoints rather than debug prints.
  - The self-hosted suites are the ownership gate: e2e plus poison has stayed green through bugs only they caught.
  - Changes to `selfhost/packages/frontend/` require refreshing the frontend `ash1:` hash in all six `ashes.lock` files.

## Docs to read first

- `docs/md/future/SELF_HOSTING.md`: "Start here", "What leak-free means", the Work order, and step E in full (last paragraph: "Class C, closed for records").
- `docs/md/internals/architecture.md#memory-model`, including "Borrowed parameters, owned results".
- `docs/md/internals/ir.md`
- `docs/md/guide/testing.md`
- `scripts/selfhost-dev/README.md`
- `CLAUDE.md`
