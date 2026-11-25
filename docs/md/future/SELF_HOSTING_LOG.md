# Self-Hosting Log

What each merged pull request and each round of the current branch changed, newest first. The plan
is in [SELF_HOSTING.md](SELF_HOSTING.md) and the item catalogue in the
[checklist](SELF_HOSTING_CHECKLIST.md). The log as it read before it became a changelog, with every
round checkpoint, the completed checklist entries and the language prerequisites audit, is in the
[archive of 2026-11-25](https://github.com/MattiasHognas/Ashes/tree/main/docs/selfhost-archive/2026-11-25).

Behaviour rows below are stage 1 (or stage 2) against stage 0 on the `tests/` corpus, as
same / differing only in live cells at exit / any other difference.

## 2026-11-25, `selfhost/round11` round 8 (PR #1161)

Located by `9c4b2e0d`.

- Partitioned multi-process code generation is the default: one partition per 1,024 lifted
  functions (at most 16), so a program below 2,048 functions keeps the single-module path.
  `ASHES_LLVM_JOBS=1` selects one module; `ASHES_LLVM_PROCESSES` (default 6, capped by the online
  processors) bounds the child processes. Output is the same for every process count.
- Step 8 of "Type inference before lowering", entries 1, 2, 3 and 5: an operator over open operands
  and an open trait goal are refused at their site, evidence dispatch reads the recorded operator
  instance, and a deep copy of an open-typed value is made at its recorded type. The corpus's
  lowered IR is unchanged; 12 deep-copy sites in the compiler change.
- The measurement driver shadow-compares all eleven step-8 entries and hashes the lowered IR
  independently of where the entry file lives.
- Stage 0's trivial field accessor fast path is ported: a call of a function whose body is a
  borrowed field read of its parameter retains the field instead of copying it. The wider case is
  recorded as OPT-88.
- The behaviour check was run under `ASHES_RC_POISON=1`; the wrong-output defects it found are in
  the plan's "Known open defects" table, all present on main.

| Measure | Value |
| --- | --- |
| Stage 1 built by stage 0 | about 29 min, 19–20 GiB |
| Stage-1 behaviour (1,270 programs) | 1,067 / 188 / 0 |
| Stage 2, defaults (16 partitions, 6 processes) | 620 s, 17.4 GiB; relocatable merge 938 ms |
| Stage 2, `ASHES_LLVM_JOBS=1` | 856 s, 13.5 GiB |
| Stage 3 built by stage 2 | 475 s, byte-identical to stage 2 |
| Stage-2 behaviour | zero non-leak rows |
| Semantics suite | 96 of 98 groups |
| Backend suite | 495 of 497 |
| C# lowered-IR parity tests | 208 of 208 |

## 2026-11-25, `selfhost/round11` rounds 6 and 7

Located by `a5dd2751`.

- The compile command lost the stitched-program lowering, `ASHES_TYPED_LOWERING` and
  `ASHES_SKIP_INFERENCE_CHECK`; the measurement driver lost its legacy mode.
- A trait goal left under typed lowering is refused instead of replayed.
- A rejected program is worded by the inference alone: it carries the operator, call and argument
  site stage 0 words a unification error after. Of the 131 `expect-compile-error` tests, 106 print
  their expected message from the inference, 13 are rejected by lowering's own checks and 11 fail
  at stitching.
- The raw lowering entry points moved to `CoreLowering.Raw` and `CoreLowering.Measurement`, outside
  the compiler's import closure; they remain for the lowered-IR parity tests until step 9.
- The relocatable merge reads each input's bytes and symbol maps once per relocation section
  instead of per relocation: 22.6 s to 0.035 s on a 9.1 MB merge, byte-identical to stage 0's
  merged object; on the whole compiler, 414–427 s to under one second.
- Round 7's partition measurements were lost to a machine restart and rerun in round 8.

## 2026-11-25, `selfhost/round11` round 5

Located by `0e72ed89`.

- Lowering from the type table is the default of `compile` and `run`; the bootstrap closes with it.
- `ASHES_DIAGNOSTIC_LLVM_STATS` also reports the partition plan, per-partition timings, child
  phases and the merge.
- Child-process partitions measured: byte-identical at K = 1, 2, 6 and 8, per-partition LLVM work
  320 s at K=1 and 62 s at K=6, but the serial merge (414–427 s) made K=6 slower than one module
  (1,010 s against 880 s), so partitions stayed off by default until round 8.

| Measure | Typed lowering (default) | Old path |
| --- | --- | --- |
| Stage-1 behaviour | 1,065 / 188 / 0 | 1,064 / 189 / 0 |
| Stage 2 built by stage 1 | 14:40, 10.5 GiB resident | 16:20, 16.2 GiB |
| Stage 3 built by stage 2 | 13:08, 37.0 GiB with swap, byte-identical | |
| Stage-2 behaviour | 1,065 / 188 / 0 | |
| Semantics suite | 96 / 96 | |
| Backend suite | 495 / 497 | |

## 2026-11-24 to 2026-11-25, `selfhost/round11` rounds 3 and 4

Located by `5c8434ec` (round 3) and `6c2db48b` (round 4).

- Typed lowering over the corpus (1,148 programs): 0 replays, 0 pending goals, 0 equality goals,
  0 deferred operators. Arithmetic-generic bindings used at several types take their operator's
  dictionary; synthesized identities for the strict partial-stage calls; a requirement the
  enclosing function supplies is taken from its own dictionary. The recorded lambda-parameter read
  was dropped (ten programs leaked with it).
- First bootstrap with typed lowering on every stage; stage 3 byte-identical on both paths.
- Code generation in child processes (CG-17 step B): correct and scaling, not yet faster, because of
  the serial merge.
- A shipped module's qualified reference to its own binding is no longer read as a self import (any
  project using `Ashes.Task.Parallel` failed with `ModuleImportCycle`).
- Retracted: round 2's "stage 3 does not build". The cause was the 24 GiB cap with no swap, not a
  regression. A stage-2 compiler compiling the compiler needs about 39 GiB resident plus swapped
  memory on main as well; stage 3 runs under `MemoryMax=40G MemorySwapMax=32G`, sampling `VmRSS`
  and `VmSwap` together.

| Measure | Default path | Typed lowering |
| --- | --- | --- |
| Stage-1 behaviour | 1,065 / 188 / 0 | 1,066 / 187 / 0 |
| Stage 2 | 16:20, 16.2 GiB | 13:08, 14.1 GiB |
| Stage 3 | 15:04, 48.3 GiB with swap, byte-identical | 11:30, 36.9 GiB with swap, byte-identical |

## 2026-11-23 to 2026-11-24, `selfhost/round11` rounds 1 and 2

Located by `91b77bc5`.

- Diagnostic parity: locations for local recursive signature mismatches, stitched trait names and
  stage 0's context at requirement sites, a second join of a task handle.
- The CLI releases the stitched program, the inference tables and the IR before code generation
  and LLVM optimization.
- Typed lowering: reduced dictionary goals from the table, recorded lambda parameter types, the
  body's own evidence for a whole open type, and the corpus sweep script.
- CG-17 step A: sequential partition modules behind `ASHES_LLVM_JOBS`, merged by LNK-14.
- A cached routed reuse-specialization callee is bound at a fresh instantiation of its scheme;
  without it stage 1 rejected the compiler source with a false `ASH002 Bool vs Int`
  (`tests/routed_map_set_at_two_value_types.ash`).

| Measure | Value |
| --- | --- |
| Stage 1 built by stage 0 | 28:23, 18.5 GiB |
| Stage-1 behaviour | 1,064 / 188 / 0 |
| Stage 2 | 16:56, 16.2 GiB |
| Stage-2 behaviour | 1,063 / 189 / 0 |
| Stage 2 partitioned (`ASHES_LLVM_JOBS=auto`) | 22:57, 17.4 GiB, two builds byte-identical |

## 2026-11-23, PR #1160: round 10

Stage 1 at parity, stage 2 runs, stage 3 byte-identical. Located by `4c26b1ab`.

- Stage-1 capability resolution: capabilities keep their declared name and are program-wide, as in
  stage 0; provider implementations are checked against the operation's `needs` row; a capability
  operation wins over a same-named trait method.
- Recursive evidence summaries converge: the merge keeps the earliest stage per use, as stage 0
  does (the compiler's `inferExpressions` settles at pass 4 with 648 uses instead of growing past
  310,000).
- The replay pass no longer fails on an operator over a still-open operand: a body's provisional
  first lowering runs unpinned.
- Stage-0 miscompiles fixed, each with regressions: a branch returning an owner bound before the
  `if` or `match`, a branch returning a component of a tuple bound before it, and
  `first :: second :: more` rebuilt in a match arm.
- Stage-2 crashes fixed: a lone `let recursive` double-released its tail, and a loop returning a
  generic tuple finished its result after its exit drops.
- Stage-2 memory: every closure copy inlined a dispatch over every environment normalizer; stage 1
  now uses stage 0's shared helper. LLVM instructions for the self-compile 55.5 M to 13.2 M.
- An operator-generic `let` below top level gets per-concrete-call copies, so the `generic_add_*`
  rows match stage 0; a binding's eager part is evaluated once at the binding.

| Measure | Value |
| --- | --- |
| Stage 1 built by stage 0 | 23:02, 19.7 GiB |
| Stage-1 behaviour | 1,061 / 190 / 0 |
| Stage 2 | 15:43, 17.2 GiB (was 39.9 GiB) |
| Stage 3 | 14:07, 14.4 GiB, byte-identical |
| Stage-2 behaviour | 1,061 / 190 / 0 |
| Backend suite | 245 / 246, normal and poison |
| Semantics suite | 96 / 96; whole-program IR 32 of 95 differ |

## Leak branch (not merged)

Branch `probe/r12-leaks-b` and the `fix/r12-leak-*` branches; step 2 of the plan brings them onto
main.

- Probe L: 1,131 same / 133 live-differs / no crash. The plan counts 36 programs and about 22,600
  cells leaking more than stage 0 after it, against 97 programs and 7.1 million cells on the
  integration branch.
- Rows fixed by probe: `consumed_argument_through_await` 40,005 to 5; `loop_record_field_cons_returned_at_exit`
  22,015 to 1; the loop-exit field through `if` 22,007 to 1; concrete-copy callee facts (eight
  rows, among them 6,298 to 2,094 and 489 to 21); comparison calls borrowed 1,003 to 3; async frame
  retain and dropper at stage 0's counts under poison.
- Use-after-free fixes: a let alias of a local table outliving a back edge (probe F); views of
  let-bound call results on `fix/r12-leak-generic-call` and `fix/s1-view-backing-uaf` (the plan's
  defects table lists what still fails).
- Rejected: reading every pattern owner as not a reference-counted aggregate child (seven crashes,
  90 new leak rows), and a stage-0-rebuilding stack whose stage 1 segfaulted on 1,171 of 1,273
  programs.
- Stage 0 itself leaks: a stage-0-built compiler has 221.7 million live cells when it finishes
  compiling the compiler. Six leaks present in both compilers are found, five with fixes on the
  branch.
- Stage 0 needs about 23 GiB to build stage 1 from the branch; not attributable to one commit.

## Earlier milestones

Details are in the archived log and plan.

- 2026-11-21: round 10 paused at a usage limit; branches consolidated into one, retired states kept in
  the [2026-11-21 archive](https://github.com/MattiasHognas/Ashes/tree/main/docs/selfhost-archive/2026-11-21).
- 2026-11-18, #1159: stage-2 miscompile fixes, inference edge cases, the first typed-lowering slice.
- 2026-11-18, #1156: the fixpoint. Stage 0 builds stage 1, stage 1 builds stage 2, and stage 3 is
  byte-identical to stage 2 (45,765,488 bytes); stage 1 type-checks with the standalone inference.
- 2026-11-12, #1154 and #1155: native bootstrap advanced, type-graph queries cached.
- 2026-10-22 to 2026-11-09, #1142 to #1153: step E leak rounds; the leak census of stage 1 came up
  empty (#1145) and the exit residual per added function fell from about 1,770 to 821 cells.
- 2026-09-20 to 2026-09-25, #1114 to #1140: the ownership contract (borrowed parameters, owned
  results) in both stages, the development tools in `scripts/selfhost-dev/`, and stage 1's module
  probe from 2,767 MB to 920 MB.
- Before 2026-09-20: the self-hosted frontend, formatter, semantics, IR, LLVM backend and ELF linker
  were built up item by item, after an audit of the language and standard-library prerequisites.
