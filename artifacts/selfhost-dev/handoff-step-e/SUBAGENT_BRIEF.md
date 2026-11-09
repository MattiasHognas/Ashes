# Brief shared by the step-E leak-fix subagents

You are fixing reference-counting leaks in the Ashes compiler. Ashes is a pure functional ML-family
language; stage 0 is the C#/.NET 10 compiler (src/Ashes.*), stage 1 is the self-hosted compiler written
in Ashes (selfhost/packages/*), compiled by stage 0. Read CLAUDE.md in your worktree first, then
docs/md/internals/architecture.md "Memory model" and "Borrowed parameters, owned results", and
docs/md/future/SELF_HOSTING.md step E (the last paragraphs, "The leak after #1139, named and ranked" and
"One stage-0 fix was withdrawn").

## Setup
- `cp -r /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/runtimes/. ./runtimes/`
- Create your branch from commit `e1a24915` (named in your task) and check it out. Commit locally only:
  never push, never open a PR, never touch other branches. The commit-msg hook rejects Co-Authored-By
  and Claude-Session lines.
- `dotnet build -c Release src/Ashes.Cli` after every stage-0 change (the tools use this build).
- Memory: running too much at once on this 60 GB machine has killed the whole Claude session twice.
  Up to two workers run at once, and heavy commands share three machine-wide slots (third opened by Mattias 2026-11-03 20:57):
  every heavy command (stage-1 build, suites, probe, census, e2e, C# test runs) runs as
  `bash /home/mattias/.claude/jobs/heavy2.sh systemd-run --user --scope -q -p MemoryMax=<cap> -p MemorySwapMax=0 <command>`
  (heavy2.sh takes whichever of the three slots is free and sets the build-server variables below)
  with the smallest cap that works (16G for a stage-1 build, suites.sh or parity (parity peaks near 8.7 GB), 8G for censuses, 4G for a
  single program). Waiting for a slot is expected; editing, reading and reasoning need none.
  Export `UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0` before any
  `dotnet build` under the lock: a leftover Roslyn/MSBuild server otherwise holds the lock for minutes.
  Never `ulimit -v`. Stage-1 binaries need `ulimit -s 1048576`.
- EVERY compiled program you run, including a tiny reproducer in a rounds loop, runs under a cap:
  `systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 timeout 120 <binary>` (no slot
  needed for a single small program). A use-after-free can turn a reproducer into
  an unbounded allocator: on 2026-10-25 an uncapped reproducer binary (t11_1, a pre-existing stage-0 UAF
  in List.reverse over a 3-cons loop push) grew to 51 GB in under a minute, twice, and took down every session. Run
  nothing heavy outside `systemd-run ... -p MemoryMax=`: uncapped processes share the Claude app's own
  cgroup (now capped at 28 GB RAM + 4 GB swap, so the kernel kills the largest process in it).
- Build stage 1 only when a commit changes stage-1 sources (selfhost/) or you need a stage-1 measurement;
  a commit that changes only stage 0, tests or census templates is checked without a stage-1 build.
- Measure peak memory: run heavy commands under `/usr/bin/time -v` and note "Maximum resident set
  size". A spike (a stage-1 build over 15 GB, where 13-14 GB is its normal peak; a compiled test program over 1 GB, anything growing
  without bound) is a bug: kill it, don't raise the cap, and report the command and the number.
  A watchdog (artifacts/selfhost-dev/handoff-step-e/tools/memwatch.sh, user unit `ashes-memwatch`)
  kills the largest process of this user (RSS + swap, any name, never Claude or the desktop) when
  available memory falls below 10 GB, swap use passes 6 GB, or one process passes 24 GB; if one of your jobs dies with SIGKILL, check
  artifacts/selfhost-dev/handoff-step-e/work/memwatch.log in the stage1-leak-free worktree.
- Never leave `tail -f` watchers behind; wait on a log with an `until grep -q ...` loop.
- The Bash tool refuses complex constructs (loops over variables inside one command, `$(...)` passed into
  commands, variables inside sed programs, `sed -f`, awk programs it cannot read): put logic in a
  script file and run `bash script`.
- Style: explicit C# types, ordinal string comparisons, methods at most 60 lines (MA0051; warnings
  are errors), no Python, no emojis, comments describe code (not history, no task IDs). `.ash`
  changes are formatted with `dotnet run -c Release --no-build --project src/Ashes.Cli -- fmt <file> -w`.

## How leaks are measured
- General RC is OFF for a whole program only when some function may run under a pending one-shot post
  (ProgramMayRunUnderLiveHandlerPost: a `handle` whose arm resumes in a non-tail position). Stage 1 compiles its own code with
  general RC ON (the eef21fd1 dump has ~53k general_rc_owned labels), so reproducers of stage-1 shapes need NO handler; adding
  a non-tail-resuming handle switches general RC off and tests a different path (a real leak, but not stage 1's). Corrected
  2026-11-03 01:5x after a handler-based check had been adopted by mistake.
- A flat single-function reproducer can also hide cross-function state: Lowering keeps some per-function sets keyed by temp or
  slot numbers that are never cleared between functions (2026-11-03: _releasedPatternOwnerDuplicates and
  _patternOwnerAggregateDuplicates), so the same shape leaks only after an earlier function used the same numbers. Put an
  identical copy of the function before the one under test.
- `ASHES_RC_LEAKCHECK=1` at compile time makes a program print `ashes-rc-live N` on stderr at its
  end. A ROUNDS template compiled at 1 and 2 rounds leaks exactly the difference.
- `dotnet run -c Release --no-build --project src/Ashes.Fuzzing -- leak-census --seeds 3
  [--templates <dir>] --artifacts <dir>` runs every combination template once per seed; the
  `leak.*` templates (src/Ashes.Fuzzing/Combinations/Leak*CombinationTemplates.cs) generate each
  known leak class. Your fix is done when your classes' `leak.*` templates report `ok` on every seed
  (also try 5 more seeds with `--seed 1000 --seeds 5`) and their hand-written reproducers report no
  growth. Reproducers: /home/mattias/Documents/source/Ashes/.claude/worktrees/stage1-leak-free/artifacts/selfhost-dev/handoff-step-e/census-templates/
  (copy what you need; the last census log is ../work/census-run1.log next to it).
- Stage 1's own memory: `bash scripts/selfhost-dev/quickstage1.sh <tag>` builds stage 1 at
  artifacts/selfhost-dev/s1-<tag>; `bash scripts/selfhost-dev/probepeak.sh <s1-before> <s1-after>` is
  the module probe (normally ends exit=1 MissingCoreTraitEvidence; compare peak MB and seconds).
- scripts/selfhost-dev/README.md lists the other tools: `s1diffprog.sh -b` (stage 1 vs stage 0
  lowered IR of a stdlib-free program), `s1survey.sh -b`, `regenfixtures.sh`, `parityonly.sh`,
  `suites.sh`, `plateau.sh`, `progcensus2.sh`, `tinywatch.sh`.

## Rules
- Narrow before fixing: reduce, read stage 0's `--emit-ir lowered`, find the rule, fix the rule.
- The mirror rule: a stage-0 lowering change ships with its stage-1 counterpart in the same commit
  (selfhost/packages/semantics/src/AshesCompiler/Semantics/CoreLowering.ash and neighbours), unless
  stage 1 demonstrably handles the shape already (show it: compile the reproducer with your stage 1
  and run it at two round counts; stage 1's backend has no leak counter, so compare peak RSS at 1,000
  and 100,000 rounds). A stage-1 source change that removes a leak from stage 1's own code (rewriting
  a stage-1 function) is also welcome where the compiler fix is a design job, but the compiler fix is
  the goal.
- A release must follow the retain it balances. Two fixes failed today by breaking that: releasing a
  record-update successor's children like a literal's (exposed a latent over-release of borrowed
  literal children; reproducer scripts/selfhost-dev/reproducers/borrowed_child_of_literal_successor_masked_by_update_leak.ash
  in the stage1-leak-free worktree), and skipping a loop-parameter retain for arena contract cells
  (crashed stage 1's linker: the cell was nested inside a generic `Ok`). A fix that crashes stage 1
  (`quickstage1.sh` smoke programs fail) or any suite is not done.

## Validation before you commit (minimal; the coordinator runs the suites, parity and the full gate)
The user's rule for step E: per fix, only the minimal checks below, so the work moves fast. The
coordinator runs the six self-hosted suites and whole-program parity on the integration branch after
cherry-picking your commits, and the full gate (C# tests, e2e plain and poisoned, full census) once at
the end of E. Do NOT run `suites.sh`, `parityonly.sh`, the whole C# suite, the whole e2e suite, or a
full-seed census yourself; if the coordinator's run fails on your commit, it comes back to you with the
failure.
1. `dotnet build Ashes.slnx` clean; `dotnet format Ashes.slnx --verify-no-changes`.
2. Your classes clean: `leak-census --only <your templates> --seeds 3 --stage1 <s1> --poison`, plus
   your reproducers at 1 and 2 rounds plain and with `ASHES_RC_POISON=1` (and under stage 1's leak
   counter when the fix has a stage-1 side).
3. `bash scripts/selfhost-dev/regenfixtures.sh` (cheap; list changed fixture files and say why).
4. A new or changed leak template: run the whole `src/Ashes.Fuzzing.Tests` project (fast; it checks every
   template varies across seeds). Your own new tests only: the new e2e file (`test tests/<file>.ash`) and the new C# test class
   member (`--treenode-filter`), each shown failing without the fix.
5. Probe before/after once at hand-back, only when stage 1's own memory is the point of the task.
6. Add a test per fix: an e2e test under tests/ (named for the scenario, `// expect:`, `fmt -w`) and,
   for a leak, a C# test in src/Ashes.Tests/LinuxBackendCoverageTests.cs that fails without the fix
   (a plateau test, or an IR-shape test when the program's memory is dominated by something else).
7. One commit per fix, concise imperative subject, body with the rule and the numbers.
A stage-1 build (`quickstage1.sh`) is still needed for the `--stage1` column and the stage-1 leak
counter; build it once per task, not per commit, unless a later commit changes stage 1.

## Report (final message)
Branch, commit hashes, worktree path; per class: the root cause (the rule and the IR), the fix, the
stage-1 counterpart or proof it needs none, census before/after, validation results with counts,
probe lines; what you could not fix and why.
- Keep your thread's progress checklist current and visible at every step change (current item, time box, what is running), not only at hand-back (Mattias 2026-11-03).
- A rule that emits reference-counting IR at every call site of a common shape is unaffordable twice over (W3, 2026-11-07):
  inline on every curried call it pushed stage 0 compiling stage 1 from 14.6-15 GB to the 16G cap (OOM-killed), and even as
  one call per site into a synthesized helper it changed 145 lowered-IR parity fixtures that stage 1 would have had to mirror
  instruction for instruction. Gate such a rule on compiler knowledge of the callee so `regenfixtures.sh` changes nothing, and
  before committing build stage 1 under the cap with `/usr/bin/time -v` INSIDE systemd-run (outside it sees only the wrapper);
  `journalctl --user | grep "run-.*memory peak"` gives the peak of a scope the cap killed.
