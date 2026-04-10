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
    public void RestoreArenaState_has_cursor_and_end_slots()
    {
        var restore = new IrInst.RestoreArenaState(5, 6);
        restore.CursorLocalSlot.ShouldBe(5);
        restore.EndLocalSlot.ShouldBe(6);
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
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Match expression with Int arms should emit RestoreArenaState.");
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
    public void Match_with_string_result_does_not_emit_RestoreArenaState()
    {
        var ir = LowerProgram(
            """
            match 1 with
                | 1 -> "yes"
                | _ -> "no"
            """);
        HasRestoreArenaState(ir.EntryFunction.Instructions).ShouldBeFalse(
            "Match expression with String arms should NOT emit RestoreArenaState.");
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

    private static bool HasSaveArenaState(List<IrInst> instructions)
    {
        return instructions.Any(i => i is IrInst.SaveArenaState);
    }

    private static bool HasRestoreArenaState(List<IrInst> instructions)
    {
        return instructions.Any(i => i is IrInst.RestoreArenaState);
    }

    private static bool HasDropInstruction(List<IrInst> instructions, string typeName)
    {
        return instructions.Any(i => i is IrInst.Drop d && d.TypeName == typeName);
    }
}
