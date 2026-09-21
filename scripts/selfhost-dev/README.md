# Self-hosting development tools

The scripts the self-hosting work is driven by: the landing gate, stage parity, memory and time measurement, and
the leak census. Each one is a minutes-long loop that would otherwise be a day of manual steps. The plan they
serve is in [SELF_HOSTING.md](../../docs/md/future/SELF_HOSTING.md).

Every script sources `env.sh`, which finds the repository root from its own location and works in a scratch
directory: `artifacts/selfhost-dev/` by default (ignored by git), or `ASHES_SELFHOST_SCRATCH`. The scratch
directory can be deleted at any time. Logs go to its `logs/`. The scripts print every exit status and abort on a
failed build, so a stale binary or a crash cannot pass for a result.

Run one heavy script at a time. Memory is capped with a cgroup (`systemd-run --user --scope -p MemoryMax=40G`),
never with `ulimit -v`. Self-hosted binaries run under `ulimit -s 1048576`: the compiler recurses deeply.

## The landing gate

| Script | What it does |
|---|---|
| `gatepr.sh [changed .ash files...]` | The gate for one pull request, run once: build, `dotnet format`, canonical `.ash` formatting, the six self-hosted suites, whole-program IR parity, `Ashes.Tests` (failing names listed), `Ashes.Lsp.Tests`, the end-to-end suite plain and under `ASHES_RC_POISON=1`. About 45 minutes. |
| `gatechanged.sh` | `gatepr.sh` over every `.ash` file that differs from `BASE` (default `HEAD`), after checking that regenerating the shared fixtures leaves them untouched. |
| `suites.sh` | The six self-hosted suites. |
| `suitetoggle.sh <suite> <ENV=value\|->...` | One suite, optionally compiled under an environment switch. Full output in `logs/suitetoggle.out`. |
| `suitebt.sh <suite>` | One suite compiled with debug information and run under gdb, stopped where the process exits: a failed assertion's backtrace. |

The self-hosted `cli` and `backend` suites are the only ones that run code stage 1 generated. Whole-program parity
and the semantics suite compare IR text, and have both been green while stage 1 miscompiled a standard library
function.

## Stage parity

| Script | What it does |
|---|---|
| `regenfixtures.sh` | Builds, regenerates the shared fixtures from stage 0 (`ASHES_UPDATE_PARITY_FIXTURES=1`), lists the fixture files that changed, and verifies. |
| `parityonly.sh` | Compiles and runs `selfhost/tests/ir-program-parity`; prints the first differing line of each program. |
| `s1diff.sh [-b] <fixture>...` | Stage 1's lowered IR of a shared fixture against stage 0's, locations stripped and temp, slot and label numbers normalised, so only the instructions that differ show. `-b` rebuilds the dump tool after a stage-1 edit; `SHOW=n` sets how much of each diff prints. |
| `c4loop.sh` | The inner loop of a stage-1 port: rebuild the dump tool, run whole-program parity, print the difference size of every program that still differs (`differing.sh`). |
| `rawdiff.sh <fixture>` | The same comparison with numbers kept: a numbering-only difference. |
| `s1dumpat.sh <commit> <source> <out>` | Stage 1's lowered IR of a source file as the lowering stood at a commit. With `normdiff.sh <a> <b>` this makes good-against-bad stage 1 a two-minute check. |
| `orphanfixtures.sh` | The lowered-IR fixtures the C# parity test does not regenerate, and whether stage 0 still reproduces each. There should be none. |
| `togglefix.sh <fixture> <pattern>` | Lowers a fixture with a published compiler under each `GRC_NO_*` switch; the switch that restores the old output names the change responsible. |

## Measurement

| Script | What it does |
|---|---|
| `publishcli.sh <label>` | Publishes this checkout's compiler as a self-contained binary into the scratch directory, for comparisons across branches. |
| `challenge_ab.sh <labelA> <cliA> <labelB> <cliB> [challenge...]` | Every `challenges/` program on its README workload with two compilers, three runs each, one at a time: best time, peak memory, exit status, output hash. |
| `quickstage1.sh <tag> [ENV=value]` | Builds stage 1 and tries it on two tiny programs, a seconds-long crash detector. |
| `probepeak.sh <stage1>...` | Stage 1 compiling the self-hosted code generator module to completion: wall time and peak memory. |
| `stage2.sh <stage1> <tag>` | The whole stage-2 build with memory samples. |
| `curryscale.sh <stage1> <k>...` | One `k`-parameter curried function per `k`: exponential growth means a body is lowered again at every nesting level. |
| `toggleab.sh <program>` | A program compiled under each `GRC_NO_*` switch and run under a memory cap: the switch that changes the exit status or peak memory names the mechanism. |

## Reproducers

A leak found in stage 1 is reduced to a program of a few dozen lines with the word `ROUNDS` where its repeat count
goes, and everything after that runs on the reproducer in seconds. A peak that stays flat as the rounds grow means
nothing leaks per round; the program's output must depend on the work, because a wrong number is how a
use-after-free shows when it does not crash. The templates of the first round of leak work are kept in
`reproducers/`, each named for its scenario and canonically formatted like any other `.ash` file (`ROUNDS` reads
as an identifier). Only these scripts run them; each scenario that stands for a fix is also an end-to-end test
under `tests/`.

| Script | What it does |
|---|---|
| `plateau.sh <template.ash> <rounds>...` | Compiles the template at each round count with this checkout's compiler and runs it under a memory cap: exit status, output, time, peak memory. `TOGGLE=NAME=1` compiles under one switch. |
| `plateauall.sh <small> <large> <template.ash>...` | `plateau.sh` for several templates at two round counts, plain and with `ASHES_RC_POISON=1`. |
| `plateauswitches.sh <template.ash> <rounds> <SWITCH>...` | The template with no switch and under each given switch, plain and poisoned: the rule a wrong result depends on. |
| `plateautoggles.sh <template.ash> <rounds>` | The same over every `GRC_NO_*` switch of the ownership contract. |
| `plateauold.sh <template.ash> <rounds>...` | With every placement rule added by the stage-1 leak work switched off: how the compiler behaved before it. |
| `publishref.sh <git-ref> <label>`, `plateauwith.sh <label> <template.ash> <rounds>...` | Publishes another commit's compiler (`origin/main`, say) from an exported tree, without touching this checkout, and runs a template with it. |
| `progcensus.sh <program.ash> [chunks]` | Runs any program under gdb to its exit and prints the reference-counted cells still live, by size and first word. Naming what leaks this way has beaten reading the lowering every time. |
| `irfn.sh <program.ash> <binding> [which] [pattern]` | Stage 0's lowered IR of one binding of a program, locations stripped, optionally only the lines matching a pattern. |
| `dumptoggles.sh <fixture> <SWITCH>...` | Builds the stage-1 dump tool under each switch and runs it on a fixture: which stage-0 rule makes the stage-1 binary crash. |
| `dumpbt.sh [-k] <fixture> [words]` | The dump tool with debug information under gdb: the faulting instruction and the functions whose return addresses are on the stack. `compile --project ... --emit-ir lowered` maps a `lambda_N` to the source binding it was lowered from. |
| `s1diffold.sh <fixture>...` | Stage 1 against the fixtures as stage 0 lowered them before the leak work, then as it lowers them now: a missing mirror of the new rules against an older gap. |
| `variants.sh <template.ash> <rounds> <name>=<sed-expression>...` | Shrinks a reproducer: each sed expression deletes or replaces one construct, and the variant runs through `plateau.sh`. |
| `progbt.sh <program.ash> [frames]`, `progwatch.sh <address-hex> [stops] [frames]` | A program built with debug information under gdb: the crash site, then every write to one word. A corrupted pointer is located with gdb's `find /g` over the reference-counted region first. An allocation at a misaligned address means a free-list link was decremented: some cell was released twice. |
| `optloop.sh [-b] <source.ash> <rounds>...` | The real self-hosted optimizer run N times over one lowered program (`projects/optloop`), in a fraction of a second: a peak that grows with the rounds is a leak inside the optimizer. `CENSUS=<rounds>` dumps the heap at exit for `tinyrootshape.sh` and `tinypeek.sh`; `WATCH=<cell> ROUNDS_WATCHED=<n>` after a `DEBUG=1 -b` build lists every write to a cell's count with source lines. Hand-written reproducers of a stage-1 leak kept diverging from the real code; importing the real package into a probe project did not. |
| `probeswitches.sh <SWITCH>...` | Builds stage 1 once per switch and runs the module probe with each: which stage-0 rule moves stage 1's peak. A rule that helps a probe can cost stage 1 elsewhere, and only this shows it. |
| `biggraph.sh <stage1> <seconds> <GB> [lines]`, `fulldump.sh` | A snapshot of the module probe's reference-counted heap at a moment: leaked states, then root classes with exclusive attribution. |

## Leak census

Compile a tiny input with stage 1, take a census of what is still live at exit, add one construct and diff the
census, then put a hardware watchpoint on one leaked cell's reference count and read which retain has no matching
release. Identical leaked objects in power-of-two multiplicities mean repeated work, not a missing release.

| Script | What it does |
|---|---|
| `mktinyinputs.sh <count>...` | Writes `tiny/loops<count>.ash`, that many copies of one recursive list function: two counts give the census a per-function difference. |
| `debugstage1.sh <tag>` | Stage 1 built with debug information, for `tinywatch.sh`. The census naming a cell must come from the same binary. |
| `tinyrootshape.sh <len> <elem> [tag]`, `tinypeek.sh <depth> <address-hex>...` | Over the last dump: the leaked roots of one shape tallied by nearby strings, and a cell printed as a tree of its words (`rcpeek.c`), which is how a root's type is recognised. |
| `tinyrun.sh <stage1> <src.ash>` | Compiles one small file with stage 1: exit status, elapsed time, peak memory. |
| `tinycensus.sh <stage1> <src.ash> [chunks]` | The same under gdb, stopped at process exit: dumps the reference-counted region and prints the size census and the leaked-state census. Everything live at exit is a leak. |
| `tinylist.sh <stage1> <src.ash> [last-n]` | The leaked states at exit in address order; the most recently allocated belong to the last things lowered. |
| `tinydiff.sh <stage1> <src.ash>...` | The leaked-state census for each of several small inputs, side by side. |
| `tinyrootdiff.sh <stage1> <small.ash> <larger.ash>` | Leaked list-head roots tallied by shape for two inputs; prints the shapes whose count differs. |
| `tinygraphdiff.sh <stage1> <small.ash> <larger.ash>` | Root classes with exclusive attribution for two inputs: root count and kilobytes kept per class, and the difference. |
| `tinywatch.sh <debug-stage1> <src.ash> <cell-hex>` | Every write to one cell's reference-count word, one line per event with the top frames. The run is deterministic under gdb, so an address from a census names the same cell here. |
| `bigwatch.sh dump\|watch ...` | The same two steps on the code generator module probe rather than a tiny input. |
| `rcstates.sh <snapshot-dir>...`, `mkrcroots.sh` | Build and run the census programs over a dump: leaked lowering states by constructor, and leaked list-head roots. |
| `*.c` | The census programs those scripts build: they read a dump of the reference-counted region, whose cells are `[count:8][size:8][payload]` from `0x100000000000`. |
