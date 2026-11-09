# 40_0 (round 11, W2, 11-08 19:42-20:40) - NAMED, not reproduced, no commit
- Census on s1-z0a (a9f9670f + nothing): 40_0 226 -> 230 (+4/fn). rcrefs 40 0 (refs-1{0,1}.txt): growth held by
  32_ptr@0 +1, 32_1@8 +2, 56_ptr@8 +1.
- W4's old lead (topLevelEscapeBody's owned slot in lowerCoreProgramItems) is GONE at the tip: stage 0's IR of the real
  function (dump.ir from `compile --project dumpir --emit-ir lowered`, lcpi.ir) releases slot 189's predecessor 190 at the
  back edge and both at the exit.
- Walked the loops11 dump (rcrefsl.c = rcrefs2 + LIST=1 referrer listing; raw.sh prints a cell's words from its header):
  the 32_1 "Some" cells are Some(TypeAt ...) (40_0 = TypeExpr tag 0 TypeAt), held by (Str, Maybe(TypeExpr)) pairs
  ("seen", "total"), held by an UNREFERENCED cons chain [(seen, Some(List(Str))), (total, Some(Int))]: the parser's
  sugar-parameter list of each walkN minus its first element (items). rcpeek misprints a cell whose first word is a
  small pointer byte as a 1-char string, read raw words.
- Producer: Parser.ash parserParseTopLevelBinding (lambda_767 in dump.ir, ptb.ir), `parserParseSugarParameters([])(afterName)`
  -> `(parameters, None, [], afterParameters)` at 3591:60. The tuple field normalization of `parameters` walks the list:
  an arena cell is copied; at the first RC cell it RcDups that suffix AND RcDrops the owner 388 (the whole original list,
  OwnerSlot=127). If the original's head is arena that drop releases nothing, so the RC suffix keeps its original
  reference plus the dup: exactly "tail of the list leaks, first cell gone".
- Reproducer p1/p2.tpl (work/z0; p2 = the same in an and-group) stay flat: their list is all RC, so the normalization
  takes the whole-list RcDup path. Need a list with an arena prefix and an RC suffix reaching that tuple (reverse of a
  list built by parserParseSugarParameters inside the big mutual Parser group; why its prefix is arena is open).
- Next: tinywatch the suffix cell (count word at cell-16) on a debug s1 to confirm the RcDup at 3591 is the unbalanced
  retain; then either the normalization's owner drop must release the shared suffix for an arena head, or the
  normalization must not dup a suffix the owner's reference already covers (transfer instead).

# 48_11 (W2, 11-08 19:56-20:15) - NAMED, stage-0 rule WIP UNCOMMITTED in the worktree (leak/32-0 branch base a9f9670f)
- 48_11 = SemFunction cells (3 per top-level function = each walkN's curried type), held via TypeScheme in CoreBinding
  in closeOuterBindings of leaked PendingTopLevelClose records (pendingBindingsRestore, one per `let recursive` item;
  72_ptr unreferenced 12 -> 13). Tools: rcgraph.c (all edges), up.sh, raw.sh.
- Stage 0 IR of lowerCoreProgramItems (lcpi.ir): the restore record is owned slot 307 (pred 308), consed into an arena
  cell for `pending`; back edge normalizes pending (RcDup of the record) but only clears 307's fresh flag, never releases.
  Cause (ASHES_DEBUG_BE probe, be2.txt): NoSuccessorCanHoldPartOf is blocked by arg 2 `seen: MapTree<Str,Bool>`, which
  "may hold" the record's Str parts; NoSuccessorNamesPartOf needs binders (none).
- WIP rule GRC_NO_ITERATIONSTRINGPARTS: fresh-path NoSuccessorCanHoldPartOf uses PredecessorPartTypes (strings
  excluded, as predecessors already do). dump3.ir confirms 307 is now released at the back edge.
- NOT DONE: reproducer (q1-q3.tpl in work/z0 stay flat), poisoned e2e subset, selfhost suites, stage-1 mirror,
  stage-1 build + census, template/e2e/plateau/doc. The work/z0 32_0 commit a9f9670f is already picked.

# Full exit tally (the round's census method from 11-08 23:52)
- rccensus2 (tinycensus.sh's table) prints only 25 rows whatever CENSUS_LINES says, so its totals are truncated.
- Recipe: `bash work/q40/fulltally.sh <stage1-binary> <tag>` (heavy: wrap in heavy2.sh + systemd-run MemoryMax=16G).
  It runs tinycensus.sh on artifacts/selfhost-dev/tiny/loops10.ash and loops11.ash (dumps copied to
  work/q40/dump-<tag>-{10,11}), tallies every live RC cell with `CELLS=1 work/q40/rccells 0 0 <dump>/full-*.bin`
  by size_firstword (first word a pointer -> "ptr"), and writes work/q40/tally-<tag>-diff.txt.
- Report both, labelled: NET = live(loops11) - live(loops10), GROWTH = sum of positive class deltas. s1-m12a: NET +69/fn (6897 -> 6966), GROWTH +81/fn (round target NET 0, no class growing). Before: "growth of growing classes" = sum of positive deltas (+81/fn on s1-m12a, built at aa339ac9 with
  `quickstage1.sh m12a ASHES_RC_LEAKCHECK=1`). A class whose size follows the input length (strings like 72_3792)
  shows up on one input only; that is not a one-size leak class.
- Build rccells if missing: `gcc -O2 -o work/q40/rccells work/q40/rccells.c`.
- 11-09 00:50: fulltally.sh no longer uses tinycensus.sh. That script dumps into the worktree's shared $T/tiny-dump
  (and $T/tinycensus.gdb, $J/tiny.out), so two runs of my fulltally.sh from any worktrees used the SAME dump dir and
  could read each other's dumps. Now each run dumps into its own work/q40/run-<tag>-<pid>/ and prints the binary path.
  Tallies made with the old script while another run was active are suspect: re-run them.

# 40_9 real class (W2, 11-09 01:06-)
- Fast reproducer of the real leak without building stage 1: work/q41/dl (the dump-tool project: stage 1's lowering of a file,
  stitched with lib/Ashes, compiled by stage 0 with the leak counter). `bash work/q41/dlrun.sh <tag>` rebuilds it from the
  current semantics source (3 min) and full-tallies loops10/11 under it: NET +29/fn, 40_9 +4/fn, 32_ptr +6, 32_4 +4. So the
  optimizer is not needed; a stage-0 switch can be tried on the real code in 3 minutes.
- Debug build dlg.bin; watch of a leaked "lambda_64" (count 1, unreferenced): allocated in a runtime helper with no frames
  (?? = probably the RC string copy-out), first retained by returnedClosureLabelOf reading a MakeClosure label
  (finishLambdaBody -> recordReturnedClosureLabel). Diagnostic only, reverted: binding lowerLambdaBody's ten fresh
  "lambda_" + fromInt(lambdaId) arguments to one let changes nothing (40_9 still +4), so it is not those concats; most
  likely the leaked cell is an RC COPY of an arena label made when an instruction or state is normalized.
- Stage-0 copies m1/m2 (work/z0) of the finishLambdaBody shape stay flat.
- 11-09 03:2x (tip ee0f9d1b, dl): at the leaked label's LAST event (2 -> 1, dropping a (Str, Str) entry of
  functionReturnedClosureLabels at lowerFunctionBodyResolvingCalls), only two words in all writable memory hold the
  label: tuple 0x100000248120 field 1 (the entry being dropped) and 0x100000242508, the label word of a MakeClosure (CORRECTED below)
  instruction kind [6, label, 0, 0x18, ...] whose "header" is not an RC header (an arena cell). After that the label
  gets no event and at exit no word anywhere holds it: its last reference is held by an ARENA copy of the MakeClosure
  instruction that took the RC label by move, and the arena is reset under it. dl dumps: work/q41/dlg2 (h-*.bin at the
  last event, a1/a2/rc.bin at exit), finder work/q41/findptr.c.
- CORRECTION 03:33: the MakeClosure kind cell at 0x1000002424e8 is an RC cell (size 88) already FREED (count word = free link) while its label word still holds the label: it was freed WITHOUT releasing its label (a shallow free of an instruction kind), a local bug, not an arena move.
- 11-09 03:4x NAMED (dl, tip ee0f9d1b): the unbalanced reference is the retain returnedClosureLabelOf's `Some(label)`
  takes. In stage 0's IR (work/q41/dl/ret.ir, lambda_10064, CoreLowering ~3530-3540) the TCO loop matches
  `IrInstruction { instruction = MakeClosure(target, label, ...) } :: rest`, RcDups `label` (pattern owner), then for
  `Some(label)` RcDups it again, releases the pattern owner, and builds `Some` with AllocAdt Tag=1 in the ARENA, returned
  raw (`Return` of the arena cell, no copy-out). The caller recordReturnedClosureLabel copies `returned` into the RC
  tuple (another retain); the arena Some's retain is never released. Label count trace: K1 1, owner +1, Some +1,
  owner -1, tuple +1, K1 -1, tuple -1 -> 1 = the arena Some's. This is the arena-result-holds-a-handed-over-reference
  class (option c): a loop's arena result cell holding a retained part of its parameter, settled only by the caller's
  copy. Same shape as the 32_8 rule (GRC_NO_LOOPRESULTCELLOWNERDUP), but for a PART of the parameter and with no
  copy-out at this function's return, so that rule's owned slot is not available.
