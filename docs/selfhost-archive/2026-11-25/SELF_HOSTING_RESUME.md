# Resume Ashes self-hosting after round 10

Round 10 merged as #1160 on 2026-11-23 with the bootstrap closed again: stage 3 is byte-identical to
stage 2, stage 1 and stage 2 match stage 0 on every non-leak behaviour row, and the self-compile
peaks at 17 GiB. The remote branch `selfhost/round10` is deleted.

Work continues on `selfhost/round11`, branched from main, in the worktree
`.claude/worktrees/step-f-round3`, with one draft pull request that stays open until typed lowering
is the default (step 7), the order-dependent ownership rules are replaced (step 8) and code
generation runs in parallel partitions (CG-17). A last pull request then takes the remaining leaks.

Start by reading, in [the plan](SELF_HOSTING.md), "Start here" and all of "Current round: round 10":
its notes dated 2026-11-22 and 2026-11-23 list the measured state, the batches that are pushed but
not yet built into a stage 1, the decisions taken, and the next steps. Then read
`.claude/worktrees/step-f-round3/work/coord/rules.md` on the development machine before running any
compiler.

Rules that have not changed: at most two implementation workers, each in its own worktree and
`fix/*` branch; workers implement and run quick checks only; the coordinator runs every build,
suite and bootstrap under memory-capped scopes; fix the compiler, never a witness; pair stage-0
fixes with their stage-1 port; no development tags; never touch `main` history or `release/*`.
