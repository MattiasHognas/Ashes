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

    // --- Lifetime marker and resource cleanup insertion ---

    [Test]
    public void String_binding_emits_rc_drop()
    {
        var ir = LowerProgram("let s = \"hello\" in Ashes.IO.print(s)");
        HasRcDropInstruction(ir.EntryFunction.Instructions, "String").ShouldBeTrue();
    }

    [Test]
    public void List_binding_emits_rc_drop()
    {
        var ir = LowerProgram("let xs = [1, 2, 3] in Ashes.IO.print(1)");
        HasRcDropInstruction(ir.EntryFunction.Instructions, "List").ShouldBeTrue();
    }

    [Test]
    public void Function_binding_emits_resource_cleanup()
    {
        var ir = LowerProgram("let f = given (x) -> x + 1 in Ashes.IO.print(f(42))");
        HasCleanupResourceInstruction(ir.EntryFunction.Instructions, "Function").ShouldBeTrue();
    }

    [Test]
    public void Tuple_binding_emits_rc_drop()
    {
        var ir = LowerProgram("let t = (1, 2) in Ashes.IO.print(1)");
        HasRcDropInstruction(ir.EntryFunction.Instructions, "Tuple").ShouldBeTrue();
    }

    [Test]
    public void Result_adt_binding_emits_rc_drop()
    {
        // Ashes.IO.File.exists returns Result(Str, Bool) — an ADT
        var ir = LowerProgram("let r = Ashes.IO.File.exists(\"test.txt\") in Ashes.IO.print(1)");
        HasRcDropInstruction(ir.EntryFunction.Instructions, "Result").ShouldBeTrue();
    }

    [Test]
    public void Local_copy_record_field_reads_use_runtime_rc()
    {
        IrProgram ir = LowerProgram("type Point = | x: Int | y: Int\nlet p = Point(x = 40, y = 2) in Ashes.IO.print(p.x + p.y)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Point", RuntimeManaged: true }).ShouldBeTrue();
        int runtimeValue = ir.EntryFunction.Instructions
            .OfType<IrInst.AllocAdt>()
            .Single(allocation => allocation.RuntimeManaged)
            .Target;
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena { SrcTemp: var source } && source == runtimeValue).ShouldBeFalse();
    }

    [Test]
    public void Local_pointer_record_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Box = | value: String\nlet box = Box(value = \"hello\") in Ashes.IO.print(1)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Captured_copy_record_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Point = | x: Int | y: Int\nlet p = Point(x = 40, y = 2) in let read = given (u) -> p.x in Ashes.IO.print(read(0))");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Point", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Fresh_nested_copy_records_emit_recursive_runtime_drops()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | bonus: Int\nlet node = Node(child = Leaf(value = 40), bonus = 2) in Ashes.IO.print(node.bonus)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeTrue();
    }

    [Test]
    public void Unsupported_outer_record_keeps_fresh_nested_record_on_arena()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | label: String\nlet node = Node(child = Leaf(value = 40), label = \"answer\") in Ashes.IO.print(node.label)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Existing_child_binding_keeps_parent_and_child_on_arena()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | bonus: Int\nlet leaf = Leaf(value = 40) in let node = Node(child = leaf, bonus = 2) in Ashes.IO.print(node.bonus)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Nested_runtime_record_is_dropped_before_tco_back_edge()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | bonus: Int\nlet recursive loop n total = if n <= 0 then total else let node = Node(child = Leaf(value = 40), bonus = 2) in loop(n - 1)(total + node.bonus)\nAshes.IO.print(loop(3)(0))");

        IrFunction loop = ir.Functions.Single(function => function.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }));
        int backEdge = loop.Instructions.FindLastIndex(inst => inst is IrInst.Jump);
        backEdge.ShouldBeGreaterThan(0);
        loop.Instructions.Take(backEdge).Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void Ordinary_heap_binding_emits_rc_drop_not_resource_cleanup()
    {
        var ir = LowerProgram("let s = \"hello\" in Ashes.IO.print(s)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "String" }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CleanupResource { TypeName: "String" }).ShouldBeFalse();
    }

    [Test]
    public void Closure_binding_emits_resource_cleanup_not_rc_drop()
    {
        var ir = LowerProgram("let f = given (x) -> x + 1 in Ashes.IO.print(f(42))");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CleanupResource { TypeName: "Function" }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Function" }).ShouldBeFalse();
    }

    // --- Copy types do not get lifetime markers or cleanup ---

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
        // Both s1 and s2 should get RcDrop("String")
        var dropCount = insts.Count(i => i is IrInst.RcDrop d && string.Equals(d.TypeName, "String", StringComparison.Ordinal));
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
        HasRcDropInstruction(insts, "String").ShouldBeTrue("String binding should be dropped.");
        // Int binding should not produce an RcDrop
        var intDropCount = insts.Count(i => i is IrInst.RcDrop d && string.Equals(d.TypeName, "Int", StringComparison.Ordinal));
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
        var dropCount = insts.Count(i => i is IrInst.RcDrop d && string.Equals(d.TypeName, "String", StringComparison.Ordinal));
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
        var dropCount = insts.Count(i => i is IrInst.RcDrop d && string.Equals(d.TypeName, "String", StringComparison.Ordinal));
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
        var dropCount = insts.Count(i => i is IrInst.RcDrop d && string.Equals(d.TypeName, "String", StringComparison.Ordinal));
        dropCount.ShouldBe(2, "Non-alias fresh values should each get their own Drop.");
    }

    // --- Drop in match arms ---

    [Test]
    public void Owned_pattern_binding_in_match_gets_drop()
    {
        var ir = LowerProgram(
            """
            match Ashes.IO.File.readText("test.txt") with
                | Ok(content) -> Ashes.IO.print(content)
                | Error(msg) -> Ashes.IO.print(msg)
            """);
        var insts = ir.EntryFunction.Instructions;
        // The Ok(content) and Error(msg) bindings are String-typed — should get drops
        HasRcDropInstruction(insts, "String").ShouldBeTrue();
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
        var dummyDecl = new TypeDecl("Maybe", [new TypeParameter("a")], []);
        var typeSymbol = new TypeSymbol("Maybe", [new TypeParameterSymbol("a")], [], dummyDecl);
        var namedType = new TypeRef.TNamedType(typeSymbol, [new TypeRef.TInt()]);
        BuiltinRegistry.IsOwnedType(namedType).ShouldBeTrue();
    }

    [Test]
    public void Socket_binding_still_gets_close_drop()
    {
        var ir = LowerProgram(
            """
            Ashes.IO.print(match await Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(msg) -> msg
                | Ok(sock) -> "connected")
            """);
        HasCleanupResourceInstruction(ir, "Socket").ShouldBeTrue();
    }

    // --- Lifetime and cleanup IR instruction structure ---

    [Test]
    public void Rc_drop_instruction_has_type_name_field()
    {
        var drop = new IrInst.RcDrop(5, "String");
        drop.SourceTemp.ShouldBe(5);
        drop.TypeName.ShouldBe("String");
    }

    [Test]
    public void Rc_drop_instruction_for_list()
    {
        var drop = new IrInst.RcDrop(3, "List");
        drop.TypeName.ShouldBe("List");
    }

    [Test]
    public void Cleanup_resource_instruction_for_function()
    {
        var drop = new IrInst.CleanupResource(7, "Function");
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

    private static bool HasRcDropInstruction(List<IrInst> instructions, string typeName)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.RcDrop drop && string.Equals(drop.TypeName, typeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool HasCleanupResourceInstruction(List<IrInst> instructions, string typeName)
    {
        return instructions.Any(inst => inst is IrInst.CleanupResource cleanup
            && string.Equals(cleanup.TypeName, typeName, StringComparison.Ordinal));
    }

    private static bool HasCleanupResourceInstruction(IrProgram program, string typeName)
    {
        if (HasCleanupResourceInstruction(program.EntryFunction.Instructions, typeName))
        {
            return true;
        }

        foreach (var func in program.Functions)
        {
            if (HasCleanupResourceInstruction(func.Instructions, typeName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnyDropInstruction(List<IrInst> instructions)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.RcDrop or IrInst.CleanupResource)
                return true;
        }
        return false;
    }
}
