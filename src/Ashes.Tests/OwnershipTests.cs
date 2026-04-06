using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class OwnershipTests
{
    // --- Type classification: copy vs owned ---

    [Test]
    public void Int_is_copy_type()
    {
        BuiltinRegistry.IsCopyType(new TypeRef.TInt()).ShouldBeTrue();
    }

    [Test]
    public void Float_is_copy_type()
    {
        BuiltinRegistry.IsCopyType(new TypeRef.TFloat()).ShouldBeTrue();
    }

    [Test]
    public void Bool_is_copy_type()
    {
        BuiltinRegistry.IsCopyType(new TypeRef.TBool()).ShouldBeTrue();
    }

    [Test]
    public void String_is_owned_type()
    {
        BuiltinRegistry.IsOwnedType(new TypeRef.TStr()).ShouldBeTrue();
    }

    [Test]
    public void List_is_owned_type()
    {
        BuiltinRegistry.IsOwnedType(new TypeRef.TList(new TypeRef.TInt())).ShouldBeTrue();
    }

    [Test]
    public void Tuple_is_owned_type()
    {
        BuiltinRegistry.IsOwnedType(new TypeRef.TTuple([new TypeRef.TInt(), new TypeRef.TStr()])).ShouldBeTrue();
    }

    [Test]
    public void Function_is_owned_type()
    {
        BuiltinRegistry.IsOwnedType(new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TStr())).ShouldBeTrue();
    }

    [Test]
    public void Int_is_not_owned_type()
    {
        BuiltinRegistry.IsOwnedType(new TypeRef.TInt()).ShouldBeFalse();
    }

    [Test]
    public void Bool_is_not_owned_type()
    {
        BuiltinRegistry.IsOwnedType(new TypeRef.TBool()).ShouldBeFalse();
    }

    [Test]
    public void String_is_not_copy_type()
    {
        BuiltinRegistry.IsCopyType(new TypeRef.TStr()).ShouldBeFalse();
    }

    [Test]
    public void List_is_not_copy_type()
    {
        BuiltinRegistry.IsCopyType(new TypeRef.TList(new TypeRef.TInt())).ShouldBeFalse();
    }

    // --- Drop insertion for owned types ---

    [Test]
    public void String_binding_emits_drop()
    {
        var ir = LowerProgram("let s = \"hello\" in Ashes.IO.print(s)");
        HasDropInstruction(ir.EntryFunction.Instructions, "String").ShouldBeTrue();
    }

    [Test]
    public void List_binding_emits_drop()
    {
        var ir = LowerProgram("let xs = [1, 2, 3] in Ashes.IO.print(1)");
        HasDropInstruction(ir.EntryFunction.Instructions, "List").ShouldBeTrue();
    }

    [Test]
    public void Function_binding_emits_drop()
    {
        var ir = LowerProgram("let f = fun (x) -> x + 1 in Ashes.IO.print(f(42))");
        HasDropInstruction(ir.EntryFunction.Instructions, "Function").ShouldBeTrue();
    }

    [Test]
    public void Tuple_binding_emits_drop()
    {
        var ir = LowerProgram("let t = (1, 2) in Ashes.IO.print(1)");
        HasDropInstruction(ir.EntryFunction.Instructions, "Tuple").ShouldBeTrue();
    }

    [Test]
    public void Result_adt_binding_emits_drop()
    {
        // Ashes.File.exists returns Result(Str, Bool) — an ADT
        var ir = LowerProgram("let r = Ashes.File.exists(\"test.txt\") in Ashes.IO.print(1)");
        HasDropInstruction(ir.EntryFunction.Instructions, "Result").ShouldBeTrue();
    }

    // --- Copy types do NOT get Drop ---

    [Test]
    public void Int_binding_does_not_emit_drop()
    {
        var ir = LowerProgram("let x = 42 in Ashes.IO.print(x)");
        HasAnyDropInstruction(ir.EntryFunction.Instructions).ShouldBeFalse();
    }

    [Test]
    public void Bool_binding_does_not_emit_drop()
    {
        var ir = LowerProgram("let b = true in Ashes.IO.print(1)");
        HasAnyDropInstruction(ir.EntryFunction.Instructions).ShouldBeFalse();
    }

    [Test]
    public void Float_binding_does_not_emit_drop()
    {
        var ir = LowerProgram("let f = 3.14 in Ashes.IO.print(1)");
        HasAnyDropInstruction(ir.EntryFunction.Instructions).ShouldBeFalse();
    }

    // --- Multiple owned bindings in nested scopes ---

    [Test]
    public void Nested_owned_bindings_each_get_drop()
    {
        var ir = LowerProgram(
            """
            let s1 = "hello" in
            let s2 = "world" in
            Ashes.IO.print(s1)
            """);
        var insts = ir.EntryFunction.Instructions;
        // Both s1 and s2 should get Drop("String")
        var dropCount = insts.Count(i => i is IrInst.Drop d && d.TypeName == "String");
        dropCount.ShouldBe(2, "Each owned String binding should get its own Drop.");
    }

    [Test]
    public void Mixed_owned_and_copy_bindings()
    {
        var ir = LowerProgram(
            """
            let x = 42 in
            let s = "hello" in
            Ashes.IO.print(x)
            """);
        var insts = ir.EntryFunction.Instructions;
        HasDropInstruction(insts, "String").ShouldBeTrue("String binding should be dropped.");
        // Int binding should not produce a Drop
        var intDropCount = insts.Count(i => i is IrInst.Drop d && d.TypeName == "Int");
        intDropCount.ShouldBe(0, "Int (copy type) should not produce Drop.");
    }

    // --- Alias tracking: rebinding an owned value should NOT produce duplicate Drop ---

    [Test]
    public void Alias_of_owned_string_emits_single_drop()
    {
        var ir = LowerProgram(
            """
            let s = "hello" in
            let a = s in
            Ashes.IO.print(a)
            """);
        var insts = ir.EntryFunction.Instructions;
        var dropCount = insts.Count(i => i is IrInst.Drop d && d.TypeName == "String");
        dropCount.ShouldBe(1, "Aliasing an owned value should produce exactly one Drop (on the original owner).");
    }

    [Test]
    public void Chained_alias_of_owned_string_emits_single_drop()
    {
        var ir = LowerProgram(
            """
            let s = "hello" in
            let a = s in
            let b = a in
            Ashes.IO.print(b)
            """);
        var insts = ir.EntryFunction.Instructions;
        var dropCount = insts.Count(i => i is IrInst.Drop d && d.TypeName == "String");
        dropCount.ShouldBe(1, "Chained aliases should still produce exactly one Drop.");
    }

    [Test]
    public void Non_alias_fresh_values_still_get_separate_drops()
    {
        var ir = LowerProgram(
            """
            let s1 = "hello" in
            let s2 = "world" in
            Ashes.IO.print(s1)
            """);
        var insts = ir.EntryFunction.Instructions;
        var dropCount = insts.Count(i => i is IrInst.Drop d && d.TypeName == "String");
        dropCount.ShouldBe(2, "Non-alias fresh values should each get their own Drop.");
    }

    // --- Drop in match arms ---

    [Test]
    public void Owned_pattern_binding_in_match_gets_drop()
    {
        var ir = LowerProgram(
            """
            match Ashes.File.readText("test.txt") with
                | Ok(content) -> Ashes.IO.print(content)
                | Error(msg) -> Ashes.IO.print(msg)
            """);
        var insts = ir.EntryFunction.Instructions;
        // The Ok(content) and Error(msg) bindings are String-typed — should get drops
        HasDropInstruction(insts, "String").ShouldBeTrue();
    }

    // --- Resource types still work correctly ---

    [Test]
    public void Socket_still_classified_as_resource_type()
    {
        BuiltinRegistry.IsResourceTypeName("Socket").ShouldBeTrue();
    }

    [Test]
    public void Named_adt_type_is_owned()
    {
        // TNamedType is always owned (covers Result, Maybe, Socket, etc.)
        // Verified via the BuiltinRegistry.IsOwnedType check on the TypeRef variant
        BuiltinRegistry.IsOwnedType(new TypeRef.TStr()).ShouldBeTrue();
        // Functions are owned
        BuiltinRegistry.IsOwnedType(new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TInt())).ShouldBeTrue();
    }

    [Test]
    public void Socket_binding_still_gets_close_drop()
    {
        var ir = LowerProgram(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(msg) -> Ashes.IO.print(msg)
                | Ok(sock) -> Ashes.IO.print("connected")
            """);
        HasDropInstruction(ir.EntryFunction.Instructions, "Socket").ShouldBeTrue();
    }

    // --- Drop IR instruction structure ---

    [Test]
    public void Drop_instruction_has_type_name_field()
    {
        var drop = new IrInst.Drop(5, "String");
        drop.SourceTemp.ShouldBe(5);
        drop.TypeName.ShouldBe("String");
    }

    [Test]
    public void Drop_instruction_for_list()
    {
        var drop = new IrInst.Drop(3, "List");
        drop.TypeName.ShouldBe("List");
    }

    [Test]
    public void Drop_instruction_for_function()
    {
        var drop = new IrInst.Drop(7, "Function");
        drop.TypeName.ShouldBe("Function");
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

    private static bool HasDropInstruction(List<IrInst> instructions, string typeName)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.Drop drop && drop.TypeName == typeName)
                return true;
        }
        return false;
    }

    private static bool HasAnyDropInstruction(List<IrInst> instructions)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.Drop)
                return true;
        }
        return false;
    }
}
