using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ArenaDeallocationTests
{
    // --- SaveArenaState IR structure ---

    [Test]
    public void SaveArenaState_has_cursor_and_end_slots()
    {
        var save = new IrInst.SaveArenaState(5, 6);
        save.CursorLocalSlot.ShouldBe(5);
        save.EndLocalSlot.ShouldBe(6);
    }

    [Test]
    public void RestoreArenaState_has_cursor_end_and_pre_restore_end_slots()
    {
        var restore = new IrInst.RestoreArenaState(5, 6, 7);
        restore.CursorLocalSlot.ShouldBe(5);
        restore.EndLocalSlot.ShouldBe(6);
        restore.PreRestoreEndSlot.ShouldBe(7);
    }

    [Test]
    public void ReclaimArenaChunks_has_saved_end_and_pre_restore_end_slots()
    {
        var reclaim = new IrInst.ReclaimArenaChunks(6, 7);
        reclaim.SavedEndSlot.ShouldBe(6);
        reclaim.PreRestoreEndSlot.ShouldBe(7);
    }

    // --- SaveArenaState emitted at ownership scope entry ---

    [Test]
    public void Let_scope_emits_SaveArenaState()
    {
        var ir = LowerProgram("let x = 42 in x + 1");
        HasSaveArenaState(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Let expression should emit SaveArenaState at scope entry.");
    }

    [Test]
    public void Match_arm_emits_SaveArenaState()
    {
        var ir = LowerProgram(
            """
            match 1 with
                | 1 -> 42
                | _ -> 0
            """);
        HasSaveArenaState(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Match arm should emit SaveArenaState at scope entry.");
    }

    // --- RestoreArenaState emitted for copy-type results ---

    [Test]
    public void Int_body_let_emits_RestoreArenaState()
    {
        var ir = LowerProgram("let x = 42 in x + 1");
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Let expression with Int body should emit RestoreArenaState.");
    }

    [Test]
    public void Bool_body_let_emits_RestoreArenaState()
    {
        var ir = LowerProgram("let x = 42 in x >= 0");
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Let expression with Bool body should emit RestoreArenaState.");
    }

    [Test]
    public void Float_body_let_emits_RestoreArenaState()
    {
        var ir = LowerProgram("let x = 3.14 in x + 1.0");
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Let expression with Float body should emit RestoreArenaState.");
    }

    [Test]
    public void Match_with_int_result_emits_RestoreArenaState()
    {
        var ir = LowerProgram(
            """
            match 1 with
                | 1 -> 42
                | _ -> 0
            """);
        var insts = ir.EntryFunction.Instructions;
        var restoreCount = insts.Count(i => i is IrInst.RestoreArenaState);
        var saveCount = insts.Count(i => i is IrInst.SaveArenaState);

        // Each arm has RestoreArenaState on cleanup path AND on success path
        // (because Int is a copy type). So restoreCount > saveCount.
        restoreCount.ShouldBeGreaterThan(saveCount,
            "Int-result match should emit RestoreArenaState on both cleanup and success paths.");
    }

    // --- RestoreArenaState NOT emitted for heap-type results ---

    [Test]
    public void String_body_let_does_not_emit_RestoreArenaState()
    {
        var ir = LowerProgram("let x = 42 in \"hello\"");
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeFalse(
            "Let expression with String body should NOT emit RestoreArenaState.");
    }

    [Test]
    public void List_body_let_does_not_emit_RestoreArenaState()
    {
        var ir = LowerProgram("let x = 42 in [1, 2, 3]");
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeFalse(
            "Let expression with List body should NOT emit RestoreArenaState.");
    }

    [Test]
    public void Match_with_string_result_does_not_emit_RestoreArenaState_on_success_path()
    {
        // With arm cleanup paths, each match arm emits RestoreArenaState on the
        // failure (cleanup) path. For heap-type results (String), the success path
        // does NOT emit RestoreArenaState. So the total RestoreArenaState count
        // equals the number of arms (cleanup only, not success).
        var ir = LowerProgram(
            """
            match 1 with
                | 1 -> "yes"
                | _ -> "no"
            """);
        var insts = ir.EntryFunction.Instructions;
        var restoreCount = insts.Count(i => i is IrInst.RestoreArenaState);
        var saveCount = insts.Count(i => i is IrInst.SaveArenaState);

        // Each arm has one SaveArenaState and one RestoreArenaState (cleanup only).
        // Success path does NOT restore because String is a heap type.
        restoreCount.ShouldBe(saveCount,
            "String-result match should only emit RestoreArenaState on cleanup paths, " +
            "not on success paths.");
    }

    // --- Save/Restore slot pairing ---

    [Test]
    public void Save_and_Restore_use_matching_slots()
    {
        var ir = LowerProgram("let x = 42 in x + 1");
        var insts = ir.EntryFunction.Instructions;

        var save = insts.OfType<IrInst.SaveArenaState>().First();
        var restore = insts.OfType<IrInst.RestoreArenaState>().First();

        save.CursorLocalSlot.ShouldBe(restore.CursorLocalSlot,
            "SaveArenaState and RestoreArenaState should use the same cursor slot.");
        save.EndLocalSlot.ShouldBe(restore.EndLocalSlot,
            "SaveArenaState and RestoreArenaState should use the same end slot.");
    }

    // --- Nested scopes: multiple saves ---

    [Test]
    public void Nested_let_emits_multiple_SaveArenaState()
    {
        var ir = LowerProgram(
            """
            let x = 1 in
            let y = 2 in
            x + y
            """);
        var saveCount = ir.EntryFunction.Instructions.Count(i => i is IrInst.SaveArenaState);
        saveCount.ShouldBeGreaterThanOrEqualTo(2,
            "Nested let expressions should emit SaveArenaState for each scope.");
    }

    [Test]
    public void Nested_let_with_int_result_emits_multiple_RestoreArenaState()
    {
        var ir = LowerProgram(
            """
            let x = 1 in
            let y = 2 in
            x + y
            """);
        var restoreCount = ir.EntryFunction.Instructions.Count(i => i is IrInst.RestoreArenaState);
        restoreCount.ShouldBeGreaterThanOrEqualTo(2,
            "Nested let expressions with Int result should emit RestoreArenaState for each scope.");
    }

    // --- Lambda functions get arena save/restore ---

    [Test]
    public void Lambda_with_let_and_int_body_emits_SaveArenaState()
    {
        var ir = LowerProgram("let f = fun (x) -> let y = x + 1 in y + 2 in Ashes.IO.print(f(42))");
        // The lambda function should have SaveArenaState from the inner let scope
        var lambdaFunc = ir.Functions.First();
        HasSaveArenaState(lambdaFunc.Instructions).ShouldBeTrue(
            "Lambda function with let binding should emit SaveArenaState.");
    }

    // --- Owned value with copy-type body still gets arena reset ---

    [Test]
    public void Owned_string_with_int_body_emits_both_Drop_and_RestoreArenaState()
    {
        var ir = LowerProgram(
            """
            let s = "hello" in
            42
            """);
        var insts = ir.EntryFunction.Instructions;
        HasDropInstruction(insts, "String").ShouldBeTrue(
            "String binding should still get Drop instruction.");
        HasRestoreArenaState(insts).ShouldBeTrue(
            "Int result body should emit RestoreArenaState after drops.");
    }

    // --- Watermark placement: SaveArenaState before heap-allocating let-bound value ---

    [Test]
    public void SaveArenaState_emitted_before_list_alloc_in_let()
    {
        // When the let-bound value allocates on the heap (list construction),
        // SaveArenaState must appear BEFORE the Alloc instructions so that
        // the arena watermark covers those allocations.
        var ir = LowerProgram("let xs = [1, 2, 3] in 0");
        var insts = ir.EntryFunction.Instructions;

        var saveIndex = insts.FindIndex(i => i is IrInst.SaveArenaState);
        var firstAllocIndex = insts.FindIndex(i => i is IrInst.Alloc);

        saveIndex.ShouldBeGreaterThanOrEqualTo(0, "Should emit SaveArenaState.");
        firstAllocIndex.ShouldBeGreaterThanOrEqualTo(0, "Should emit Alloc for list construction.");
        saveIndex.ShouldBeLessThan(firstAllocIndex,
            "SaveArenaState must precede heap allocations from the let-bound value.");
    }

    [Test]
    public void Heap_let_value_with_copy_body_emits_RestoreArenaState()
    {
        // let xs = [1, 2, 3] in 0 — the body is Int (copy type),
        // so RestoreArenaState should be emitted to reclaim the list allocation.
        var ir = LowerProgram("let xs = [1, 2, 3] in 0");
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Copy-type body after heap-allocating let value should emit RestoreArenaState.");
    }

    [Test]
    public void SaveArenaState_emitted_before_tuple_alloc_in_let()
    {
        var ir = LowerProgram("let t = (1, 2) in 0");
        var insts = ir.EntryFunction.Instructions;

        var saveIndex = insts.FindIndex(i => i is IrInst.SaveArenaState);
        var firstAllocIndex = insts.FindIndex(i => i is IrInst.Alloc);

        saveIndex.ShouldBeGreaterThanOrEqualTo(0, "Should emit SaveArenaState.");
        firstAllocIndex.ShouldBeGreaterThanOrEqualTo(0, "Should emit Alloc for tuple construction.");
        saveIndex.ShouldBeLessThan(firstAllocIndex,
            "SaveArenaState must precede heap allocations from the let-bound tuple.");
    }

    // --- Match arm failure cleanup: RestoreArenaState on failed paths ---

    [Test]
    public void Failed_match_arm_emits_RestoreArenaState_on_cleanup_path()
    {
        // When a match arm's pattern fails, the cleanup path should:
        // 1. Label (match_arm_cleanup) — the pattern/guard failure target
        // 2. RestoreArenaState — reclaim allocations from the failed arm
        // 3. Jump — proceed to the next arm or noMatch
        var ir = LowerProgram(
            """
            match 1 with
                | 0 -> 42
                | _ -> 0
            """);
        var insts = ir.EntryFunction.Instructions;

        // Find a cleanup path: Label followed by RestoreArenaState + ReclaimArenaChunks followed by Jump
        bool foundCleanupPath = false;
        for (int i = 0; i < insts.Count - 3; i++)
        {
            if (insts[i] is IrInst.Label
                && insts[i + 1] is IrInst.RestoreArenaState
                && insts[i + 2] is IrInst.ReclaimArenaChunks
                && insts[i + 3] is IrInst.Jump)
            {
                foundCleanupPath = true;
                break;
            }
        }
        foundCleanupPath.ShouldBeTrue(
            "Match arm should have a cleanup path: Label → RestoreArenaState → ReclaimArenaChunks → Jump.");
    }

    [Test]
    public void Match_arm_cleanup_RestoreArenaState_uses_same_slots_as_Save()
    {
        var ir = LowerProgram(
            """
            match 1 with
                | 0 -> 42
                | _ -> 0
            """);
        var insts = ir.EntryFunction.Instructions;

        // Each SaveArenaState should have a corresponding RestoreArenaState on
        // the cleanup path using the same slot pair.
        var saves = insts.OfType<IrInst.SaveArenaState>().ToList();
        saves.Count.ShouldBeGreaterThanOrEqualTo(1);

        foreach (var save in saves)
        {
            var matchingRestores = insts.OfType<IrInst.RestoreArenaState>()
                .Where(r => r.CursorLocalSlot == save.CursorLocalSlot
                         && r.EndLocalSlot == save.EndLocalSlot)
                .ToList();
            matchingRestores.Count.ShouldBeGreaterThanOrEqualTo(1,
                $"SaveArenaState(cursor={save.CursorLocalSlot}, end={save.EndLocalSlot}) " +
                "should have at least one matching RestoreArenaState (cleanup or success path).");
        }
    }

    // --- TCO loop iteration arena reset ---

    [Test]
    public void TCO_loop_with_int_args_emits_SaveArenaState_after_body_label()
    {
        var ir = LowerProgram(
            """
            let rec sum = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else sum (n - 1) (acc + n)
            in sum 100 0
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        // Find the body label (contains "_body")
        var bodyLabelIdx = insts.FindIndex(i => i is IrInst.Label lbl && lbl.Name.Contains("_body"));
        bodyLabelIdx.ShouldBeGreaterThanOrEqualTo(0, "TCO function should have a body label.");

        // SaveArenaState should appear right after the body label
        var nextInst = insts[bodyLabelIdx + 1];
        nextInst.ShouldBeOfType<IrInst.SaveArenaState>(
            "SaveArenaState should be emitted immediately after the TCO body label.");
    }

    [Test]
    public void TCO_loop_with_int_args_emits_RestoreArenaState_before_jump_back()
    {
        var ir = LowerProgram(
            """
            let rec sum = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else sum (n - 1) (acc + n)
            in sum 100 0
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        // Find the tail-call jump back: RestoreArenaState + ReclaimArenaChunks followed by Jump to body label
        bool foundTcoRestore = false;
        for (int i = 0; i < insts.Count - 2; i++)
        {
            if (insts[i] is IrInst.RestoreArenaState
                && insts[i + 1] is IrInst.ReclaimArenaChunks
                && insts[i + 2] is IrInst.Jump j
                && j.Target.Contains("_body"))
            {
                foundTcoRestore = true;
                break;
            }
        }
        foundTcoRestore.ShouldBeTrue(
            "TCO loop with copy-type args should emit RestoreArenaState + ReclaimArenaChunks before jumping back.");
    }

    [Test]
    public void TCO_loop_with_int_args_save_restore_slots_match()
    {
        var ir = LowerProgram(
            """
            let rec sum = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else sum (n - 1) (acc + n)
            in sum 100 0
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        // Find the SaveArenaState after body label
        var bodyLabelIdx = insts.FindIndex(i => i is IrInst.Label lbl && lbl.Name.Contains("_body"));
        var save = (IrInst.SaveArenaState)insts[bodyLabelIdx + 1];

        // Find RestoreArenaState + ReclaimArenaChunks before the jump back
        for (int i = 0; i < insts.Count - 2; i++)
        {
            if (insts[i] is IrInst.RestoreArenaState restore
                && insts[i + 1] is IrInst.ReclaimArenaChunks
                && insts[i + 2] is IrInst.Jump j
                && j.Target.Contains("_body"))
            {
                restore.CursorLocalSlot.ShouldBe(save.CursorLocalSlot,
                    "TCO arena Save and Restore should use matching cursor slots.");
                restore.EndLocalSlot.ShouldBe(save.EndLocalSlot,
                    "TCO arena Save and Restore should use matching end slots.");
                return;
            }
        }
        Assert.Fail("Expected RestoreArenaState before TCO jump-back.");
    }

    [Test]
    public void TCO_loop_with_list_of_int_arg_emits_RestoreArenaState_and_CopyOutArena_before_jump()
    {
        // When a tail-call argument is a TList(Int) (cons cell with copy-type head),
        // the cons cell is self-contained (head is a direct i64, tail points to pre-watermark
        // memory from the previous iteration). Emitting RestoreArenaState + CopyOutArena(16)
        // allows the iteration's arena to be reclaimed while the accumulator survives.
        var ir = LowerProgram(
            """
            let rec build = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else build (n - 1) (n :: acc)
            in build 5 []
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        // Find the sequence: RestoreArenaState → CopyOutArena(_, _, 16) → StoreLocal → Jump
        bool foundCopyOutSequence = false;
        for (int i = 0; i < insts.Count - 2; i++)
        {
            if (insts[i] is IrInst.RestoreArenaState
                && insts[i + 1] is IrInst.CopyOutArena copyOut
                && copyOut.StaticSizeBytes == 16)
            {
                foundCopyOutSequence = true;
                break;
            }
        }
        foundCopyOutSequence.ShouldBeTrue(
            "TCO loop with TList(Int) arg should emit RestoreArenaState + CopyOutArena(16).");
    }

    [Test]
    public void TCO_loop_with_list_arg_still_emits_SaveArenaState_after_body_label()
    {
        // SaveArenaState is always emitted at loop body start for the per-iteration watermark.
        var ir = LowerProgram(
            """
            let rec build = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else build (n - 1) (n :: acc)
            in build 5 []
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        var bodyLabelIdx = insts.FindIndex(i => i is IrInst.Label lbl && lbl.Name.Contains("_body"));
        bodyLabelIdx.ShouldBeGreaterThanOrEqualTo(0, "TCO function should have a body label.");
        insts[bodyLabelIdx + 1].ShouldBeOfType<IrInst.SaveArenaState>(
            "SaveArenaState should be emitted immediately after the TCO body label.");
    }

    [Test]
    public void TCO_loop_with_list_of_list_arg_emits_RestoreArenaState_and_CopyOutTcoListCell_before_jump()
    {
        // TList(TList(Int)): each iteration creates a new inner list [n] and prepends it
        // to acc. The inner list has copy-type elements, so the outer cell + inner chain
        // can be copied out via CopyOutTcoListCell(InnerList).
        var ir = LowerProgram(
            """
            let rec build = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else build (n - 1) ([n] :: acc)
            in build 5 []
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        bool found = false;
        for (int i = 0; i < insts.Count - 1; i++)
        {
            if (insts[i] is not IrInst.RestoreArenaState)
            {
                continue;
            }

            var reclaimIndex = -1;
            for (int j = i + 1; j < insts.Count; j++)
            {
                if (insts[j] is IrInst.ReclaimArenaChunks)
                {
                    reclaimIndex = j;
                    break;
                }
            }

            if (reclaimIndex == -1)
            {
                continue;
            }

            for (int j = i + 1; j < reclaimIndex; j++)
            {
                if (insts[j] is IrInst.CopyOutTcoListCell cell
                    && cell.HeadCopy == IrInst.ListHeadCopyKind.InnerList)
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                break;
            }
        }

        found.ShouldBeTrue(
            "TCO loop with TList(TList(Int)) arg should emit CopyOutTcoListCell(InnerList) after RestoreArenaState and before ReclaimArenaChunks.");
    }

    [Test]
    public void TCO_single_param_with_int_arg_emits_RestoreArenaState()
    {
        // Single-parameter TCO: let rec countdown = fun n -> if n == 0 then 0 else countdown (n - 1)
        var ir = LowerProgram(
            """
            let rec countdown = fun (n) ->
                if n == 0 then 0
                else countdown (n - 1)
            in countdown 100
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        bool foundTcoRestore = false;
        for (int i = 0; i < insts.Count - 2; i++)
        {
            if (insts[i] is IrInst.RestoreArenaState
                && insts[i + 1] is IrInst.ReclaimArenaChunks
                && insts[i + 2] is IrInst.Jump j
                && j.Target.Contains("_body"))
            {
                foundTcoRestore = true;
                break;
            }
        }
        foundTcoRestore.ShouldBeTrue(
            "Single-param TCO loop with Int arg should emit RestoreArenaState + ReclaimArenaChunks before jump-back.");
    }

    // --- Extended TCO copy-out ---

    [Test]
    public void TCO_loop_with_list_of_string_arg_emits_RestoreArenaState_and_CopyOutTcoListCell()
    {
        // TList(TStr): each iteration creates a string and prepends it to acc.
        // The cons cell head is a string pointer — CopyOutTcoListCell(String)
        // copies the cell AND the string to new arena locations.
        var ir = LowerProgram(
            """
            let rec build = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else build (n - 1) ("x" :: acc)
            in build 5 []
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        bool found = false;
        for (int i = 0; i < insts.Count - 1; i++)
        {
            if (insts[i] is not IrInst.RestoreArenaState)
            {
                continue;
            }

            var reclaimIndex = -1;
            for (int j = i + 1; j < insts.Count; j++)
            {
                if (insts[j] is IrInst.ReclaimArenaChunks)
                {
                    reclaimIndex = j;
                    break;
                }
            }

            if (reclaimIndex == -1)
            {
                continue;
            }

            for (int j = i + 1; j < reclaimIndex; j++)
            {
                if (insts[j] is IrInst.CopyOutTcoListCell cell
                    && cell.HeadCopy == IrInst.ListHeadCopyKind.String)
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                break;
            }
        }

        found.ShouldBeTrue(
            "TCO loop with TList(TStr) arg should emit CopyOutTcoListCell(String) after RestoreArenaState and before ReclaimArenaChunks.");
    }

    [Test]
    public void TCO_loop_with_closure_arg_emits_RestoreArenaState_and_CopyOutClosure()
    {
        // TFun: the TCO accumulator is a closure. CopyOutClosure copies the 24-byte
        // closure struct and its environment.
        var ir = LowerProgram(
            """
            let rec build = fun (n) -> fun (f) ->
                if n == 0 then f 0
                else build (n - 1) (fun (x) -> x + n)
            in build 5 (fun (x) -> x)
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        static bool IsJumpBackToBodyLabel(object inst)
        {
            var text = inst.ToString() ?? string.Empty;
            return text.Contains("_body") && (text.Contains("Jump") || text.Contains("Branch") || text.Contains("Br"));
        }

        bool found = false;
        for (int i = 0; i < insts.Count; i++)
        {
            if (insts[i] is not IrInst.RestoreArenaState)
            {
                continue;
            }

            int copyOutIndex = -1;
            int reclaimIndex = -1;
            int jumpBackIndex = -1;

            for (int j = i + 1; j < insts.Count; j++)
            {
                if (copyOutIndex == -1 && insts[j] is IrInst.CopyOutClosure)
                {
                    copyOutIndex = j;
                }

                if (reclaimIndex == -1 && insts[j] is IrInst.ReclaimArenaChunks)
                {
                    reclaimIndex = j;
                }

                if (jumpBackIndex == -1 && IsJumpBackToBodyLabel(insts[j]))
                {
                    jumpBackIndex = j;
                }

                if (reclaimIndex != -1 && jumpBackIndex != -1)
                {
                    break;
                }
            }

            if (copyOutIndex != -1
                && reclaimIndex != -1
                && jumpBackIndex != -1
                && copyOutIndex < reclaimIndex
                && copyOutIndex < jumpBackIndex)
            {
                found = true;
                break;
            }
        }

        found.ShouldBeTrue(
            "TCO loop with closure arg should emit CopyOutClosure after RestoreArenaState and before ReclaimArenaChunks and the jump-back to the _body label.");
    }

    [Test]
    public void TCO_loop_with_adt_copy_type_arg_emits_RestoreArenaState_and_CopyOutArena()
    {
        // TNamedType with copy-type fields: ADT can be shallow-copied.
        var ir = LowerProgram(
            """
            type Box =
                | Box(Int)
            let rec build = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else build (n - 1) (Box(n))
            in build 5 (Box(0))
            """);
        var tcoFunc = FindTcoFunction(ir);
        var insts = tcoFunc.Instructions;

        bool found = false;
        for (int i = 0; i < insts.Count - 2; i++)
        {
            if (insts[i] is not IrInst.RestoreArenaState)
            {
                continue;
            }

            var copyOutIndex = -1;
            for (int j = i + 1; j < insts.Count - 1; j++)
            {
                if (insts[j] is IrInst.CopyOutArena c && c.StaticSizeBytes == 16)
                {
                    copyOutIndex = j;
                    break;
                }
            }

            if (copyOutIndex == -1)
            {
                continue;
            }

            var reclaimIndex = -1;
            for (int j = copyOutIndex + 1; j < insts.Count; j++)
            {
                if (insts[j] is IrInst.ReclaimArenaChunks)
                {
                    reclaimIndex = j;
                    break;
                }
            }

            if (reclaimIndex != -1)
            {
                found = true;
                break;
            }
        }

        found.ShouldBeTrue(
            "TCO loop with ADT(Int) arg should emit RestoreArenaState -> CopyOutArena(16) before ReclaimArenaChunks.");
    }

    [Test]
    public void CopyOutTcoListCell_instruction_has_correct_fields()
    {
        var inst = new IrInst.CopyOutTcoListCell(7, 3, IrInst.ListHeadCopyKind.String);
        inst.DestTemp.ShouldBe(7);
        inst.SrcTemp.ShouldBe(3);
        inst.HeadCopy.ShouldBe(IrInst.ListHeadCopyKind.String);

        var innerInst = new IrInst.CopyOutTcoListCell(10, 5, IrInst.ListHeadCopyKind.InnerList);
        innerInst.HeadCopy.ShouldBe(IrInst.ListHeadCopyKind.InnerList);
    }

    [Test]
    public void CopyOutList_instruction_has_default_inline_head_copy()
    {
        var inst = new IrInst.CopyOutList(7, 3);
        inst.HeadCopy.ShouldBe(IrInst.ListHeadCopyKind.Inline);
    }

    // --- Copy-out for String scope results ---

    [Test]
    public void String_result_let_with_owned_binding_emits_CopyOutArena()
    {
        // let s = "hello" in s + " world"
        // s is an owned String binding. The body is a heap-allocated concat string.
        // RestoreArenaState + CopyOutArena(-1) should be emitted for the string result.
        var ir = LowerProgram("let s = \"hello\" in s + \" world\"");
        var insts = ir.EntryFunction.Instructions;

        HasCopyOutArena(insts).ShouldBeTrue(
            "String result with owned binding should emit CopyOutArena.");
    }

    [Test]
    public void String_result_let_with_owned_binding_emits_RestoreArenaState_before_CopyOutArena()
    {
        var ir = LowerProgram("let s = \"hello\" in s + \" world\"");
        var insts = ir.EntryFunction.Instructions;

        bool foundSequence = false;
        for (int i = 0; i < insts.Count - 1; i++)
        {
            if (insts[i] is IrInst.RestoreArenaState
                && insts[i + 1] is IrInst.CopyOutArena c
                && c.StaticSizeBytes == -1)
            {
                foundSequence = true;
                break;
            }
        }
        foundSequence.ShouldBeTrue(
            "String result copy-out should follow RestoreArenaState with StaticSizeBytes == -1.");
    }

    [Test]
    public void String_body_let_without_owned_binding_does_not_emit_CopyOutArena()
    {
        // let x = 42 in "hello" — x is Int (not owned), no heap to reclaim.
        // Copy-out requires at least one owned value in scope.
        var ir = LowerProgram("let x = 42 in \"hello\"");
        var insts = ir.EntryFunction.Instructions;

        HasCopyOutArena(insts).ShouldBeFalse(
            "String result with no owned bindings should NOT emit CopyOutArena.");
        HasRestoreArenaState(insts).ShouldBeFalse(
            "String result with no owned bindings should NOT emit RestoreArenaState.");
    }

    [Test]
    public void List_body_let_emits_CopyOutList()
    {
        // Lists are deep-copied (entire cons chain) via CopyOutList.
        var ir = LowerProgram("let s = \"hello\" in [1, 2, 3]");
        ir.EntryFunction.Instructions.Any(i => i is IrInst.CopyOutList).ShouldBeTrue(
            "List(Int) result with owned binding should emit CopyOutList from a let scope.");
    }

    [Test]
    public void CopyOutArena_instruction_has_correct_fields()
    {
        var inst = new IrInst.CopyOutArena(7, 3, -1);
        inst.DestTemp.ShouldBe(7);
        inst.SrcTemp.ShouldBe(3);
        inst.StaticSizeBytes.ShouldBe(-1);

        var fixedInst = new IrInst.CopyOutArena(10, 5, 16);
        fixedInst.StaticSizeBytes.ShouldBe(16);
    }

    [Test]
    public void CopyOutList_instruction_has_correct_fields()
    {
        var inst = new IrInst.CopyOutList(7, 3);
        inst.DestTemp.ShouldBe(7);
        inst.SrcTemp.ShouldBe(3);
    }

    [Test]
    public void CopyOutClosure_instruction_has_correct_fields()
    {
        var inst = new IrInst.CopyOutClosure(7, 3);
        inst.DestTemp.ShouldBe(7);
        inst.SrcTemp.ShouldBe(3);
    }

    [Test]
    public void MakeClosure_instruction_has_EnvSizeBytes()
    {
        var inst = new IrInst.MakeClosure(1, "test_lambda", 2, 24);
        inst.Target.ShouldBe(1);
        inst.FuncLabel.ShouldBe("test_lambda");
        inst.EnvPtrTemp.ShouldBe(2);
        inst.EnvSizeBytes.ShouldBe(24);
    }

    [Test]
    public void List_of_string_does_not_emit_CopyOutList()
    {
        // List(Str) should NOT emit CopyOutList — copying cons cells alone
        // would leave dangling string pointers after arena reclaim.
        var ir = LowerProgram("let s = \"hello\" in [\"a\", \"b\"]");
        ir.EntryFunction.Instructions.Any(i => i is IrInst.CopyOutList).ShouldBeFalse(
            "List(Str) should NOT emit CopyOutList (string elements are not copy types).");
    }

    [Test]
    public void List_of_list_does_not_emit_CopyOutList()
    {
        // List(List(Int)) — the head elements are list pointers, which are
        // not copy-type or TStr. Deep copy of the outer chain doesn't help
        // because inner lists may also be in the arena.
        var ir = LowerProgram("let s = \"hello\" in [[1, 2], [3, 4]]");
        ir.EntryFunction.Instructions.Any(i => i is IrInst.CopyOutList).ShouldBeFalse(
            "List(List(Int)) should NOT emit CopyOutList (nested list heads are not safe).");
    }

    // --- Extended copy-out: ADT ---

    [Test]
    public void Adt_with_copy_type_fields_let_result_emits_CopyOutArena()
    {
        // ADT with Int field: (1 + 1) * 8 = 16 bytes → safe for shallow copy.
        var ir = LowerProgram(
            """
            type Box =
                | Box(Int)
            let s = "hello" in Box(42)
            """);
        var insts = ir.EntryFunction.Instructions;

        HasCopyOutArena(insts).ShouldBeTrue(
            "ADT(Int) result with owned binding should emit CopyOutArena.");
    }

    [Test]
    public void Adt_with_copy_type_fields_emits_correct_static_size()
    {
        // ADT with 2 Int fields: (1 + 2) * 8 = 24 bytes.
        var ir = LowerProgram(
            """
            type Pair =
                | Pair(Int, Int)
            let s = "hello" in Pair(1)(2)
            """);
        var insts = ir.EntryFunction.Instructions;

        bool found = insts.Any(i => i is IrInst.CopyOutArena c && c.StaticSizeBytes == 24);
        found.ShouldBeTrue("Pair(Int, Int) result should emit CopyOutArena with StaticSizeBytes == 24.");
    }

    [Test]
    public void Adt_nullary_constructor_let_result_emits_CopyOutArena()
    {
        // Nullary ADT: (1 + 0) * 8 = 8 bytes → safe (just a tag, no pointers).
        var ir = LowerProgram(
            """
            type Color =
                | Red
                | Green
                | Blue
            let s = "hello" in Red
            """);
        var insts = ir.EntryFunction.Instructions;

        HasCopyOutArena(insts).ShouldBeTrue(
            "Nullary ADT result with owned binding should emit CopyOutArena.");
    }

    [Test]
    public void Adt_nullary_constructor_emits_correct_static_size()
    {
        // All-nullary ADT: (1 + 0) * 8 = 8 bytes.
        var ir = LowerProgram(
            """
            type Color =
                | Red
                | Green
                | Blue
            let s = "hello" in Red
            """);
        var insts = ir.EntryFunction.Instructions;

        bool found = insts.Any(i => i is IrInst.CopyOutArena c && c.StaticSizeBytes == 8);
        found.ShouldBeTrue("Nullary ADT result should emit CopyOutArena with StaticSizeBytes == 8.");
    }

    [Test]
    public void Adt_with_string_field_does_not_emit_CopyOutArena()
    {
        // ADT with Str field: the field is a pointer to a string that may be in the
        // freed arena region → not safe for shallow copy.
        var ir = LowerProgram(
            """
            type Named =
                | Named(Str)
            let s = "hello" in Named("world")
            """);
        var insts = ir.EntryFunction.Instructions;

        HasCopyOutArena(insts).ShouldBeFalse(
            "ADT(Str) result should NOT emit CopyOutArena (pointer field).");
    }

    [Test]
    public void Adt_with_variable_arity_constructors_does_not_emit_CopyOutArena()
    {
        // Maybe: None (0 fields) vs Some (1 field) — variable arity → not safe for
        // static-size copy.
        var ir = LowerProgram(
            """
            type Box =
                | Box(Int)
            let s = "hello" in match Box(1) with | Box(x) -> Some(x)
            """);
        var insts = ir.EntryFunction.Instructions;

        // The result type is Maybe(Int) — None has 0 fields, Some has 1 field.
        // Variable arity means we can't determine a static copy size.
        HasCopyOutArena(insts).ShouldBeFalse(
            "ADT with variable-arity constructors should NOT emit CopyOutArena.");
    }

    [Test]
    public void Closure_let_result_does_not_emit_CopyOutClosure()
    {
        // Closures may capture heap pointers in their env, so copy-out is
        // unsafe until escape analysis / recursive copy-out is implemented.
        var ir = LowerProgram("let s = \"hello\" in fun (y) -> y + 1");
        ir.EntryFunction.Instructions.Any(i => i is IrInst.CopyOutClosure).ShouldBeFalse(
            "Closure result should NOT emit CopyOutClosure (env may contain heap pointers).");
    }

    [Test]
    public void List_of_int_let_result_emits_CopyOutList()
    {
        // List(Int): deep cons-chain copy via CopyOutList.
        var ir = LowerProgram("let s = \"hello\" in [1, 2, 3]");
        ir.EntryFunction.Instructions.Any(i => i is IrInst.CopyOutList).ShouldBeTrue(
            "List(Int) result with owned binding should emit CopyOutList.");
    }

    [Test]
    public void Call_returning_adt_with_copy_fields_emits_CopyOutArena()
    {
        // Function returning an ADT with Int field → per-call copy-out with static size.
        var ir = LowerProgram(
            """
            type Box =
                | Box(Int)
            let wrap = fun (x) -> Box(x)
            in wrap(42)
            """);
        var instructions = ir.EntryFunction.Instructions;
        var lastCallIdx = instructions.FindLastIndex(i => i is IrInst.CallClosure);
        lastCallIdx.ShouldBeGreaterThan(-1, "Program should contain a CallClosure.");

        var afterCall = instructions.Skip(lastCallIdx + 1).ToList();
        afterCall.Any(i => i is IrInst.RestoreArenaState).ShouldBeTrue(
            "RestoreArenaState should appear after CallClosure for ADT(Int) result.");
        afterCall.Any(i => i is IrInst.CopyOutArena c && c.StaticSizeBytes == 16).ShouldBeTrue(
            "CopyOutArena(16) should appear after CallClosure for Box(Int) result.");
    }

    // --- Per-function-call arena watermarks ---

    [Test]
    public void Call_returning_int_emits_SaveArenaState_and_RestoreArenaState()
    {
        // add(10)(32) returns Int — per-call watermark should save+restore.
        var ir = LowerProgram(
            """
            let add = fun (x) -> fun (y) -> x + y
            in add(10)(32)
            """);
        var instructions = ir.EntryFunction.Instructions;

        // There should be a CallClosure (for the function call)
        instructions.Any(i => i is IrInst.CallClosure).ShouldBeTrue(
            "Program should contain at least one CallClosure.");

        // Find the CallClosure instructions and check that they are bracketed
        // by SaveArenaState ... RestoreArenaState (per-call watermark)
        var callIdx = instructions.FindIndex(i => i is IrInst.CallClosure);
        var savesBefore = instructions.Take(callIdx)
            .Where(i => i is IrInst.SaveArenaState).ToList();
        savesBefore.Count.ShouldBeGreaterThan(0,
            "SaveArenaState should appear before the first CallClosure.");

        var lastCallIdx = instructions.FindLastIndex(i => i is IrInst.CallClosure);
        var restoresAfter = instructions.Skip(lastCallIdx + 1)
            .Where(i => i is IrInst.RestoreArenaState).ToList();
        restoresAfter.Count.ShouldBeGreaterThan(0,
            "RestoreArenaState should appear after the last CallClosure for Int result.");
    }

    [Test]
    public void Call_returning_int_has_matching_save_restore_slots()
    {
        var ir = LowerProgram(
            """
            let inc = fun (x) -> x + 1
            in inc(5)
            """);
        var instructions = ir.EntryFunction.Instructions;
        var callIdx = instructions.FindIndex(i => i is IrInst.CallClosure);

        // Find the SaveArenaState immediately before the call chain
        var saveBefore = instructions.Take(callIdx)
            .OfType<IrInst.SaveArenaState>().Last();
        var lastCallIdx = instructions.FindLastIndex(i => i is IrInst.CallClosure);
        var restoreAfter = instructions.Skip(lastCallIdx + 1)
            .OfType<IrInst.RestoreArenaState>().First();

        saveBefore.CursorLocalSlot.ShouldBe(restoreAfter.CursorLocalSlot);
        saveBefore.EndLocalSlot.ShouldBe(restoreAfter.EndLocalSlot);
    }

    [Test]
    public void Call_returning_string_emits_CopyOutArena()
    {
        // toString returns String — per-call watermark should save+restore+copy-out.
        var ir = LowerProgram(
            """
            let toString = fun (x) -> "result"
            in toString(42)
            """);
        var instructions = ir.EntryFunction.Instructions;
        instructions.Any(i => i is IrInst.CallClosure).ShouldBeTrue(
            "Program should contain a CallClosure.");

        var lastCallIdx = instructions.FindLastIndex(i => i is IrInst.CallClosure);
        var afterCall = instructions.Skip(lastCallIdx + 1).ToList();
        afterCall.Any(i => i is IrInst.RestoreArenaState).ShouldBeTrue(
            "RestoreArenaState should appear after CallClosure for String result.");
        afterCall.Any(i => i is IrInst.CopyOutArena).ShouldBeTrue(
            "CopyOutArena should appear after CallClosure for String result.");

        // RestoreArenaState must precede CopyOutArena
        var restoreIdx = afterCall.FindIndex(i => i is IrInst.RestoreArenaState);
        var copyOutIdx = afterCall.FindIndex(i => i is IrInst.CopyOutArena);
        restoreIdx.ShouldBeLessThan(copyOutIdx,
            "RestoreArenaState must come before CopyOutArena.");
    }

    [Test]
    public void Call_returning_string_emits_ReclaimArenaChunks_after_CopyOutArena()
    {
        // Sequence should be: RestoreArenaState → CopyOutArena → ReclaimArenaChunks
        var ir = LowerProgram(
            """
            let toString = fun (x) -> "result"
            in toString(42)
            """);
        var instructions = ir.EntryFunction.Instructions;
        var lastCallIdx = instructions.FindLastIndex(i => i is IrInst.CallClosure);
        var afterCall = instructions.Skip(lastCallIdx + 1).ToList();

        var restoreIdx = afterCall.FindIndex(i => i is IrInst.RestoreArenaState);
        var copyOutIdx = afterCall.FindIndex(i => i is IrInst.CopyOutArena);
        var reclaimIdx = afterCall.FindIndex(i => i is IrInst.ReclaimArenaChunks);

        restoreIdx.ShouldBeGreaterThanOrEqualTo(0, "RestoreArenaState should be present.");
        copyOutIdx.ShouldBeGreaterThanOrEqualTo(0, "CopyOutArena should be present.");
        reclaimIdx.ShouldBeGreaterThanOrEqualTo(0, "ReclaimArenaChunks should be present.");

        restoreIdx.ShouldBeLessThan(copyOutIdx,
            "RestoreArenaState must come before CopyOutArena.");
        copyOutIdx.ShouldBeLessThan(reclaimIdx,
            "CopyOutArena must come before ReclaimArenaChunks (source must be readable before chunks are freed).");
    }

    [Test]
    public void Call_returning_int_emits_ReclaimArenaChunks_after_RestoreArenaState()
    {
        // Copy-type result: RestoreArenaState → ReclaimArenaChunks (no CopyOutArena).
        var ir = LowerProgram(
            """
            let inc = fun (x) -> x + 1
            in inc(5)
            """);
        var instructions = ir.EntryFunction.Instructions;
        var lastCallIdx = instructions.FindLastIndex(i => i is IrInst.CallClosure);
        var afterCall = instructions.Skip(lastCallIdx + 1).ToList();

        var restoreIdx = afterCall.FindIndex(i => i is IrInst.RestoreArenaState);
        var reclaimIdx = afterCall.FindIndex(i => i is IrInst.ReclaimArenaChunks);

        restoreIdx.ShouldBeGreaterThanOrEqualTo(0, "RestoreArenaState should be present.");
        reclaimIdx.ShouldBeGreaterThanOrEqualTo(0, "ReclaimArenaChunks should be present.");
        restoreIdx.ShouldBeLessThan(reclaimIdx,
            "RestoreArenaState must come before ReclaimArenaChunks.");
    }

    [Test]
    public void Call_returning_list_emits_RestoreArenaState_and_CopyOutList_after_call()
    {
        // identity function returning a list — per-call watermark should
        // emit RestoreArenaState + CopyOutList for deep cons-chain copy-out.
        var ir = LowerProgram(
            """
            let id = fun (xs) -> xs
            in id([1, 2, 3])
            """);
        var instructions = ir.EntryFunction.Instructions;
        var lastCallIdx = instructions.FindLastIndex(i => i is IrInst.CallClosure);
        lastCallIdx.ShouldBeGreaterThan(-1, "Program should contain a CallClosure.");

        var afterCallInstructions = instructions.Skip(lastCallIdx + 1).ToList();
        afterCallInstructions.Any(i => i is IrInst.RestoreArenaState).ShouldBeTrue(
            "List result should trigger per-call RestoreArenaState after CallClosure.");
        afterCallInstructions.Any(i => i is IrInst.CopyOutList).ShouldBeTrue(
            "List result should trigger per-call CopyOutList after CallClosure.");
    }

    [Test]
    public void Call_returning_closure_does_not_emit_CopyOutClosure_after_call()
    {
        // Closure copy-out is disabled until escape analysis is implemented,
        // because env may contain heap pointers that would dangle after reclaim.
        // Arena restoration should still occur for non-copy-out-eligible types.
        var ir = LowerProgram(
            """
            let add = fun (x) -> fun (y) -> x + y
            in let adder = add(5)
            in adder(10)
            """);
        var instructions = ir.EntryFunction.Instructions;

        // Find the first CallClosure (partial application: add(5))
        var firstCallIdx = instructions.FindIndex(i => i is IrInst.CallClosure);
        firstCallIdx.ShouldBeGreaterThan(-1);

        var afterFirstCall = instructions.Skip(firstCallIdx + 1).ToList();
        afterFirstCall.Any(i => i is IrInst.CopyOutClosure).ShouldBeFalse(
            "Closure result should NOT trigger per-call CopyOutClosure.");
    }

    // --- Helpers ---

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    /// <summary>
    /// Finds the lifted function containing the TCO tail-call jump (the actual TCO loop function).
    /// Identifies by the presence of a Jump instruction targeting a <c>_body</c> label.
    /// <para>
    /// Note: This relies on the label naming convention in <c>Lowering.cs</c> where TCO body
    /// labels are named <c>{functionLabel}_body</c>. If that convention changes, this helper
    /// must be updated accordingly.
    /// </para>
    /// </summary>
    private static IrFunction FindTcoFunction(IrProgram ir)
    {
        return ir.Functions.First(f =>
            f.Instructions.Any(i => i is IrInst.Jump j && j.Target.Contains("_body")));
    }

    private static bool HasSaveArenaState(List<IrInst> instructions)
    {
        return instructions.Any(i => i is IrInst.SaveArenaState);
    }

    private static bool HasRestoreArenaState(List<IrInst> instructions)
    {
        return instructions.Any(i => i is IrInst.RestoreArenaState);
    }

    private static bool HasCopyOutArena(List<IrInst> instructions)
    {
        return instructions.Any(i => i is IrInst.CopyOutArena);
    }

    private static bool HasDropInstruction(List<IrInst> instructions, string typeName)
    {
        return instructions.Any(i => i is IrInst.Drop d && d.TypeName == typeName);
    }
}
