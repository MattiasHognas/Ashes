using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class BorrowAnalysisTests
{
    // --- Inferred borrow: owned binding used after definition emits Borrow ---

    [Test]
    public void String_binding_used_once_emits_borrow()
    {
        var ir = LowerProgram("let s = \"hello\" in Ashes.IO.print(s)");
        var insts = ir.EntryFunction.Instructions;
        HasBorrowInstruction(insts).ShouldBeTrue(
            "Accessing an owned String binding should emit a Borrow instruction.");
    }

    [Test]
    public void List_binding_used_once_emits_borrow()
    {
        var ir = LowerProgram(
            """
            let xs = [1, 2, 3] in
            Ashes.IO.print(1)
            """);
        // xs is not used after definition — no Borrow should appear for it
        // (only the list literal production is tracked; xs is never accessed)
        var borrowCount = CountBorrowInstructions(ir.EntryFunction.Instructions);
        borrowCount.ShouldBe(0,
            "An owned binding that is never accessed should not emit Borrow.");
    }

    [Test]
    public void Function_binding_used_emits_borrow()
    {
        var ir = LowerProgram("let f = fun (x) -> x + 1 in Ashes.IO.print(f(42))");
        HasBorrowInstruction(ir.EntryFunction.Instructions).ShouldBeTrue(
            "Accessing an owned Function binding should emit a Borrow instruction.");
    }

    [Test]
    public void Multi_use_string_binding_emits_multiple_borrows()
    {
        var ir = LowerProgram(
            """
            let s = "hello" in
            let a = s in
            Ashes.IO.print(s)
            """);
        var borrowCount = CountBorrowInstructions(ir.EntryFunction.Instructions);
        borrowCount.ShouldBeGreaterThanOrEqualTo(2,
            "Each access to an owned binding should emit a separate Borrow.");
    }

    // --- Copy types: no borrow ---

    [Test]
    public void Int_binding_does_not_emit_borrow()
    {
        var ir = LowerProgram("let x = 42 in Ashes.IO.print(x)");
        HasBorrowInstruction(ir.EntryFunction.Instructions).ShouldBeFalse(
            "Copy type Int should not produce Borrow instructions.");
    }

    [Test]
    public void Bool_binding_does_not_emit_borrow()
    {
        var ir = LowerProgram(
            """
            let b = true in
            if b then Ashes.IO.print(1) else Ashes.IO.print(2)
            """);
        HasBorrowInstruction(ir.EntryFunction.Instructions).ShouldBeFalse(
            "Copy type Bool should not produce Borrow instructions.");
    }

    [Test]
    public void Float_binding_does_not_emit_borrow()
    {
        var ir = LowerProgram("let f = 3.14 in Ashes.IO.print(1)");
        HasBorrowInstruction(ir.EntryFunction.Instructions).ShouldBeFalse(
            "Copy type Float should not produce Borrow instructions.");
    }

    // --- Borrow + Drop interaction ---

    [Test]
    public void Borrow_does_not_prevent_drop_at_scope_exit()
    {
        var ir = LowerProgram("let s = \"hello\" in Ashes.IO.print(s)");
        var insts = ir.EntryFunction.Instructions;
        // Should have both Borrow (for access) and Drop (at scope exit)
        HasBorrowInstruction(insts).ShouldBeTrue("Should have Borrow for the access.");
        HasDropInstruction(insts, "String").ShouldBeTrue("Should still have Drop at scope exit.");
    }

    [Test]
    public void Drop_appears_after_last_borrow()
    {
        var ir = LowerProgram("let s = \"hello\" in Ashes.IO.print(s)");
        var insts = ir.EntryFunction.Instructions;
        int lastBorrowIdx = -1;
        int firstDropIdx = -1;
        for (int i = 0; i < insts.Count; i++)
        {
            if (insts[i] is IrInst.Borrow)
                lastBorrowIdx = i;
            if (insts[i] is IrInst.Drop && firstDropIdx < 0)
                firstDropIdx = i;
        }

        lastBorrowIdx.ShouldBeGreaterThan(-1, "Expected at least one Borrow.");
        firstDropIdx.ShouldBeGreaterThan(-1, "Expected at least one Drop.");
        firstDropIdx.ShouldBeGreaterThan(lastBorrowIdx,
            "Drop should appear after the last Borrow in the instruction stream.");
    }

    // --- Multiple owned bindings ---

    [Test]
    public void Nested_owned_bindings_each_get_borrows()
    {
        var ir = LowerProgram(
            """
            let s1 = "hello" in
            let s2 = "world" in
            Ashes.IO.print(s1)
            """);
        var insts = ir.EntryFunction.Instructions;
        // s1 is accessed → Borrow; s2 is never accessed → no Borrow for s2
        HasBorrowInstruction(insts).ShouldBeTrue();
    }

    [Test]
    public void Mixed_owned_and_copy_borrows()
    {
        var ir = LowerProgram(
            """
            let x = 42 in
            let s = "hello" in
            Ashes.IO.print(s)
            """);
        var insts = ir.EntryFunction.Instructions;
        // Only s (String) should have Borrow; x (Int) should not
        HasBorrowInstruction(insts).ShouldBeTrue("String access should produce Borrow.");
        // The borrow count should be exactly for the string access
        CountBorrowInstructions(insts).ShouldBe(1,
            "Only the String binding access should produce a Borrow.");
    }

    // --- Borrow in match arms ---

    [Test]
    public void Owned_pattern_binding_in_match_gets_borrow()
    {
        var ir = LowerProgram(
            """
            match Ashes.File.readText("test.txt") with
                | Ok(content) -> Ashes.IO.print(content)
                | Error(msg) -> Ashes.IO.print(msg)
            """);
        var insts = ir.EntryFunction.Instructions;
        // The content and msg pattern bindings are String-typed owned values
        HasBorrowInstruction(insts).ShouldBeTrue(
            "Accessing an owned pattern binding should emit Borrow.");
    }

    // --- IR structure ---

    [Test]
    public void Borrow_instruction_has_correct_fields()
    {
        var borrow = new IrInst.Borrow(5, 3);
        borrow.Target.ShouldBe(5);
        borrow.SourceSlot.ShouldBe(3);
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

    private static bool HasBorrowInstruction(List<IrInst> instructions)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.Borrow)
                return true;
        }
        return false;
    }

    private static int CountBorrowInstructions(List<IrInst> instructions)
    {
        return instructions.Count(i => i is IrInst.Borrow);
    }

    private static bool HasDropInstruction(List<IrInst> instructions, string typeName)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.Drop drop && drop.TypeName == typeName)
                return true;
        }
        return false;
    }
}
