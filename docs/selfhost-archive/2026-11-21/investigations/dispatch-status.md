# Dispatch leak investigation

- Branch leak/step-e-dispatch-analysis, clean base 92a10ae8.
- Scope 32_28/32_29 only. Parent coordinates global heavy-job slots; no runtime started yet.
- W4 confirms RcDup 133 on nestedVisited from owned tuple into dispatch slot 5; slot arena-placed,
  no predecessor/exit owner release. Skipping dup would dangle after tuple release.
- Source inspection: AccumulatorRebuild admits passthrough, GrownCons, and call-result-part edges.
  IsGeneralRcAccumulatorList gates this fact on IsCallResultAccumulatorElement, which excludes Int.
  FreshOrHandedOnListRebuild admits call parts and passthrough, but rejects cons-onto-parameter.
  Thus the dispatch graph's combination of cons and call-part edges falls between both gates.
  The ordinary-loop a3 lacks the cons edge, explaining its flatness without a dispatch-specific rule.
- Need fresh exact runtime/watchpoint evidence before code edits. baseline.sh prepared but NOT RUN.
- Candidate direction after evidence: admit proven scalar-element accumulator combination using
  existing AccumulatorRebuild facts, behind new switch, preserving predecessor and return handling.
  Do not indiscriminately RC-place all Mixed dispatch slots. Check aliasing/borrowed negatives.

## Baseline and candidate

- Parent granted slot2 for baseline/watchpoint, then slot released after completion.
- Current baseline at 2/10 rounds, plain and poison identical: a2 19/91, a3 1/1,
  s4 1/1, d 5/21, k 3/11, neg 1/1, neg2 1/1. Peak compile RSS 186432 KiB.
- a2 debug final IR reproduces RcDup133 on tuple field nestedVisited followed by StoreLocal slot5,
  then tuple drop. Hardware watches on leaked 32_28 headers 0x100000001010 and 0x1000000010f0
  each record only allocation to count1, no decrement before natural exit at live19.
  0x10f0 allocation is lambda_13 at a2:18 through lambda_19 dispatch at a2:27.
- Candidate source prepared, UNTESTED/UNCOMMITTED: existing IsGeneralRcAccumulatorList admits
  scalar elements under GRC_NO_SCALARACCUMULATOR, retaining all existing structural/exclusion gates.
  CanArenaReset erases zero-cost wrappers and admits scalar words plus non-resource opaque handles;
  it does not admit arbitrary tuple/ADT/list aggregates. Architecture note added.
- Stage1 currently has IrCodegen.LeakCheck plus CLI leakCheckRequested, despite stale W4 notes.
  Exemption therefore must be measured with exact live-cell counts, not RSS alone.
- Awaiting next parent-granted slot for own 8G build and candidate probes. No build started.

## Candidate result and revised diagnosis

- Candidate sequence completed; slot2 released. Release build succeeded, peak RSS 1307884 KiB.
- Candidate did not change any baseline counts. Plain and poison both pass but a2 remains19/91,
  d5/21 and k3/11. GRC_NO_SCALARACCUMULATOR gives the same a2 counts.
- Current stage1 generated-program counters are31/151 at2/10 rounds, stdout387/1940.
  Stage1 exemption is disproven; mirror diagnosis is required. Compiler counters are not these values.
- Source candidate and architecture paragraph remain UNCOMMITTED experimental evidence, NOT a fix.
- Earlier prerequisite found in source: BuildDispatchLambda introduces `let visited = dispatchArg`.
  The structural walker records this in letValues, but never maps visited into parameterScope.
  ClassifyTcoSelfCallArgument checks the ORIGINAL argument name for passthrough, and a cons tail
  against parameterScope; both visited and cell::visited consequently become Mixed. IsAccumulatorEdge
  rejects these, so the all-edge AccumulatorRebuild proof fails before scalar element eligibility.
- Proposed targeted instrumentation (not yet run): synthesized dispatch only, print each ordinal,
  argument, structural expression, local shape, accumulatorEdge and parameterScope; print final
  membership and TCO exclusions. Requires another8G build and one a2 compile under parent slot grant.

## Alias diagnostic and combined candidate

- Parent granted diagnostic slot; slot released after successful build/compile.
- Two preliminary builds rejected tracing additions by MA0051. Extracted trace helpers; no analyzer
  disabled. Third build clean, peak1390076KiB. All logs retained in dispatch-trace{,2,3}/.
- dispatch-trace3/compile.stderr records ordinal4/slot5: GeneralRc=True, MixedList=True, Tmc=False,
  Consumed=False, Invariant=False, Scalar=True, AccumulatorMember=False, Eligible=False.
- Its four edges: cell::visited Mixed/False; visited Mixed/False; nestedVisited Mixed/True;
  visited Mixed/False. The parameterScope only contains synthetic argument names. The visited
  structural expression correctly resolves to __recgroup_arg3, but original-name shape still fails.
- Saved scalar-only candidate plus diagnostic patch in scalar-only-plus-diagnostic.patch.
- Combined candidate PREPARED, NOT BUILT: BindDispatchParameterAlias extends lexical parameterScope
  only for synthesized dispatch and direct Expr.Var aliases of already mapped parameters. Source
  binding resolved before replacing destination; otherwise binding name removed. New switch
  GRC_NO_DISPATCHPARAMALIAS. Existing scalar eligibility candidate remains separately switchable.
- Planned matrix: combined on, alias off, scalar off; a2/d/k2/10 plain+poison; existing a3/s4/neg/neg2;
  additional direct-alias lexical-shadowing negatives before acceptance. Stage1 still requires mirror.

## Combined candidate rejected

- Granted slot1 matrix completed only a2/a3/s4, then d2 plain crashed (exit139). Slot released.
- a2 normal+poison improved19/91 ->15/71 at2/10, not flat. a3/s4 stayed1/1.
- Matrix stopped at first crash: d2 poison, k, negatives, switch-off and final trace NOT RUN.
- Saved full patch in failed-alias-candidate.patch and result log in failed-alias-candidate-results.log.
- Reverted all THREE tracked files to HEAD by writing git-show contents. Worktree is clean.
  No files/evidence deleted. Own Release CLI still contains FAILED candidate until next rebuild;
  DO NOT use it as baseline. Root immutable CLI remains untouched.
- Crash indicates propagating alias facts changes more than accumulator membership: existing
  affine-cons/consumed-tail/fresh-list rules can now activate on synthesized slots. Need exact
  crash backtrace and final IR before any narrower candidate. No unproven release accepted.

## Crash diagnostics

- Parent granted one4G gdb run and one8G existing-candidate compile, no rebuild; slot released.
- d-crash.log: SIGSEGV0x402a72 `mov -0x10(%rdi),%rax`, rdi=7, after `mov (%rbx),%rdi`,
  rbx=0x100000001080. Release interprets scalar head7 as heap pointer. Stripped stack is
  0x402a72 ->0x7 ->0x0; exact source/IR drop not yet attributed. Continued to natural SIGSEGV exit.
- d-candidate-trace.stderr: alias propagation activates ordinal2/slot6 walked/refilled rule,
  ordinal4/slot5 scalar accumulator, AND ordinal5/slot1 List((Int,Int)) existing accumulator rule.
  Therefore global alias facts change multiple placement decisions, not only target visited slot.
- Possible future direction (NOT IMPLEMENTED): separate alias-aware accumulator proof, consumed only
  for scalar/call-result-part placement; preserve existing global shape facts. Exact crashing drop
  still needs symbolized evidence first; no additional run authorized or started.

## Isolated proof candidate prepared

- Symbolized failed d2 reproduces crash in storedValuesOf/lambda_4 line11. Active tuple-head drop
  is rcdrop_list_235: predecessor instructions Temp24 ->Slot41 ->Temp136 ->head Temp140,
  RcDrop Temp140 TypeName=Tuple. Earlier tuple drop on same edge is guarded constant0; scalar
  list drop does not traverse heads. Crash loads scalar7 as the tuple's RC header.
- New candidate computes a separate proof using fresh TcoParamFactsState and fresh lexical maps;
  default shared structural summaries and classifications remain unchanged. New partial file
  Lowering.DispatchScalarCallPart.cs. Gate GRC_NO_DISPATCHSCALARCALLPART.
- Requires known full-arity call with RcEligible result provenance and direct tuple variable fields,
  at least one tuple-part edge, all successors parameter passthrough/grown-cons/tuple-part.
  Lexical immutable aliases resolved only in this private proof. Layout gate scalar/no-owned-child;
  consumed, invariant and TMC excluded. No heap-element accumulator widening.
- d2 has no tuple-part edge, so all3 slots previously activated stay unchanged. a2 tuple-list
  slot cannot pass scalar layout; walked/refilled slot has no part edge. Expected target visited
  slot only, pending actual proof membership/counts. No runtime executed for this candidate yet.

## Isolated candidate result

- Parent granted slot2. Build succeeded peak1304464KiB; all7 reproducers plain+poison2/10 safe.
- Counts identical to baseline, including a219/91 ON and OFF; d5/21 now safe. Slot released.
- RegisterSynthesizedLoopFunction has exactly one caller: CreateDispatchTcoContext TopLevel.cs1178.
  Dedicated proof thus runs only for mutual dispatch; other generated loops cannot enter it.
- Preparing diagnostic of private per-call scope/arity/summary/provenance and per-ordinal edges.
  No guard relaxed. ASHES_TRACE_DISPATCH_CALLPART remains disabled by default.
- RcEligible means allocation eligibility, NOT proof of actual RC representation; local variable
  named rcEligibleResult accordingly. Baseline target has actual retained-field IR evidence, but
  general release acceptance must account for actual owned transfer/normalization as well.
- Failed compiler preserved intact under failed-candidate-cli before rebuild; hash recorded.

## Actual-owned-result gate (prepared, unbuilt)

- Private diagnostic completed: withdrawCellRetains args4 exactArity=True hasSummary=True,
  RcEligibleResult=False. No tuple-part was admitted, explaining unchanged counts.
- Timing: build10.10s wall94.26s CPU1350204KiB exit0; compile0.77s wall0.80s CPU188056KiB exit0.
  .time files preserved separately in isolated-trace/; slot released.
- New candidate replaces static eligibility gate with actual representation evidence: private proof
  stores required direct known/full-arity tuple-call Expr identities. LowerMatch records a site only
  when its lowered type is TTuple and IsRuntimeManagedResultTemp(temp) is true (the existing match
  ownership predicate). Every required site must be observed; unreachable/unobserved sites fail closed.
- PostBodyRefresh in LowerLambdaCoreFinalizeTcoOwnership reevaluates placement before entry ownership
  splicing, deferred back-edge lowering and exit drops. Earlier note suggesting RecomputeTcoParameterFlow
  was after body was incorrect; that method is pre-body. PostBodyRefresh is the relevant late step.
- Required/observed sets are private/reference-keyed per function; global structural summary unchanged.
- owned-call-candidate-check.sh prepared. Each build/compile/program writes separate /usr/bin/time-v
  output with wall/CPU/RSS/status, keeping leakcheck stderr clean. Awaiting next parent slot grant.

## Actual-owned-result candidate rejected

- Initial build hit existing method statement/line limits; extracted helper functions, no rule disabled.
- Rebuild clean:21.47s wall194.08s CPU1399756KiB exit0. a2 compile0.79s wall0.81s CPU179440KiB exit0.
- First a2_2 plain executable SIGSEGV (shell139),0.02s wall,0.00s CPU,8204KiB. Matrix aborted.
  GNU time says signal11 but misleadingly prints Exit status0; shell/process signal is authoritative.
- Slot released directly to32_3 and root notified. No poison/OFF/other probes run.
- Full failed compiler retained at failed-owned-call-cli/, patch at failed-owned-call-candidate.patch,
  new partial copied/moved into work/l28. Tracked source restored to HEAD; git status clean.
- Actual tuple ownership fact alone was insufficient for safe late scalar-slot placement. Need
  symbolized a2 crash and exact IR/drop before any further change. No release accepted.

## First watched return head did not prove invalid release

- owned-call-first-drop.gdb breaks at4135dc (dispatch tuple return line20), watches visited header
  0x1000000010f0. At tuple construction count2; exit drop2->1, caller retains and later releases;
  freed by withdrawCells line31, then reused next round. No direct invalid release established.
- gdb0.11s wall0.07s CPU66580KiB exit0; inferior naturalSIGSEGV. Slot released.
- Fault at411fe6 is allocator free-list pop, not copy source read: rcx=0x100000000028/bin32 head,
  rax=1 invalid next pointer. Need identify cell that inserted1 in free list, then watch its lifetime.
- Immediate-return-dangling hypothesis is unproven; no further candidate edits authorized or made.

## Return alias marker candidate rejected

- Original a2 now safe plain+poison, but counts21/101 vs baseline19/91 at2/10 rounds: extra retained
  arena children leak (+1 per round). Placement-OFF restores19/91. All other7-original cases baseline.
- First new alias-chain variant2 rounds plain SIGSEGV139. Matrix stopped; shadow/copy variants unrun.
- Build20.50s wall168.19s CPU1288056KiB exit0. Failing run0.02s wall0.00CPU8204KiB signal11.
- Source reverted clean. Complete compiler archived failed-return-alias-cli; diff and new partial
  saved failed-return-alias-candidate.patch and failed-return-alias-DispatchScalarCallPart.cs.
- Slot released to root. No further runtime. Marker alone fixes a2 lifetime but does not establish
  balanced ownership of escaping arena tuple fields. Alias-chain failure needs exact source/IR
  diagnosis before further changes. Existing semantic baseline remains unchanged.

## Return ownership contract diagnosis and whole-result candidate

- Alias-chain candidate OFF debug program returns387/live19, ON crashes allocator with free-list next1.
- Original a2 watchpoint head29/header0x1130 proves added return retain1->2, exit2->1,
  caller1->2 then2->1: arena shell retains a stranded owner through normal exitlive21.
- Existing MovesOwnedPartsIntoTupleResult sees only general owned slots, not TCO-owned slots.
  TCO exit transfer compares whole result pointer, not tuple fields. Tuple-parts contract applies
  unresolved layouts only. RefreshRuntimeManagedTcoResult does not promote an arena tuple shell.
- New isolated candidate uses private proof with resolved owned tuple + requested-arena conversion
  support gates, then existing whole-result deep normalization when slot reach finds private RC slot.
  No return alias retain. Compiled ownership fact and requested-arena conversion reviewed.
- First build rejected MA0051 (61-line method); condition compacted. Separate candidate2 build passed:
 20.58s wall166.53s CPU1263704KiB exit0.
- Full targeted original7+alias/shadow/copy plain/poison2/10 matrix passes without crash, but a2 remains
 19/91 ON and OFF: no improvement. Alias-chain/saved-shadow/copied19/91; fresh-shadow17/81.
- Slot released. Guarded diagnostic prepared to identify exact conservative result gate rejection;
  no behavior broadening. Candidate uncommitted and isolated; not accepted as a fix.
- Correction after emitted IR review: result gates pass at post-body refresh; private slot5 DOES
  activate. lambda19 copies whole result into RC Alloc201 and retains visited into field8 viaDup233,
  then drops slot5. lambda18 MakeClosure has ReturnsRuntimeManaged=true; lambda23 wrapper forwards
  owned result and emits requested-arena copy/drop. Total19/91 does not establish class unchanged.
- Trace build9.59s wall92.42s CPU1412412KiB, compile0.82s wall0.83s CPU182188KiB, exits0.
  Evidence whole-result-trace/compile.stderr. Slot released; requested existing-binary exit dump
  and immutable scanner to compare remaining live cells against baseline before more code changes.

## Exact tuple owner and narrowed adoption correction (current candidate)

- Whole-result candidate still19/91. Watched header0x10d0 is tuple(instructions,visited):
  lambda13 alloc1 at0x40c46d; lambda9 postcall normalization RcDup38 Source36 at0x406959
  increments1->2; dispatch tupledrop at0x415f4a decrements2->1. Original tuple owns the surviving
  visited tail and instructions. Evidence whole-result-trace/watch-owner.log and compile.stderr.
- Broad dynamic tuple normalization initially improved a2 to13/61 but introduced a regression in
  dispatch_min.tpl: baseline1/1 vs ON8/36. Private snapshot of actual lowered bodyType (before
  PostBodyRefresh, without changing global inference order) fixed default but tuple rule alone
  still leaked. Peer step_e_32_3 correctly rejected broad eligibility: other consumers might be
  outside private placement. Archived broad-tuple-candidate.patch, broad-tuple-DispatchScalarCallPart.cs,
  broad-tuple-CoreLowering.ash and full broad-tuple-cli before replacing broad behavior.
- CURRENT narrow rule captures separate ownedTupleResultFlagTemp before final application and
  threads it through LowerCallApplyArgs/LowerCallFinish. It is used ONLY within the existing
  OwnResultBeforeArgumentRelease gate (preserved heap consumed argument, ownable type, not already
  settled). Flag true adopts returned tuple as-is; false uses original copy/retain. Standard call
  ownership flags, arena restore, accessor classification and argument transfer behavior unchanged.
- IsNormalizableOwnedTupleResult requires GeneralRcEnabled, new GRC_NO_OWNEDTUPLECALLRESULT enabled,
  resolved owned tuple layout, existing CanOwnResultBeforeArgumentRelease. Capturing flag alone
  creates no ownership. Broad ResultOwnershipReadAtRunTime/LowerUncoveredCallResultCopyOut edits removed.
- Private dispatch rule remains GRC_NO_DISPATCHSCALARCALLPART: own separate structural state,
  actual owned call-tuple observations, scalar elements, exclusions for TMC/consumed/invariant,
  resolved actual body layout snapshot, whole-result normalization before TCO exit. No shared
  structural alias facts or general inference timing changes.
- Narrow candidate a2 plain/poison2/10:13/61 vs baseline19/91. Full original7 + alias-chain,
  saved-shadow, fresh-shadow, copied variants all safe and correct output. Artifacts obligation-candidate/.
- Minimized dispatch_min.tpl ON1/1, bothOFF1/1; dispatchOFF1/1; tuple-callOFF1/1; poison1/1.
  This is the key negative regression that rejected broad normalization. Existing stage1 f-bootstrap2
  also1/1 for this minimized case (measured earlier), but original a2 stage1 remains31/151.
- Exact CURRENT watch obligation-trace/watch.log: tuple0x10d0 alloc1 in lambda13, dispatcher
  releases1->0 at0x41607c. No lambda9 extra retain. A reused returned tuple also1->0 in outer caller.
- Remaining a2 live cells13 =2rounds*(4 instruction tuple/list cells +2 storedValuesOf temporary
  scalar lists)+constant1. Original visited chain/nested tuple gone; tags32_28/32_29 still name
  instruction tuples, so class-name counts alone cannot prove real compiler class fixed.
- Candidate build20.78s wall162.36s CPU1397820KiB exit0. Final debug compile0.58s wall0.60CPU
  184212KiB; watch0.09s wall0.07CPU66568KiB exits0. All timings separate from leakcheck stderr.
- Narrow stage1 mirror prepared/formatted: separate CoreCallStage.ownedTupleResultFlagTemp,
  capture at final application, use only existing ownResultBeforeArgumentRelease with conditional
  adoption helper. No broad call-window changes remain. UNBUILT and unvalidated; full stage1 build
  deferred explicitly by root/user prioritizing Step F stage2 performance.
- Source uncommitted; no remote/integration. No permanent templates/plateau/e2e/architecture note
  yet: current minimized template is a regression negative (already flat baseline); need a flat
  positive reproducer before registering LAST. a2 still has unrelated leaks so cannot assert flat.
- Additional dispatch_owned_min.tpl experiment (addOne helper)13/61 bothON/OFF, does not isolate
  target; preserve as negative evidence, do not use as passing flat template.
- Next when resumed: peer-review narrow boundary, stage1 mirror build+2count evidence, real-code
  probe of visited class, permanent targeted regressions and architecture note, then coordinated gates.

## Source-only regression preparation after performance reprioritization

- Registered DispatchTupleResultOwnershipTemplate LAST, in a new source file. This is explicitly
  a negative boundary regression (baseline flat), not falsely claimed as a flat positive leak repro.
- Added Linux_backend_mutual_call_tuple_successor_memory_should_plateau with2k/8k/32k rounds,
  expected14*rounds, and four e2e scenarios copied from already formatted/measured inputs:
  mutual_call_tuple_successor, mutual_dispatch_tuple_return_aliases,
  mutual_dispatch_tuple_saved_shadow, mutual_dispatch_tuple_fresh_shadow.
- Added architecture note for private scalar dispatch ownership, whole-result normalization,
  and obligation-only tuple adoption. No language semantics change.
- Reviewed narrow mirror statically: exactly two CoreCallStage constructors initialize new flag;
  capture only final application; existing tuple plan/gate and existing arena-copy path preserved;
  other record updates carry field unchanged. Helper ordering respects sequential declarations.
- Source-only additions not built/tested yet per parent instruction. Mirror formatted, unbuilt.
  Exact original candidate owner balance and targeted switch isolation are recorded above.
- Still needed: flat positive reproducer isolating a2 ownership from unrelated instruction/temp
  leaks; targeted permanent tests/format gates; mirror build+measurements; real-code probe; full
  gates at agreed integration checkpoint. No further useful source correction is justified until
  those runtime checks. Large builds remain deferred to stage2 performance work.
