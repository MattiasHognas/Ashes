Questions asked:

Q1: Whether my liveness analysis can incorrectly miss variables that must survive an await.

Q2: Whether temps or locals can be restored incorrectly after resume.

Q3: Whether recursion or nested async calls can break the transform.

Q4: Whether completed tasks can unnecessarily suspend and cause scheduler round-trips.

Q5: Whether the state struct layout is sufficient for all values that may cross await boundaries.

Q6: Whether any async function can observe incorrect behavior if an awaited task completes immediately.

Q7: Whether my save/restore logic is CFG-correct in the presence of branches, pattern matching, and recursive calls.

Q8: Whether there are lifetime or ownership issues with values stored in the coroutine frame.

Q9: Whether my implementation is truly stackless or accidentally relies on caller stack state after suspension.

Q10: Any cases where async tail-call optimization would be possible but currently prevented.

Knowledge gained:

Bug 1 — Liveness analysis silently drops several instruction kinds (Q1, Q2, Q5)

StateMachineTransform.GetDefinedTemps (lines 470‑541) and GetUsedTemps (548‑618) are missing cases for instructions that do define/use temps:

- TextUncons, TextParseInt, TextParseFloat (Ir.cs:95‑97)

- CopyOutArena, CopyOutList, CopyOutClosure, CopyOutTcoListCell (Ir.cs:183,218,235,261)

That these are real def/use temps is proven by IrOptimizer.cs:930‑944, which handles all of them correctly. The two switches have drifted out of sync (exactly the hazard the comments at StateMachineTransform.cs:466‑469 warn about).

This causes three distinct failures:

(a) A live value produced before an await is never saved. Because temp slots are alloca’d and zero-initialized on every coroutine re-entry (LlvmCodegen.cs:842‑846), the missed value deterministically reads 0 after resume.

Trace — Ashes.Text.parseInt result crossing an await (not bound to a let, so the locals path can’t rescue it):
async
  Ashes.Text.parseInt(input) + (await (async 1))

LowerAdd evaluates the left operand first (Lowering.cs:861‑862):

TextParseInt %tL, %input     ; define %tL  (state 0)
... CreateTask / AwaitTask %tR  ; await0
AddInt %res, %tL, %tR        ; uses %tL    (state 1)

ComputeLiveTempsAcrossAwaits: definedBefore calls GetDefinedTemps(TextParseInt) → [], so %tL is not in definedBefore → not in the live set → no StoreMemOffset at suspend. On resume %tL = 0. Result is 0 + 1 = 1 instead of parseInt(input) + 1. Wrong, deterministically.

(b) A post-await use is invisible, so the operand feeding it isn’t saved (GetUsedTemps returns [] for these, so usedAfter misses it).

(c) MaxTemp underestimation → reserved-temp aliasing. GetAllTemps (623‑626, used at 109‑115) computes maxTemp. The transform then reserves stateStructTemp = maxTemp+1, stateIdxTemp, statusTemp, awaitResultTemp (118‑122). If the highest body temp appears only as e.g. CopyOutList.DestTemp, maxTemp is too low and stateStructTemp aliases that live temp. The very first emitted instruction LoadLocal(stateStructTemp, 0) (line 125) then clobbers it. This corrupts arena/TCO copy-out results in any async body that returns owned aggregates.


Bug 2 — `Ashes.Async.run` only handles 2 levels of nested awaited coroutines (Q3)

EmitRunTask (LlvmCodegenExpressions.cs:867) drives the top task; for a normal (non-leaf) awaited sub-task it calls EmitRunTaskRecursive (985). But EmitRunTaskRecursive's nested handling (nestedStepBlock, 1094‑1116) only re-calls the awaited coroutine in a loop — it never drives that coroutine’s own awaited sub-task. When the nested coroutine suspends, line 1106‑1108 loops back to nestedStepBlock and re-enters it without ever populating its ResultSlot.

Trace — minimal failing program (4 nested async blocks):
Ashes.Async.run(async            // T0  -> EmitRunTask
  await (async                    // T1  -> EmitRunTaskRecursive
    await (async                  // T2  -> handled inline (nestedStepBlock)
      await (async 1))))          // T3  -> NEVER stepped

- EmitRunTask(T0): T0 suspends on T1 → normalSubBlock → EmitRunTaskRecursive(T1).
- EmitRunTaskRecursive(T1): T1 suspends on T2 → subSuspendedBlock, awaitedTask = T2.
- T2 is a coroutine (state 0) → nestedStepBlock: call T2. T2 suspends on T3 (state→1), returns SUSPENDED. nestedSuspended is true → loop back to nestedStepBlock.
- Call T2 again: it dispatches to state 1 and loads its own ResultSlot, which was never filled with T3’s result (T3 was never stepped). It reads 0.

Result: Ok(0) instead of Ok(1). The existing tests/async_nested_tasks.ash sits exactly one level below this boundary (its innermost async 3 has no await, so a single nestedStepBlock call completes it), which is why the gap is untested. Note the cooperative driver EmitStepTaskUntilPendingOrDone (518) does not have this bug — it makes a genuine recursive runtime call ashes_step_task_until_wait_or_done(awaitedTask) (568). The two drivers have inconsistent capabilities.


Bug 3 — Completed/immediate tasks always suspend (Q4, Q6)

The transform emits the suspend sequence unconditionally at every await (StateMachineTransform.cs:239‑278): save live vars, store next state, Return SUSPENDED. There is no “is the awaited task already complete?” fast path.

So await Ashes.Async.fromResult(x) (a CreateCompletedTask, already state = -1) still forces: full live-var save → return 0 → driver reloads AwaitedTask, steps the completed task, copies result, re-enters and resumes. In Ashes.Async.all/race and the cooperative scheduler (EmitStepTaskUntilPendingOrDone, which surfaces control back to its caller at 581‑586), each already-complete await costs a scheduler round-trip plus a redundant save/restore. Functionally the result is correct (Q6: I did not find an incorrect value from immediate completion), but it is a guaranteed extra round-trip per await — directly answering Q4 in the affirmative.


Bug 4 — `run` path is not truly stackless; sleep blocks the thread (Q9)

Two issues on the synchronous run path:

1. EmitRunTaskRecursive recursion is real native recursion (967, and the nested loop), so a chain of awaited coroutines consumes native C stack proportional to await-nesting depth. Combined with Bug 2 this is both incorrect and stack-bound.

2. EmitStepLeafTask’s sleep case performs a blocking nanosleep/Sleep on the OS thread (LlvmCodegenExpressions.cs:352‑360; also EmitHandleSubTask 1243‑1245) and marks the task complete in place, rather than yielding to the scheduler. While a sleep is pending, no other ready task makes progress — the “stackless cooperative” property holds for the generated coroutine frames themselves, but not for these drivers.

The generated coroutine bodies are correctly stackless (all live state lives in the heap frame; nothing relies on caller stack after Return SUSPENDED) — the violations are in the drivers, not the state machine.


Bug 5 — Async tail calls are categorically un-optimizable (Q10)

LowerAsync force-disables TCO for the whole coroutine body: _tcoCtx = null; at Lowering.cs:3880 (restored after the body at Lowering.cs:3974; a separate coroutine-building path restores at Lowering.cs:2026). So a tail-recursive async function — including return await self(...) — is never turned into a loop, and combined with the real native recursion in EmitRunTaskRecursive, deep async recursion overflows the stack. There is no async tail-call elision anywhere: the final segment always allocates a fresh state, loads ResultSlot, and returns (StateMachineTransform.cs:279‑288); a return await f() could instead splice f’s task as the continuation but does not.


Q7 — Save/restore CFG correctness with branches/match

The save/restore is positional, not CFG-based (ComputeLiveTempsAcrossAwaits/ComputeLiveLocalsAcrossAwaits scan by instruction index, 354‑434). I traced match-with-await-in-arms and match/let lowering and it is currently sound for branches, but only by two fragile accidents:

- Cross-state control labels (e.g. L_armB, L_merge) are emitted inside their segment, after the resume prologue, and intra-body jumps target those labels directly — so a branch entering a “future” state’s code lands after its restore prologue and doesn’t double-restore.
- The body is loop-free: TCO is off (3880) and the language has no loop construct, so there are no backward edges. Positional “defined-before ∧ used-after” can therefore only over-approximate (safe).

This is the latent risk to flag: the moment any backward edge appears in a coroutine body (re-enabling async TCO, or adding a loop construct), positional liveness will under-approximate and silently drop variables live around the back-edge. A value defined late in a loop body and consumed early on the next iteration across an await would never be saved. The design needs real CFG liveness before any of that is added.


Q8 — Lifetime/ownership of values in the frame

The frame saves raw i64 by value (StoreMemOffset/LoadMemOffset), which is correct for scalars and heap pointers, but the transform has no guard that a saved temp/local isn’t a pointer into memory that is reclaimed across the suspend:

- SaveArenaState/RestoreArenaState only save the arena cursor/end local slots (StateMachineTransform.cs:440‑463), not the arena contents. If an arena region is reset while a coroutine is suspended, any restored pointer into it dangles.
- AllocStack/AllocAdtStack/MakeClosureStack (Lowering.cs:2755,2937,3492,3556) produce alloca memory. The coroutine’s native stack frame is destroyed at Return SUSPENDED; if such a pointer were ever live across an await, restoring it on re-entry yields a pointer to dead stack.

Today these stack-allocation sites are limited to immediately-consumed, non-escaping values (e.g. single-arm constructor match scrutinee, ShouldStackAllocateImmediateMatchScrutinee 5576‑5579), so I could not construct a current crash — but nothing in the transform enforces this invariant, so it’s a real latent ownership hazard.


Q5 — Is the state-struct layout sufficient?

Size-wise, yes: every scalar fits one 8-byte slot, including floats (stored bit-cast to i64: LlvmCodegen.cs:1141‑1143,1152), and aggregates (strings/lists/tuples/ADTs/closures) are single heap pointers. Slots are unioned per temp/local, which over-allocates but is correct. The only “insufficiency” is missing slots for the instructions in Bug 1 — a coverage bug, not a width/layout bug.

Priority summary

1. Bug 1 (missing liveness cases) — silent wrong results + temp aliasing; highest impact, smallest fix (sync the two switches with IrOptimizer.cs:930‑944).

2. Bug 2 (run nesting depth ≥3) — wrong results for deep nesting; align EmitRunTaskRecursive with the recursive ashes_step_task_until_wait_or_done approach.

3. Bug 5 / Q7 / Q8 — architectural: positional liveness + disabled TCO + by-value frame saves are mutually load-bearing; document/enforce the loop-free + no-cross-await-stack-pointer invariants before extending the language.

4. Bug 3 / Bug 4 — performance/round-trips and blocking sleep.

This document records the confirmed findings only; fixes and regression tests should be tracked in follow-up issues or PRs.
