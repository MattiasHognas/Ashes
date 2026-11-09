# Prompt for a new agent

Copy everything below the line as is.

---

You are picking up "Step E" of the Ashes compiler project (repo MattiasHognas/Ashes, owner Mattias).
Ashes is a pure functional language whose .NET compiler emits native ELF/PE executables. There are two
compilers:

- stage 0, in C#, under the .NET solution;
- stage 1, self-hosted, written in Ashes (for example `CoreLowering.ash` and `IrOptimizer.ash`).

Step E's goal is zero memory leaks per compiled function, in both stages.

Start by reading [ROUND11_HANDOFF.md](ROUND11_HANDOFF.md) and [SUBAGENT_BRIEF.md](SUBAGENT_BRIEF.md) in
`artifacts/selfhost-dev/handoff-step-e/`, on branch `selfhost/step-e-round11-cont` (PR #1153). They
hold the open item list, the rules, links to every tool, and the notes from the last round. The round
log is [round11-notes/ROUND11_LIST.md](round11-notes/ROUND11_LIST.md).

## The metric

The census compiles a fixed program, [loops10.ash](tools/round11/loops10.ash) and
[loops11.ash](tools/round11/loops11.ash), with the stage-1 compiler. It counts the live heap cells by
class, for example `32_3`, `32_28`, `40_9` or `32_ptr`.

- **NET** = live(11) - live(10) per function.
- **GROWTH** = the sum of the positive class deltas.

The only valid census is [fulltally.sh](tools/round11/q40/fulltally.sh); its recipe is in
[q40/NOTES.md](tools/round11/q40/NOTES.md). At the end of round 11 the tip was NET +20/fn and
GROWTH +32/fn. The top classes were `32_ptr` +5, `32_3` +2, `32_29` +2 and `32_28` +2, then
singletons. The picks that landed last (W2's constrained-loop copies and rules 1+2) have not been
tallied yet.

## Verification tools, in order of cost

1. **Leak templates.** They live in `src/Ashes.Fuzzing/Combinations/LeakCombinationTemplates.cs`
   (register new templates last) and are run by LeakTemplateTests. Each fix adds one template that
   reproduces the leak.
2. **Plateau tests and e2e tests**, run plain and with `ASHES_RC_POISON=1`. Poison is read at
   compile time. `ASHES_RC_LEAKCHECK=1` prints a live counter.
3. **Probes.** The real-code probe [cp/do](tools/round11/cp/do/Main.ash), with
   [docount.sh](tools/round11/cp/docount.sh) and [probekey.sh](tools/round11/cp/probekey.sh), counts
   live cells while compiling real stage-1 sources. The dl probe
   ([dlrun.sh](tools/round11/q41/dlrun.sh), project [dl](tools/round11/q41/dl/Main.ash)) is a fast
   lowering-only loop; [probe](tools/round11/q41/probe/Main.ash) adds `optimizeIrProgram`.
4. **Suites.**
   - Fuzzing.Tests: expect 107/107. The earlier word-pool template failure is fixed.
   - ir-program-parity (`scripts/selfhost-dev/parityonly.sh`): 30 of 95 diffs are pre-existing.
   - `scripts/selfhost-dev/regenfixtures.sh`: verify must be 207/207.
   - The stage-1 semantics suite (`scripts/selfhost-dev/suites.sh`) at MemoryMax=20G.
5. **gdb watchpoints** on a leaked cell's address, to find the missing release. Search memory for
   pointers to the cell with [findptr.c](tools/round11/q41/findptr.c).

Every stage-0 fix sits behind a `GRC_NO_<NAME>` switch (unset means the rule is on; stage 0 only), so
fixes can be bisected by sweeping the switches. Every stage-0 fix must be mirrored into stage 1 unless
stage 1 is already flat for that case. When it is, document the exemption.

## Hard rules

- Work only on mattias-cachyos, in `/home/mattias/Documents/source/Ashes`, in a worktree under
  `.claude/worktrees/`. Never work in the main checkout.
- Run nothing bare. Every dotnet, compile, gdb or test runs through
  `systemd-run --user --scope -q -p MemoryMax=<cap> -p MemorySwapMax=0`. Stage-1 builds peak at
  16-17 GB, so cap them at 20G.
- Run at most two heavy jobs at once, and keep 25 GB free. The slot wrapper is
  [heavy2.sh](tools/round11/jobs/heavy2.sh); it hands out three slots, so count the running jobs
  yourself.
- Never delete or kill anything yourself. Write a script into `.claude/worktree-archive/` and let
  Mattias run it.
- Never add attribution lines to commits or PRs; a hook rejects them.
- Ask before touching a remote branch, and keep a backup ref.
- Do not propose repo-side enforcement again (PR #1149 was refused).
- Open one draft PR per round branch, and explain it in plain language.

## Open items

Full list: [ROUND11_HANDOFF.md](ROUND11_HANDOFF.md), section "Remaining items".

### 32_3

The `(IrTemp, IrTemp)` remap lists from `elideTrivialOwnershipCopies` in `IrOptimizer.ash`
(`buildRemap`) are never released.

- **Cause:** stage 0 does not fuse the curried call `buildRemap(instructions)([])`, so the
  CallClosure's general-RC list result is never dropped at exit.
- **Reproducers:** [p4.tpl](tools/round11/repro/w9c/p4.tpl) leaks +4/round in both stages;
  [p3.tpl](tools/round11/repro/w9c/p3.tpl) is fused and flat.
- **Notes:** [W1-32_3-NOTES.md](round11-notes/W1-32_3-NOTES.md).
- **Directions:** resolve the curried chain to the compiled result fact, or extend the
  unknown-callee arena request to list results.

### 32_28 / 32_29

An `RcDup` of `nestedVisited`, a part of an owned call-result tuple from `withdrawCellRetains`, goes
into the arena dispatch slot `visited` of a mutual-recursion dispatch loop and is never released.

- **Fix direction:** place that dispatch slot on the RC heap.
- **Notes:** [W4-l28-dispatch-visited-NOTES.md](round11-notes/W4-l28-dispatch-visited-NOTES.md).
- **Reproducers:** [a2.tpl](tools/round11/repro/l28/a2.tpl), [a3.tpl](tools/round11/repro/l28/a3.tpl),
  [s4.tpl](tools/round11/repro/l28/s4.tpl), [neg2.tpl](tools/round11/repro/l28/neg2.tpl).
- **Earlier attempt:** branch `leak/dispatch-borrowed-call` (`21e1c07f`, the unhanded
  `GRC_NO_DISPATCHBORROWEDCALL`, in worktree `exit-40-0`) did not move the class. Use watchpoints
  before another guess.

### W2 rule 3 (`GRC_NO_COPIEDCELLHEADCONSTRUCTOR`, 40_9)

- WIP commit `1249450e` on the pushed branch `leak/c-prime-on-d1ec`, with notes in
  [W2-copied-cell-head-constructor-WIP.md](round11-notes/W2-copied-cell-head-constructor-WIP.md).
- It crashed the real-code probe once (`__rcnorm`), and the rewrite is unverified.
- There is also a pre-existing crash on the generic path: [rc.tpl](tools/round11/cp/rc.tpl).

### fn.tpl case (W3)

The exit-parameter own-field release does not cover it (17 then 33 live).

- **Notes:** [W3-fn-NOTES.md](round11-notes/W3-fn-NOTES.md).
- **Reproducers:** [fn.tpl](tools/round11/repro/fn/fn.tpl), [fn_1.ash](tools/round11/repro/fn/fn_1.ash),
  [fn_2.ash](tools/round11/repro/fn/fn_2.ash).

### The rest

These are in [ROUND11_HANDOFF.md](ROUND11_HANDOFF.md): d2, g9, the constant 21-cell exit residue,
the parity items, the shared-walker blind spot, the audit of redundant rules, and the dead stage-1
exports. A known issue that belongs to step P, not E: stage 1's ASH002 on a constrained helper used at
two key types.

## How to work

Take one class at a time:

1. Reproduce it with a template.
2. Find the missing release with a watchpoint.
3. Fix stage 0 behind a switch.
4. Mirror the fix to stage 1.
5. Run the template plus the plateau tests, then one census.

Report NET and GROWTH before and after each pick.
