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
    public void Local_concat_consumed_by_print_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let text = \"ab\" + \"cd\" in Ashes.IO.print(text)");

        int runtimeString = ir.EntryFunction.Instructions
            .OfType<IrInst.ConcatStr>()
            .Single(concat => concat.RuntimeManaged)
            .Target;
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.CopyOutArena { SrcTemp: var source } && source == runtimeString).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_scratch_free_concat_transfers_runtime_ownership_without_copy_out()
    {
        IrProgram ir = LowerProgram("let escaped = (let text = \"ab\" + \"cd\" in text) in Ashes.Text.byteLength(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_concat_reclaims_nested_string_producer_scratch()
    {
        IrProgram ir = LowerProgram("let escaped = (let text = \"value-\" + Ashes.Text.fromInt(42) in text) in Ashes.Text.byteLength(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RestoreArenaState).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_text_from_int_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let text = Ashes.Text.fromInt(-42) in text) in Ashes.Text.byteLength(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_scalar_text_conversions_transfer_runtime_ownership()
    {
        IrProgram hex = LowerProgram("let escaped = (let text = Ashes.Text.toHex(48879) in text) in Ashes.Text.byteLength(escaped)");
        IrProgram floating = LowerProgram("let escaped = (let text = Ashes.Text.fromFloat(12.25) in text) in Ashes.Text.byteLength(escaped)");
        IrProgram fixedFloat = LowerProgram("let escaped = (let text = Ashes.Text.formatFloat(12.25)(3) in text) in Ashes.Text.byteLength(escaped)");

        hex.EntryFunction.Instructions.Any(inst => inst is IrInst.TextToHex { RuntimeManaged: true }).ShouldBeTrue();
        floating.EntryFunction.Instructions.Any(inst => inst is IrInst.TextFromFloat { RuntimeManaged: true }).ShouldBeTrue();
        fixedFloat.EntryFunction.Instructions.Any(inst => inst is IrInst.TextFormatFloat { RuntimeManaged: true }).ShouldBeTrue();
        foreach (IrProgram ir in new[] { hex, floating, fixedFloat })
        {
            ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
            ir.EntryFunction.Instructions.Any(inst =>
                inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        }
    }

    [Test]
    public void Directly_escaping_text_copies_and_bigint_text_transfer_runtime_ownership()
    {
        IrProgram upper = LowerProgram("let escaped = (let text = Ashes.Text.asciiUpper(\"hello\") in text) in Ashes.Text.byteLength(escaped)");
        IrProgram subText = LowerProgram("let escaped = (let text = Ashes.Byte.subText(Ashes.Byte.fromText(\"abcdef\"))(1)(3) in text) in Ashes.Text.byteLength(escaped)");
        IrProgram bigInt = LowerProgram("let escaped = (let text = Ashes.Text.fromBigInt(42N) in text) in Ashes.Text.byteLength(escaped)");

        upper.EntryFunction.Instructions.Any(inst => inst is IrInst.TextAsciiCase { RuntimeManaged: true }).ShouldBeTrue();
        subText.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
        bigInt.EntryFunction.Instructions.Any(inst => inst is IrInst.BigIntToString { RuntimeManaged: true }).ShouldBeTrue();
        foreach (IrProgram ir in new[] { upper, subText, bigInt })
        {
            ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
            ir.EntryFunction.Instructions.Any(inst =>
                inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        }
    }

    [Test]
    public void Direct_known_function_result_transfers_runtime_string_ownership_without_copy_out()
    {
        IrProgram ir = LowerProgram(
            "let make = given (unit) -> (let text = \"ab\" + \"cd\" in text) in let value = make(0) in Ashes.Text.byteLength(value)");

        ir.Functions.SelectMany(function => function.Instructions).Any(inst =>
            inst is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CallClosure).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Saturated_curried_known_function_result_transfers_runtime_string_ownership_without_copy_out()
    {
        IrProgram ir = LowerProgram(
            "let make : Str -> Str -> Str = given (left) -> given (right) -> (let ignored = Ashes.Text.byteLength(left) in let text = \"ab\" + \"cd\" in text) in let value = make(\"left\")(\"right\") in Ashes.Text.byteLength(value)");

        ir.Functions.SelectMany(function => function.Instructions).Any(inst =>
            inst is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.CallClosure or IrInst.CallKnown).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Direct_known_function_results_transfer_runtime_bytes_and_bigint_ownership()
    {
        IrProgram bytes = LowerProgram(
            "let make = given (unit) -> (let value = Ashes.Byte.u64Le(72623859790382856u64) in value) in let value = make(0) in Ashes.Byte.length(value)");
        IrProgram bigInt = LowerProgram(
            "let make = given (number) -> (let value = Ashes.Number.BigInt.fromInt(number) in value) in let value = make(42) in Ashes.Number.BigInt.compare(value)(value)");

        bytes.Functions.SelectMany(function => function.Instructions).Any(inst =>
            inst is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
        bytes.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        bytes.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        bigInt.Functions.SelectMany(function => function.Instructions).Any(inst =>
            inst is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        bigInt.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        bigInt.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_bigint_arithmetic_reclaims_operand_scratch()
    {
        IrProgram ir = LowerProgram("let escaped = (let value = Ashes.Number.BigInt.add(Ashes.Number.BigInt.fromInt(40))(2N) in value) in Ashes.Number.BigInt.compare(escaped)(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BigIntBinary { Op: "add", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RestoreArenaState).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_scalar_result_containers_transfer_runtime_ownership()
    {
        IrProgram parsedInt = LowerProgram("let escaped = (let parsed = Ashes.Text.parseInt(\"123\") in parsed) in 1");
        IrProgram parsedFloat = LowerProgram("let escaped = (let parsed = Ashes.Text.parseFloat(\"1.5\") in parsed) in 1");
        IrProgram convertedBigInt = LowerProgram("let escaped = (let converted = Ashes.Number.BigInt.toInt(123N) in converted) in 1");

        parsedInt.EntryFunction.Instructions.Any(inst => inst is IrInst.TextParseInt { RuntimeManaged: true }).ShouldBeTrue();
        parsedFloat.EntryFunction.Instructions.Any(inst => inst is IrInst.TextParseFloat { RuntimeManaged: true }).ShouldBeTrue();
        convertedBigInt.EntryFunction.Instructions.Any(inst => inst is IrInst.BigIntToInt { RuntimeManaged: true }).ShouldBeTrue();
        foreach (IrProgram ir in new[] { parsedInt, parsedFloat, convertedBigInt })
        {
            ir.EntryFunction.Instructions.Any(inst =>
                inst is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();
            ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        }
    }

    [Test]
    public void Directly_escaping_bigint_parse_result_transfers_child_aware_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let parsed = Ashes.Text.parseBigInt(\"123\") in parsed) in 1");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BigIntFromString { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_copy_field_adt_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("type Pair = | Pair(Int, Int)\nlet escaped = (let pair = Pair(40)(2) in pair) in match escaped with | Pair(left, right) -> left + right");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Pair", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_fresh_recursive_adt_transfers_child_ownership()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet escaped = (let tree = Node(Node(Leaf)(20)(Leaf))(42)(Leaf) in tree) in match escaped with | Leaf -> 0 | Node(left, value, _) -> match left with | Leaf -> value | Node(_, childValue, _) -> value + childValue");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(5);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CallKnown { FuncLabel: var label }
            && label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBeTrue();
        ir.Functions.Any(function => function.Label.StartsWith("__rcdrop_", StringComparison.Ordinal)
            && function.Instructions.Any(inst => inst is IrInst.RcIsUnique)
            && function.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tree", RuntimeManaged: true })).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_recursive_adt_with_borrowed_child_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet child = Node(Leaf)(20)(Leaf) in let escaped = (let tree = Node(child)(42)(Leaf) in tree) in match escaped with | Leaf -> 0 | Node(_, value, _) -> value");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(0);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tree", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Direct_function_alias_preserves_runtime_result_ownership_provenance()
    {
        IrProgram ir = LowerProgram(
            "let make = given (unit) -> (let text = \"ab\" + \"cd\" in text) in let alias = make in let value = alias(0) in Ashes.Text.byteLength(value)");

        ir.Functions.SelectMany(function => function.Instructions).Any(inst =>
            inst is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CallClosure).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Captured_function_alias_preserves_runtime_result_ownership_provenance()
    {
        IrProgram ir = LowerProgram(
            "let make = given (unit) -> (let text = \"ab\" + \"cd\" in text) in let alias = make in let invoke = given (unit) -> (let value = alias(0) in Ashes.Text.byteLength(value)) in invoke(0)");

        ir.Functions.SelectMany(function => function.Instructions).Any(inst =>
            inst is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        ir.Functions.Any(function =>
            function.Instructions.Any(inst => inst is IrInst.CallClosure)
            && function.Instructions.Any(inst =>
                inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true })
            && function.Instructions.All(inst => inst is not IrInst.CopyOutArena)).ShouldBeTrue();
    }

    [Test]
    public void Local_bytes_append_consumed_by_length_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.append(Ashes.Byte.fromText(\"ab\"))(Ashes.Byte.fromText(\"cd\")) in Ashes.Byte.length(bytes)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_scratch_free_bytes_append_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let bytes = Ashes.Byte.append(Ashes.Byte.fromText(\"ab\"))(Ashes.Byte.fromText(\"cd\")) in bytes) in Ashes.Byte.length(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Escaping_bytes_append_with_allocating_operand_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.append(Ashes.Byte.fromList([1u8, 2u8]))(Ashes.Byte.fromText(\"cd\")) in bytes");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Local_append_byte_consumed_by_length_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText(\"ab\"))(33u8) in Ashes.Byte.length(bytes)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_scratch_free_append_byte_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText(\"ab\"))(33u8) in bytes) in Ashes.Byte.length(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Escaping_append_byte_with_allocating_operand_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromList([1u8, 2u8]))(33u8) in bytes");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Local_bytes_from_list_consumed_by_length_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in Ashes.Byte.length(bytes)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_bytes_from_fresh_list_transfers_runtime_ownership_and_reclaims_scratch()
    {
        IrProgram ir = LowerProgram("let escaped = (let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in bytes) in Ashes.Byte.length(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RestoreArenaState).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Escaping_bytes_from_borrowed_list_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let values = [7u8, 8u8, 9u8] in let bytes = Ashes.Byte.fromList(values) in bytes");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Local_byte_singleton_consumed_by_length_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.singleton(7u8) in Ashes.Byte.length(bytes)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_byte_singleton_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let bytes = Ashes.Byte.singleton(7u8) in bytes) in Ashes.Byte.length(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Local_empty_bytes_consumed_by_length_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.empty(Unit) in Ashes.Byte.length(bytes)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesEmpty { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_empty_bytes_transfer_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let bytes = Ashes.Byte.empty(Unit) in bytes) in Ashes.Byte.length(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesEmpty { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Local_fixed_width_bytes_consumed_by_length_use_runtime_rc()
    {
        IrProgram u16 = LowerProgram("let bytes = Ashes.Byte.u16Le(258u16) in Ashes.Byte.length(bytes)");
        IrProgram u32 = LowerProgram("let bytes = Ashes.Byte.u32Le(16909060u32) in Ashes.Byte.length(bytes)");
        IrProgram u64 = LowerProgram("let bytes = Ashes.Byte.u64Le(72623859790382856u64) in Ashes.Byte.length(bytes)");

        u16.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
        u32.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU32Le { RuntimeManaged: true }).ShouldBeTrue();
        u64.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
        foreach (IrProgram ir in new[] { u16, u32, u64 })
        {
            ir.EntryFunction.Instructions.Any(inst =>
                inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        }
    }

    [Test]
    public void Directly_escaping_fixed_width_bytes_transfer_runtime_ownership()
    {
        IrProgram u16 = LowerProgram("let escaped = (let bytes = Ashes.Byte.u16Le(258u16) in bytes) in Ashes.Byte.length(escaped)");
        IrProgram u32 = LowerProgram("let escaped = (let bytes = Ashes.Byte.u32Le(16909060u32) in bytes) in Ashes.Byte.length(escaped)");
        IrProgram u64 = LowerProgram("let escaped = (let bytes = Ashes.Byte.u64Le(72623859790382856u64) in bytes) in Ashes.Byte.length(escaped)");

        u16.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
        u32.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU32Le { RuntimeManaged: true }).ShouldBeTrue();
        u64.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Local_byte_subtext_consumed_by_print_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let text = Ashes.Byte.subText(Ashes.Byte.fromText(\"abcdef\"))(1)(3) in Ashes.IO.print(text)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Directly_escaping_byte_subtext_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let text = Ashes.Byte.subText(Ashes.Byte.fromText(\"abcdef\"))(1)(3) in text) in Ashes.Text.byteLength(escaped)");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Escaping_byte_subtext_with_allocating_source_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let text = Ashes.Byte.subText(Ashes.Byte.fromText(Ashes.Text.fromInt(42)))(0)(1) in text");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeFalse();
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
    public void Directly_escaping_fresh_nested_records_transfer_runtime_ownership()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | bonus: Int\nlet escaped = (let node = Node(child = Leaf(value = 40), bonus = 2) in node) in escaped.bonus");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_string_field_record_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Box = | value: String\nlet escaped = (let box = Box(value = \"hello\") in box) in Ashes.IO.print(escaped.value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Unsupported_outer_record_keeps_fresh_nested_record_on_arena()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | label: String\nlet node = Node(child = Leaf(value = 40), label = \"answer\") in Ashes.IO.print(node.label)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Existing_runtime_record_child_moves_into_parent_without_dup()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | bonus: Int\nlet leaf = Leaf(value = 40) in let node = Node(child = leaf, bonus = 2) in Ashes.IO.print(node.bonus)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBe(2);
    }

    [Test]
    public void Existing_runtime_record_child_is_duped_when_original_remains_live()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Node = | child: Leaf | bonus: Int\nlet leaf = Leaf(value = 40) in let node = Node(child = leaf, bonus = 2) in Ashes.IO.print(node.bonus + leaf.value)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBe(3);
    }

    [Test]
    public void Existing_runtime_record_child_moves_into_pointer_variant()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Choice = | Empty | Full(Leaf, Int)\nlet leaf = Leaf(value = 40) in let choice = Full(leaf)(2) in match choice with | Empty -> Ashes.IO.print(0) | Full(_, bonus) -> Ashes.IO.print(bonus)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Leaf", RuntimeManaged: true }).ShouldBeTrue();
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
    public void Copy_only_user_adt_consumed_by_match_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("type Choice = | Left(Int) | Right(Int)\nlet choice = Left(42) in match choice with | Left(value) -> Ashes.IO.print(value) | Right(value) -> Ashes.IO.print(value + 1)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        int[] fieldReads = ir.EntryFunction.Instructions
            .Select((inst, index) => (inst, index))
            .Where(pair => pair.inst is IrInst.GetAdtField)
            .Select(pair => pair.index)
            .ToArray();
        fieldReads.Length.ShouldBe(2);
        foreach (int fieldRead in fieldReads)
        {
            ir.EntryFunction.Instructions[fieldRead + 1]
                .ShouldBeOfType<IrInst.RcDrop>()
                .RuntimeManaged.ShouldBeTrue();
        }
        ir.EntryFunction.Instructions.Count(inst =>
            inst is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBe(3);
    }

    [Test]
    public void Copy_only_nullary_user_adt_consumed_by_match_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("type Flag = | On | Off\nlet flag = On in match flag with | On -> Ashes.IO.print(1) | Off -> Ashes.IO.print(0)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        int tagRead = ir.EntryFunction.Instructions.FindIndex(inst => inst is IrInst.GetAdtTag);
        int firstDrop = ir.EntryFunction.Instructions.FindIndex(inst =>
            inst is IrInst.RcDrop { TypeName: "Flag", RuntimeManaged: true });
        firstDrop.ShouldBeGreaterThan(tagRead);
        ir.EntryFunction.Instructions.Count(inst =>
            inst is IrInst.RcDrop { TypeName: "Flag", RuntimeManaged: true }).ShouldBe(3);
    }

    [Test]
    public void Pointer_field_user_adt_consumed_by_match_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Choice = | Left(String) | Right(String)\nlet choice = Left(\"hello\") in match choice with | Left(value) -> Ashes.IO.print(value) | Right(value) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Fresh_record_child_variant_consumed_by_match_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Choice = | Empty | Full(Leaf, Int)\nlet choice = Full(Leaf(value = 40))(2) in match choice with | Empty -> Ashes.IO.print(0) | Full(_, value) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Leaf", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Unknown_record_child_variant_constructor_uses_heterogeneous_dropper()
    {
        IrProgram ir = LowerProgram("type Leaf = | value: Int\ntype Choice = | Empty | Full(Leaf, Int)\nlet rebuilt = let choice = Full(Leaf(value = 40))(2) in match choice with | Empty -> Empty | Full(child, value) -> Full(child)(value + 1) in Ashes.IO.print(1)");

        IrFunction dropper = ir.Functions.Single(function =>
            function.Label.StartsWith("__rcdrop_", StringComparison.Ordinal));
        dropper.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Leaf", RuntimeManaged: true }).ShouldBeTrue();
        dropper.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Fully_fresh_recursive_user_adt_consumed_by_match_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet tree = Node(Leaf)(42)(Leaf) in match tree with | Leaf -> Ashes.IO.print(0) | Node(_, value, _) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(3);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CallKnown { FuncLabel: var label }
            && label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBeTrue();
        IrFunction dropper = ir.Functions.Single(function => function.Label.StartsWith("__rcdrop_", StringComparison.Ordinal));
        dropper.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeTrue();
        dropper.Instructions.Any(inst => inst is IrInst.SwitchTag).ShouldBeTrue();
        dropper.Instructions.Count(inst => inst is IrInst.CallKnown { FuncLabel: var label }
            && string.Equals(label, dropper.Label, StringComparison.Ordinal)).ShouldBe(2);
        dropper.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tree", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Known_nullary_recursive_constructor_uses_specialized_drop()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet tree = Leaf in match tree with | Leaf -> Ashes.IO.print(0) | Node(_, value, _) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tree", RuntimeManaged: true }).ShouldBeTrue();
        ir.Functions.Any(function => function.Label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public void Known_recursive_node_specializes_root_but_keeps_child_dropper()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet tree = Node(Leaf)(42)(Leaf) in match tree with | Leaf -> Ashes.IO.print(0) | Node(_, value, _) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tree", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CallKnown { FuncLabel: var label }
            && label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBeTrue();
        ir.Functions.Any(function => function.Label.StartsWith("__rcdrop_", StringComparison.Ordinal)
            && function.Instructions.Any(inst => inst is IrInst.RcIsUnique)).ShouldBeTrue();
    }

    [Test]
    public void Recursive_user_adt_transfers_existing_runtime_child_without_dup()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet child = Leaf in let tree = Node(child)(42)(Leaf) in match tree with | Leaf -> Ashes.IO.print(0) | Node(_, value, _) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(3);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBeFalse();
        ir.Functions.Any(function => function.Label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Test]
    public void Recursive_user_adt_dups_existing_runtime_child_when_original_remains_live()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet child = Node(Leaf)(20)(Leaf) in let tree = Node(child)(42)(Leaf) in match tree with | Leaf -> Ashes.IO.print(0) | Node(_, value, _) -> match child with | Leaf -> Ashes.IO.print(value) | Node(_, childValue, _) -> Ashes.IO.print(value + childValue)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(5);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeTrue();
        ir.Functions.Any(function => function.Label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Test]
    public void Recursive_user_adt_reusing_one_child_in_two_fields_keeps_parent_on_arena()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet child = Leaf in let tree = Node(child)(42)(child) in match tree with | Leaf -> Ashes.IO.print(0) | Node(_, value, _) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(1);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Fully_fresh_copy_list_consumed_by_match_uses_runtime_rc()
    {
        IrProgram ir = LowerProgram("let values = [1, 2, 3] in match values with | [] -> Ashes.IO.print(0) | head :: _ -> Ashes.IO.print(head)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(3);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeFalse();
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBe(1);
    }

    [Test]
    public void Directly_escaping_fresh_copy_list_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let values = [40, 2] in values) in match escaped with | [] -> 0 | head :: _ -> head");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_pointer_element_list_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let escaped = (let values = [\"one\", \"two\"] in values) in match escaped with | [] -> Ashes.IO.print(\"empty\") | head :: _ -> Ashes.IO.print(head)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Pointer_element_list_consumed_by_match_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let values = [\"one\", \"two\"] in match values with | [] -> Ashes.IO.print(\"empty\") | head :: _ -> Ashes.IO.print(head)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Copy_list_transfers_existing_runtime_tail_without_dup()
    {
        IrProgram ir = LowerProgram("let tail = [2, 3] in let values = 1 :: tail in match values with | [] -> Ashes.IO.print(0) | head :: _ -> Ashes.IO.print(head)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(3);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Copy_list_dups_existing_runtime_tail_when_original_remains_live()
    {
        IrProgram ir = LowerProgram("let tail = [40, 2] in let values = 1 :: tail in match values with | [] -> Ashes.IO.print(0) | head :: _ -> match tail with | [] -> Ashes.IO.print(0) | tailHead :: _ -> Ashes.IO.print(head + tailHead)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(3);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Copy_list_with_used_tail_binding_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let values = [1, 2] in match values with | [] -> Ashes.IO.print(0) | _ :: tail -> match tail with | [] -> Ashes.IO.print(0) | head :: _ -> Ashes.IO.print(head)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Runtime_copy_list_is_dropped_before_each_tco_match_back_edge()
    {
        IrProgram ir = LowerProgram("let recursive loop n total = if n <= 0 then total else let values = [1, 2, 3] in match values with | [] -> loop(n - 1)(total) | head :: _ -> loop(n - 1)(total + head)\nAshes.IO.print(loop(3)(0))");

        IrFunction loop = ir.Functions.Single(function => function.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }));
        loop.Instructions.Count(inst => inst is IrInst.Jump { Target: var target }
            && target.EndsWith("_body", StringComparison.Ordinal)).ShouldBe(2);
        // Each arm has a reachable pre-back-edge drop. Unoptimized IR also retains lexical cleanup,
        // so three drops prove sibling lowering did not inherit the first arm's AutoDropped state.
        loop.Instructions.Count(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true })
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    [Test]
    public void Recursive_user_adt_with_used_child_binding_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet tree = Node(Leaf)(42)(Leaf) in match tree with | Leaf -> Ashes.IO.print(0) | Node(left, _, _) -> match left with | Leaf -> Ashes.IO.print(1) | Node(_, value, _) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tree", RuntimeManaged: true }).ShouldBeFalse();
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
