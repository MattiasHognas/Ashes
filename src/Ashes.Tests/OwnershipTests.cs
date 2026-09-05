using System.Reflection;
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
    public void Let_alias_of_rc_loop_parameter_does_not_become_an_owner()
    {
        // `let r = acc` inside an RC-normalized TCO loop is a borrowed read of the parameter slot:
        // the back edge already releases the old parameter (an RcDrop with no owner slot), so an
        // owning scope-exit drop for `r` would release the same reference a second time. Only the
        // parameter releases may be runtime-managed; no let-owned (OwnerSlot-bearing) runtime drop
        // of a String may exist anywhere in the loop.
        IrProgram ir = LowerProgram(
            """
            let recursive walk n acc extra =
                match Ashes.Text.unconsText(extra) with
                    | None -> acc
                    | Some((h, t)) ->
                        let r = acc
                        in
                            if n == 0
                            then r
                            else walk(n - 1)(r + h)(t)

            Ashes.IO.print(walk(100)("")("abcdefghij"))
            """);

        IrFunction loop = ir.Functions.Single(function =>
            function.Instructions.Any(inst => inst is IrInst.TextUnconsText));
        loop.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true, OwnerSlot: -1 }).ShouldBeTrue(
            "the back edge must still release the old parameter");
        loop.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true, OwnerSlot: >= 0 }).ShouldBeFalse(
            "a let bound to a parameter read must not own a second release of the same reference");
    }

    [Test]
    public void Let_bound_call_result_in_tco_argument_constructor_is_retained()
    {
        // `label` is an owned runtime-managed let (an RC-normalized call result) stored into the
        // Jump constructor of the next iteration's accumulator. The let's own release still fires at
        // the back edge, so the constructor field must hold its own retained reference: exactly one
        // runtime-managed RcDup must feed the field store, or the next iteration reads freed memory.
        IrProgram ir = LowerProgram(
            """
            type Inst =
                | Jump(Str)
                | Other

            type Wrapped =
                | instruction: Inst
                | location: Maybe(Int)

            let pick n =
                if n == 0
                then "picked"
                else "other"

            let recursive loop n acc =
                if n == 0
                then acc
                else
                    let label = pick(n % 2)
                    in loop(n - 1)(Wrapped(instruction = Jump(label), location = None) :: acc)

            match loop(4)([]) with
                | [] -> Ashes.IO.print(0)
                | _ -> Ashes.IO.print(1)
            """);

        List<IrFunction> loops = ir.Functions
            .Where(function => function.Instructions.Any(inst =>
                inst is IrInst.RcDrop { TypeName: "String", OwnerSlot: >= 0, RuntimeManaged: true }))
            .ToList();
        loops.ShouldNotBeEmpty("the let-bound call result must be an owned runtime-managed binding");
        (bool storedRetained, bool storedRawLoad) = ClassifyOwnedBindingFieldStores(loops);
        storedRetained.ShouldBeTrue(
            "the let-bound call result stored into the tail-call argument's constructor must be retained");
        storedRawLoad.ShouldBeFalse(
            "a constructor field must never take the owned binding's own reference, which the back edge releases");
    }

    [Test]
    public void Tuple_result_of_a_tco_loop_retains_its_runtime_managed_parameters()
    {
        // Both accumulators are runtime-managed loop parameters that the loop's exit drops
        // unconditionally unless the parameter itself is the result. A tuple that stores them is a
        // different pointer, so each stored parameter needs its own retained reference or the
        // returned tuple holds freed lists.
        IrProgram ir = LowerProgram(
            """
            let recursive walk n xs ys =
                if n == 0
                then (xs, ys)
                else walk(n - 1)(n :: xs)(n :: ys)

            match walk(3)([])([]) with
                | (a, _) ->
                    match a with
                        | [] -> Ashes.IO.print(0)
                        | _ -> Ashes.IO.print(1)
            """);

        IrFunction loop = ir.Functions.Single(function =>
            string.Equals(function.Origin?.Source?.SourceName, "walk", StringComparison.Ordinal)
            && function.Instructions.Any(inst => inst is IrInst.Label { Name: var name }
                && name.EndsWith("_body", StringComparison.Ordinal)));
        HashSet<int> retained = loop.Instructions
            .OfType<IrInst.RcDup>()
            .Where(dup => dup.RuntimeManaged)
            .Select(dup => dup.Target)
            .ToHashSet();
        // The successor cons cells store the Int `n` in their first word, so the only 16-byte
        // aggregate whose two words are both retained references is the returned tuple.
        bool tupleStoresRetainedParameters = loop.Instructions
            .OfType<IrInst.Alloc>()
            .Where(alloc => alloc.SizeBytes == 16)
            .Any(alloc =>
            {
                List<IrInst.StoreMemOffset> stores = loop.Instructions
                    .OfType<IrInst.StoreMemOffset>()
                    .Where(store => store.BasePtr == alloc.Target)
                    .ToList();
                return stores.Count == 2 && stores.All(store => retained.Contains(store.Source));
            });
        tupleStoresRetainedParameters.ShouldBeTrue(
            "each loop parameter stored into the returned tuple must be a retained runtime-managed reference; loop:\n"
            + string.Join("\n", loop.Instructions.Select(inst => inst.ToString())));
    }

    /// <summary>
    /// For every function, tracks the temps naming an owned slot's value (loads and borrows of them)
    /// versus the retained duplicates made from those temps, and reports whether any constructor
    /// field store consumed a retained duplicate and whether any consumed the raw owned reference.
    /// </summary>
    private static (bool StoredRetained, bool StoredRawLoad) ClassifyOwnedBindingFieldStores(IEnumerable<IrFunction> functions)
    {
        bool storedRetained = false;
        bool storedRawLoad = false;
        foreach (IrFunction function in functions)
        {
            HashSet<int> ownerSlots = function.Instructions
                .OfType<IrInst.RcDrop>()
                .Where(drop => drop.RuntimeManaged && drop.OwnerSlot >= 0)
                .Select(drop => drop.OwnerSlot)
                .ToHashSet();
            var ownerLoads = new HashSet<int>();
            var retainedDups = new HashSet<int>();
            foreach (IrInst inst in function.Instructions)
            {
                switch (inst)
                {
                    case IrInst.LoadLocal load when ownerSlots.Contains(load.Slot):
                        ownerLoads.Add(load.Target);
                        break;
                    case IrInst.Borrow borrow when ownerLoads.Contains(borrow.SourceTemp):
                        ownerLoads.Add(borrow.Target);
                        break;
                    case IrInst.RcDup { RuntimeManaged: true } dup when ownerLoads.Contains(dup.SourceTemp):
                        retainedDups.Add(dup.Target);
                        break;
                    case IrInst.SetAdtField store when retainedDups.Contains(store.Source):
                        storedRetained = true;
                        break;
                    case IrInst.SetAdtField store when ownerLoads.Contains(store.Source):
                        storedRawLoad = true;
                        break;
                }
            }
        }

        return (storedRetained, storedRawLoad);
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
    public void Direct_known_function_results_transfer_runtime_aggregate_ownership()
    {
        IrProgram ir = LowerProgram("type Pair = | Pair(Int, Int)\ntype Point = | x: Int | y: Int\nlet makeList unit = (let values = [40, 2] in values)\nlet makePair unit = (let pair = Pair(40)(2) in pair)\nlet makePoint unit = (let point = Point(x = 40, y = 2) in point)\nlet values = makeList(0) in let pair = makePair(0) in let point = makePoint(0) in match values with | [] -> 0 | head :: _ -> match pair with | Pair(left, right) -> head + left + right + point.x + point.y");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Pair", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Point", RuntimeManaged: true }).ShouldBeTrue();
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
    public void Directly_escaping_adt_with_fresh_string_child_transfers_child_ownership()
    {
        IrProgram ir = LowerProgram("type TextBox = | TextBox(Str)\nlet escaped = (let box = TextBox(Ashes.Text.fromInt(40)) in box) in match escaped with | TextBox(text) -> Ashes.Text.byteLength(text)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "TextBox", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_adt_with_literal_string_child_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type TextBox = | TextBox(Str)\nlet escaped = (let box = TextBox(\"40\") in box) in match escaped with | TextBox(text) -> Ashes.Text.byteLength(text)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "TextBox", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_variant_with_fresh_string_child_transfers_ownership()
    {
        IrProgram ir = LowerProgram("type Choice = | Empty | Text(Str)\nlet escaped = (let choice = Text(Ashes.Text.fromInt(42)) in choice) in match escaped with | Empty -> 0 | Text(value) -> Ashes.Text.byteLength(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.Functions.SelectMany(function => function.Instructions)
            .Any(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.Functions.SelectMany(function => function.Instructions)
            .Any(inst => inst is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_variant_with_literal_string_child_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Choice = | Empty | Text(Str)\nlet escaped = (let choice = Text(\"42\") in choice) in match escaped with | Empty -> 0 | Text(value) -> Ashes.Text.byteLength(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.Functions.SelectMany(function => function.Instructions)
            .Any(inst => inst is IrInst.RcDrop { TypeName: "Choice", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_adt_with_fresh_bytes_bigint_and_list_children_transfers_ownership()
    {
        IrProgram ir = LowerProgram("type Payload = | Payload(Bytes, BigInt, List(Int))\nlet escaped = (let value = Payload(Ashes.Byte.u16Le(258u16))(Ashes.Number.BigInt.fromInt(42))([40, 2]) in value) in match escaped with | Payload(bytes, big, values) -> match values with | [] -> 0 | head :: _ -> Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + head");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Payload", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_adt_with_borrowed_list_child_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Payload = | Payload(List(Int))\nlet values = [40, 2] in let escaped = (let value = Payload(values) in value) in match escaped with | Payload(items) -> match items with | [] -> 0 | head :: _ -> head");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Payload", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_adt_with_fresh_tuple_child_transfers_ownership()
    {
        IrProgram ir = LowerProgram("type Wrapped = | Wrapped((Int, Int))\nlet escaped = (let value = Wrapped((40, 2)) in value) in match escaped with | Wrapped((left, right)) -> left + right");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Wrapped", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    // A tuple with a list-of-records element falls back to an arena shell. A string bound out of the
    // borrowed parameter has no release of its own, so the rebuilt tuple carries it as is; cloning it
    // per call would copy a state string threaded through the tuple on every step.
    [Test]
    public void Escaping_arena_tuple_carries_a_string_bound_from_a_borrowed_parameter_without_a_clone()
    {
        IrProgram ir = LowerProgram(
            """
            type Entry =
                | key: Str
                | count: Int

            let step (n: Int) (state: (Str, List(Entry))) =
                match state with
                    | (text, entries) -> (text, Entry(key = Ashes.Text.fromInt(n), count = n) :: entries)

            Ashes.IO.print(1)
            """);

        IrFunction step = ir.Functions.Single(function =>
            function.Instructions.Any(inst => inst is IrInst.AllocAdt));
        step.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse(
            "the tuple stays an arena shell around the list of records");
        step.Instructions.Any(inst =>
            inst is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.IndependentClone }).ShouldBeFalse(
            "the borrowed parameter's string is carried, not cloned");
        step.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeFalse(
            "a borrowed parameter part has no release of its own");
    }

    // The same tuple shape around a let-owned fresh string still copies it: the let's scope-exit
    // release would otherwise free the string the escaping tuple holds.
    [Test]
    public void Escaping_arena_tuple_still_clones_a_string_owned_by_a_released_let()
    {
        IrProgram ir = LowerProgram(
            """
            type Entry =
                | key: Str
                | count: Int

            let mk (n: Int) = Ashes.Text.fromInt(n) + "!"

            let fresh (n: Int) (state: (Str, List(Entry))) =
                match state with
                    | (_text, entries) ->
                        let label = mk(n)
                        in (label, Entry(key = label, count = n) :: entries)

            match fresh(1)(("a", [])) with
                | (text, _) -> Ashes.IO.print(text)
            """);

        IrFunction fresh = ir.Functions.Single(function =>
            function.Instructions.Any(inst => inst is IrInst.AllocAdt));
        fresh.Instructions.Any(inst =>
            inst is IrInst.CopyOutArena { Purpose: IrInst.CopyOutPurpose.IndependentClone }).ShouldBeTrue(
            "the let-owned string is cloned before its scope releases it");
        fresh.Instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true, OwnerSlot: >= 0 }).ShouldBeTrue(
            "the let still releases its own reference");
    }

    // A runtime-managed string loop parameter consed into a sibling accumulator at the tail
    // self-call is a second reference the cell keeps: the back edge releases the parameter's own
    // once its argument rebuilds the string, so the cell's copy is retained. Only the parameter's
    // read inside its own successor is the reference the back edge moves and stays unretained.
    [Test]
    public void Tco_loop_parameter_consed_into_a_sibling_accumulator_is_retained_for_the_cell()
    {
        IrProgram ir = LowerProgram(
            """
            let recursive collect (n: Int) (text: Str) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(text + Ashes.Text.fromInt(n))(text :: acc)

            Ashes.IO.print(1)
            """);

        IrFunction loop = ir.Functions.Single(function =>
            function.Instructions.Any(inst => inst is IrInst.ConcatStr));
        List<IrInst> instructions = loop.Instructions;
        int cellIndex = instructions.FindIndex(inst => inst is IrInst.Alloc { RuntimeManaged: true });
        cellIndex.ShouldBeGreaterThan(0, "the accumulator's cons cell lives on the reference-counted heap");
        instructions.Take(cellIndex).Any(inst =>
            inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBeTrue(
            "the string parameter is retained before the cell stores it");
        instructions.Any(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue(
            "the back edge still releases the parameter's own reference");
    }

    [Test]
    public void Directly_escaping_generic_adt_with_copy_payload_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("type Box(a) = | Box(a)\nlet escaped = (let box = Box(42) in box) in match escaped with | Box(value) -> value");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_generic_adt_with_pointer_payload_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Box(a) = | Box(a)\nlet escaped = (let box = Box(\"hello\") in box) in match escaped with | Box(value) -> Ashes.IO.print(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_generic_adt_with_fresh_string_payload_transfers_ownership()
    {
        IrProgram ir = LowerProgram("type Box(a) = | Box(a)\nlet escaped = (let box = Box(Ashes.Text.fromInt(42)) in box) in match escaped with | Box(value) -> Ashes.Text.byteLength(value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_generic_adts_with_fresh_aggregate_payloads_transfer_ownership()
    {
        IrProgram list = LowerProgram("type Box(a) = | Box(a)\nlet escaped = (let box = Box([40, 2]) in box) in match escaped with | Box(values) -> match values with | [] -> 0 | head :: _ -> head");
        IrProgram tuple = LowerProgram("type Box(a) = | Box(a)\nlet escaped = (let box = Box((40, 2)) in box) in match escaped with | Box((left, right)) -> left + right");

        list.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
        list.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        list.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
        tuple.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        tuple.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        tuple.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
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
    public void Higher_order_function_normalizes_shallow_heap_results_to_runtime_ownership()
    {
        IrProgram ir = LowerProgram(
            "let apply : (Int -> Str) -> Str = given f -> f(0)\nlet make unit = let text = \"ab\" + \"cd\" in text\nlet literal unit = \"wxyz\"\nlet first = apply(make) in let second = apply(literal) in Ashes.Text.byteLength(first) + Ashes.Text.byteLength(second)");

        ir.Functions.SelectMany(function => function.Instructions).Any(inst =>
            inst is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        ir.Functions.Any(function =>
            function.Instructions.Any(inst => inst is IrInst.CallClosure)
            && function.Instructions.Any(inst => inst is IrInst.CopyOutArena { RuntimeManaged: true })
            && function.Instructions.All(inst => inst is not IrInst.CopyOutArena { RuntimeManaged: false })).ShouldBeTrue();
        ir.EntryFunction.Instructions.Count(inst =>
            inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBe(2);
    }

    [Test]
    public void String_parameter_returned_on_every_branch_is_normalized_at_entry()
    {
        IrProgram ir = LowerProgram(
            "type Box = | value: Str\nlet carry path = if Ashes.Text.byteLength(path) > 0 then Ok(Box(value = path)) else Error(path)\nmatch carry(\"value\") with | Ok(Box { value = value }) -> Ashes.Text.byteLength(value) | Error(error) -> Ashes.Text.byteLength(error)");

        IrFunction carry = ir.Functions.Single(function =>
            string.Equals(
                function.Origin?.Source?.SourceName,
                "carry",
                StringComparison.Ordinal));
        carry.Instructions.Any(instruction => instruction is IrInst.LoadArgumentOwnership).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(instruction => instruction is
            IrInst.MakeClosure { AcceptsRuntimeManagedArgument: true }
            or IrInst.MakeClosureStack { AcceptsRuntimeManagedArgument: true }).ShouldBeTrue();
    }

    [Test]
    public void Higher_order_function_normalizes_copy_list_results_to_runtime_ownership()
    {
        IrProgram ir = LowerProgram(
            "let apply : (Int -> List(Int)) -> List(Int) = given f -> f(0)\nlet source = [40, 2] in let borrow = given unit -> source in let result = apply(borrow) in match result with | [] -> 0 | head :: _ -> head");

        ir.Functions.Any(function =>
            function.Instructions.Any(instruction => instruction is IrInst.CallClosure)
            && function.Instructions.Any(instruction =>
                instruction is IrInst.CopyOutList { RuntimeManaged: true })).ShouldBeTrue(
            "The concrete higher-order List(Int) result should normalize an arena result into RC cells.");
        ir.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Higher_order_function_normalizes_owned_pointer_list_results()
    {
        IrProgram strings = LowerProgram(
            "let apply : (Int -> List(Str)) -> List(Str) = given f -> f(0)\nlet source = [\"forty\", \"two\"] in let borrow = given unit -> source in let result = apply(borrow) in match result with | [] -> 0 | head :: _ -> Ashes.Text.byteLength(head)");
        IrProgram nested = LowerProgram(
            "let apply : (Int -> List(List(Int))) -> List(List(Int)) = given f -> f(0)\nlet source = [[40, 2]] in let borrow = given unit -> source in let result = apply(borrow) in match result with | [] -> 0 | head :: _ -> match head with | [] -> 0 | value :: _ -> value");

        strings.Functions.SelectMany(function => function.Instructions).Any(instruction =>
            instruction is IrInst.CopyOutList
            {
                HeadCopy: IrInst.ListHeadCopyKind.String,
                RuntimeManaged: true,
            }).ShouldBeTrue();
        strings.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        nested.Functions.SelectMany(function => function.Instructions).Any(instruction =>
            instruction is IrInst.CopyOutList
            {
                HeadCopy: IrInst.ListHeadCopyKind.InnerList,
                RuntimeManaged: true,
            }).ShouldBeTrue();
        nested.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeGreaterThan(1);
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
    public void Escaping_bytes_append_with_allocating_operand_uses_owned_result_provenance()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.append(Ashes.Byte.fromList([1u8, 2u8]))(Ashes.Byte.fromText(\"cd\")) in bytes");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
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
    public void Escaping_append_byte_with_allocating_operand_uses_owned_result_provenance()
    {
        IrProgram ir = LowerProgram("let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromList([1u8, 2u8]))(33u8) in bytes");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
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
    public void Escaping_bytes_from_borrowed_list_uses_owned_result_provenance()
    {
        IrProgram ir = LowerProgram("let values = [7u8, 8u8, 9u8] in let bytes = Ashes.Byte.fromList(values) in bytes");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
    }

    [Test]
    public void Tco_list_of_borrowed_bytes_materializes_owned_elements()
    {
        IrProgram ir = LowerProgram(
            """
            let recursive build n acc =
                if n <= 0
                then acc
                else build(n - 1)(Ashes.Byte.fromText("xy") :: acc)
            in build(3)([])
            """);
        IrInst[] instructions = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .ToArray();

        instructions.Any(instruction => instruction is IrInst.CopyOutArena
        {
            RuntimeManaged: true,
            Purpose: IrInst.CopyOutPurpose.RcNormalization,
        }).ShouldBeTrue();
        instructions.Any(instruction => instruction is IrInst.Alloc
        {
            RuntimeManaged: true,
        }).ShouldBeTrue();
        instructions.Any(instruction => instruction is IrInst.RcDrop
        {
            TypeName: "Bytes",
            RuntimeManaged: true,
        }).ShouldBeTrue();
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
    public void Escaping_byte_subtext_with_allocating_source_uses_owned_result_provenance()
    {
        IrProgram ir = LowerProgram("let text = Ashes.Byte.subText(Ashes.Byte.fromText(Ashes.Text.fromInt(42)))(0)(1) in text");

        ir.EntryFunction.Instructions.Any(inst =>
            inst is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
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
    public void Directly_escaping_copy_tuple_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let pair = (40, 2) in pair) in match escaped with | (left, right) -> left + right");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_pointer_tuple_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let escaped = (let pair = (\"hello\", 2) in pair) in match escaped with | (text, _) -> Ashes.IO.print(text)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_fresh_nested_tuple_transfers_child_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let pair = ((40, 2), 0) in pair) in match escaped with | ((left, right), bonus) -> left + right + bonus");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeTrue();
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_borrowed_tuple_child_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let child = (40, 2) in let escaped = (let pair = (child, 0) in pair) in match escaped with | ((left, right), bonus) -> left + right + bonus");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_fresh_string_child_transfers_child_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let pair = (Ashes.Text.fromInt(40), 2) in pair) in match escaped with | (text, bonus) -> Ashes.Text.byteLength(text) + bonus");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_literal_string_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let escaped = (let pair = (\"40\", 2) in pair) in match escaped with | (text, bonus) -> Ashes.Text.byteLength(text) + bonus");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_fresh_bytes_and_bigint_children_transfers_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let values = (Ashes.Byte.u16Le(258u16), Ashes.Number.BigInt.fromInt(42)) in values) in match escaped with | (bytes, big) -> Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_fresh_copy_list_child_transfers_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let values = ([40, 2], 0) in values) in match escaped with | (items, bonus) -> match items with | [] -> bonus | head :: _ -> head + bonus");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(3);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_borrowed_list_child_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let items = [40, 2] in let escaped = (let values = (items, 0) in values) in match escaped with | (values, _) -> match values with | [] -> 0 | head :: _ -> head");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_fresh_adt_and_record_children_transfers_ownership()
    {
        IrProgram ir = LowerProgram("type Pair = | Pair(Int, Int)\ntype Point = | x: Int | y: Int\nlet escaped = (let values = (Pair(40)(2), Point(x = 40, y = 2)) in values) in match escaped with | (Pair(left, right), point) -> left + right + point.x + point.y");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Pair", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Point", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_tuple_with_borrowed_adt_child_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("type Pair = | Pair(Int, Int)\nlet child = Pair(40)(2) in let escaped = (let values = (child, 0) in values) in match escaped with | (Pair(left, right), bonus) -> left + right + bonus");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeFalse();
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
    public void Directly_escaping_record_with_fresh_string_field_transfers_ownership()
    {
        IrProgram ir = LowerProgram("type Box = | value: Str\nlet escaped = (let box = Box(value = Ashes.Text.fromInt(42)) in box) in Ashes.Text.byteLength(escaped.value)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
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
    public void Late_typed_tco_string_is_retained_when_wrapped_in_result()
    {
        IrProgram ir = LowerProgram(
            """
            let recursive wrap value count =
                if count <= 0
                then Ok(value)
                else wrap(value + "")(count - 1)
            wrap("value")(0)
            """);

        IrFunction wrapper = ir.Functions.Single(function =>
            function.Instructions.Any(instruction => instruction is IrInst.AllocAdt { FieldCount: 1 })
            && function.Instructions.Any(instruction => instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }));

        wrapper.Instructions.Any(instruction => instruction is IrInst.RcDup { RuntimeManaged: true })
            .ShouldBeTrue("The returned Result field needs its own reference before the TCO parameter owner is dropped.");
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
        IrFunctionOrigin dropperOrigin = dropper.Origin
            ?? throw new InvalidOperationException("Missing structural dropper origin.");
        dropperOrigin.Kind.ShouldBe(IrFunctionOriginKind.RuntimeManagedAdtDropper);
        dropperOrigin.Source.ShouldBeNull();
        CompilerFunctionOwner compilerOwner = dropperOrigin.CompilerOwner
            ?? throw new InvalidOperationException("Missing structural dropper owner.");
        compilerOwner.Kind.ShouldBe(CompilerFunctionOwnerKind.Type);
        compilerOwner.Name.ShouldContain("Tree");
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
    public void Match_result_retains_shared_list_from_closure_and_direct_owner_arms()
    {
        IrProgram ir = LowerProgram(
            """
            let selected : List(Int) =
                let captured = let element = -10 in [element] in
                match false with
                    | true -> ((given (_: Unit) -> captured))(Unit)
                    | false -> captured
            in selected
            """);

        ir.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.RcDup
            {
                RuntimeManaged: true,
                MayBeEmpty: true,
            }).ShouldBe(1);
        IrFunction closure = ir.Functions.Single(function =>
            function.Instructions.Any(instruction => instruction is IrInst.LoadEnv));
        closure.Instructions.Count(instruction =>
            instruction is IrInst.RcDup
            {
                RuntimeManaged: true,
                MayBeEmpty: true,
            }).ShouldBe(1);
    }

    [Test]
    public void Directly_escaping_pointer_element_list_remains_arena_managed()
    {
        IrProgram ir = LowerProgram("let escaped = (let values = [\"one\", \"two\"] in values) in match escaped with | [] -> Ashes.IO.print(\"empty\") | head :: _ -> Ashes.IO.print(head)");

        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeFalse();
    }

    [Test]
    public void Directly_escaping_list_with_fresh_string_elements_transfers_ownership()
    {
        IrProgram ir = LowerProgram("let escaped = (let values = [Ashes.Text.fromInt(40), Ashes.Text.fromInt(2)] in values) in match escaped with | [] -> 0 | head :: _ -> Ashes.Text.byteLength(head)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.CopyOutArena).ShouldBeFalse();
    }

    [Test]
    public void Fresh_owned_list_head_shares_existing_runtime_tail_once()
    {
        IrProgram ir = LowerProgram("let tail = [Ashes.Text.fromInt(2)] in let values = Ashes.Text.fromInt(40) :: tail in match values with | [] -> 0 | head :: _ -> match tail with | [] -> 0 | tailHead :: _ -> Ashes.Text.byteLength(head) + Ashes.Text.byteLength(tailHead)");

        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();
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

    // ProducesFreshRuntimeManageableList gives IsFreshListConstructionExpression the same control-flow
    // transparency the ADT/Tuple escape-boundary classifiers have (CollectFreshEscapeTerminals), so a
    // fresh list literal returned from an if/match arm is recognized as an escaping runtime-managed
    // result, not just when the whole let/lambda body IS the list construction directly. Calling
    // IsFreshListConstructionExpression directly on the escaping body with no arm-walking would leave
    // this exact shape arena-managed.

    [Test]
    public void Fresh_list_returned_from_if_arm_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram(
            """
            let build flag =
                let discard = 0 in
                if flag
                then [Ashes.Text.fromInt(40)]
                else [Ashes.Text.fromInt(2)]

            let escaped = build(true) in
            match escaped with
                | [] -> 0
                | head :: _ -> Ashes.Text.byteLength(head)
            """);

        ir.Functions.Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .Count(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBe(2);
        ir.Functions.Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .Count(inst => inst is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBe(2);
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true })
            .ShouldBeTrue();
    }

    // Adversarial guard found while re-verifying this phase (not merely proposed -- confirmed live by
    // direct IR inspection before the fix landed): one arm a bare-Var passthrough of an EXISTING,
    // independently-owned (here: arena) list binding, the other arm a genuinely fresh list literal.
    // An existence-check (OR) classifier sets the ambient RC-eligibility flag for the whole escaping
    // if, so the fresh arm's cons cell is allocated on the RC heap (Alloc RuntimeManaged: true) -- but
    // the join-level MarkUniformRuntimeManagedResult, which decides whether the JOINED value is
    // actually treated as owned, requires BOTH arms independently verified runtime-managed, which the
    // passthrough arm never is. The two mechanisms disagree: pre-fix, exactly one orphaned
    // Alloc{RuntimeManaged: true} exists with no corresponding RcDrop{TypeName: "List", RuntimeManaged:
    // true} anywhere in the program -- confirmed via manual revert that this test fails without the
    // fix. NOTE: compiled-binary testing at real scale (see tests/perceus_list_mixed_passthrough_sibling_no_leak.ash)
    // did NOT show a measurable RSS difference between the pre-fix and post-fix binaries -- the
    // orphaned cell appears to be reclaimed by the same scope-based bulk arena reset that reclaims
    // ordinary arena garbage, since it never escapes its own allocating scope. This is fixed anyway
    // because the bookkeeping disagreement is real and provable and costs nothing to remove, not
    // because a live leak/corruption was confirmed at the process level -- see the fuller reasoning on
    // ProducesFreshRuntimeManageableList in Lowering.TopCellFreshness.cs.
    [Test]
    public void Fresh_list_arm_with_existing_passthrough_sibling_stays_uniformly_arena_managed()
    {
        IrProgram ir = LowerProgram(
            """
            let existingList =
                let p = "p" in
                let q = "q" in
                [p, q]

            let build flag =
                let discard = 0 in
                if flag
                then existingList
                else [Ashes.Text.fromInt(1)]

            let escaped = build(false) in
            match escaped with
                | [] -> 0
                | head :: _ -> Ashes.Text.byteLength(head)
            """);

        // The list literal in `build`'s OWN body (lambda_0, not the caller's normalization machinery)
        // must never be RC-allocated: that would be the orphaned cell (Alloc{RuntimeManaged: true}
        // with no reachable owner) confirmed live before the fix. This deliberately checks only
        // `build`'s own function, not the whole program: a dynamic closure call's caller-side boundary
        // (CopyOutList) unconditionally normalizes ANY list a closure returns into a fresh,
        // properly-tracked RC copy regardless of this classifier -- that pre-existing, unrelated
        // mechanism is what the `RcDrop{TypeName: "List", RuntimeManaged: true}` pair downstream
        // belongs to, and asserting it away would conflate a real fix with a false expectation.
        IrFunction build = ir.Functions.Single(function => string.Equals(function.Label, "lambda_0", StringComparison.Ordinal));
        build.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse(
            "neither arm may be RC-allocated when a sibling arm is a passthrough of an existing, not " +
            "provably fresh binding -- an existence check would RC-allocate the fresh arm's cons cell " +
            "while the join declines to own it, permanently leaking it whenever that arm executes.");
    }

    [Test]
    public void Fresh_list_returned_from_match_arms_transfers_runtime_ownership()
    {
        IrProgram ir = LowerProgram(
            """
            let build n =
                let discard = 0 in
                match n with
                    | 0 -> []
                    | 1 -> [Ashes.Text.fromInt(1)]
                    | _ -> [Ashes.Text.fromInt(2), Ashes.Text.fromInt(3)]

            let escaped = build(2) in
            match escaped with
                | [] -> 0
                | head :: _ -> Ashes.Text.byteLength(head)
            """);

        ir.Functions.Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue(
                "a fresh list literal returned from a non-empty match arm must be recognized as an " +
                "escaping runtime-managed result, the list analog of ADT/Tuple arm walking.");
        ir.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true })
            .ShouldBeTrue();
    }

    // Tail-sharing adversarial guards: these are controls, not regression targets for THIS fix -- they
    // must stay conservative (arena) both
    // before and after ProducesFreshRuntimeManageableList exists, proving the control-flow-transparency
    // extension never widened IsFreshListConstructionExpression's terminal set. A cons cell built onto an
    // EXISTING list (a bare Var tail, or a recursive call's result as the tail) shares structure with that
    // existing list; RC-promoting it would make a fresh-looking RC cell point at a tail that may not be an
    // RC cell at all (an arena address, or a differently-owned RC graph) -- a UAF/corruption risk distinct
    // from anything ADTs or Tuples have, since only Lists have a tail that can independently alias.

    [Test]
    public void List_rebuilt_by_consing_onto_an_existing_tail_var_stays_arena_managed()
    {
        // `h :: t` in the cons arm has a bare Var tail (not ListLit/another Cons-to-ListLit), so it must
        // never classify as top-cell fresh, even though it sits behind an if/match arm the new
        // control-flow-transparent walk now sees through.
        IrProgram ir = LowerProgram(
            """
            let passThroughHead xs =
                let discard = 0 in
                match xs with
                    | [] -> []
                    | h :: t -> h :: t

            let escaped = passThroughHead([Ashes.Text.fromInt(1), Ashes.Text.fromInt(2)]) in
            match escaped with
                | [] -> 0
                | head :: _ -> Ashes.Text.byteLength(head)
            """);

        IrFunction passThroughHead = ir.Functions.Single(function =>
            function.Instructions.Any(inst => inst is IrInst.StoreMemOffset));
        passThroughHead.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse(
            "a cons cell rebuilt onto an existing (potentially non-RC) tail var must stay arena-managed " +
            "regardless of which arm it sits behind -- promoting it to RC would give a fresh-looking RC " +
            "cell a tail pointer that is not necessarily an RC cell at all.");
    }

    [Test]
    public void List_rebuilt_by_consing_onto_a_recursive_call_result_stays_arena_managed()
    {
        // `h :: rebuild(t)` has a Call as its tail -- self-contained per the arena-side
        // IsArenaSelfContainedListRebuildExpr
        // (a callee's list result is copied out of its own arena scope, so it is self-contained and safe
        // to whole-clone at a TCO back-edge), but deliberately NOT fresh per this RC-promotion engine's
        // narrower IsFreshListConstructionExpression, which only ever accepts ListLit or a Cons chain
        // bottoming out in one. The two predicates answer different questions (cost-safe-to-clone vs.
        // safe-to-RC-promote) and must not be unified into accepting the same terminal set.
        IrProgram ir = LowerProgram(
            """
            let recursive rebuild xs =
                match xs with
                    | [] -> []
                    | h :: t -> h :: rebuild(t)

            let escaped = rebuild([Ashes.Text.fromInt(1), Ashes.Text.fromInt(2)]) in
            match escaped with
                | [] -> 0
                | head :: _ -> Ashes.Text.byteLength(head)
            """);

        IrFunction rebuild = ir.Functions.Single(function => function.Instructions.Any(inst => inst is IrInst.StoreMemOffset));
        rebuild.Instructions.Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse(
            "a cons cell whose tail is a recursive call result must stay arena-managed under this RC " +
            "engine even though the arena/TCO side's own IsArenaSelfContainedListRebuildExpr treats a call result as " +
            "safe to whole-clone -- the two questions (RC-promotion-safe vs. clone-cost-safe) are not " +
            "the same question and must not share a terminal set.");
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

    // --- Self-recursive ADT: uniform arena-vs-RC representation across sibling constructors ---

    // Regression for the binary-trees RC Perceus migration leak: a self-recursive ADT's
    // base-case arm (a bare nullary constructor like `Leaf`) must not be promoted to an RC cell while
    // a sibling arm (`Node`, built from recursive calls rather than nested constructor literals) stays
    // arena-managed. A mixed representation lets an arena-managed parent's no-op drop skip over its
    // RC-managed children forever. ProducesFreshRuntimeManageableAdt must require every sibling arm
    // that also constructs the same type to be independently fresh before treating any one arm as
    // escaping-fresh.
    [Test]
    public void Self_recursive_adt_nullary_and_recursive_arms_share_runtime_managed_flag()
    {
        var ir = LowerProgram(
            """
            type Tree =
                | Leaf
                | Node(Tree, Tree)

            let recursive make depth =
                if depth == 0
                then Leaf
                else Node(make(depth - 1))(make(depth - 1))

            make(3)
            """);

        List<bool> runtimeManagedFlags = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.AllocAdt>()
            .Select(alloc => alloc.RuntimeManaged)
            .Distinct()
            .ToList();

        runtimeManagedFlags.Count.ShouldBe(1,
            "Leaf (nullary) and Node (recursive) must share one representation -- a mix leaks the " +
            "RC-managed constructor's cells forever once the arena-managed sibling's no-op drop " +
            "discards the parent without ever walking into it.");
    }

    [Test]
    public void Self_recursive_adt_sibling_arm_with_fresh_recursive_children_still_escapes()
    {
        // A recursive arm built from LITERAL nested constructor applications (not function calls) is
        // genuinely a fresh constructor tree, so both arms may still be promoted together -- this must
        // remain true after the consistency fix (it is not a regression target, just a control).
        var ir = LowerProgram(
            """
            type Tree =
                | Leaf
                | Node(Tree, Tree)

            let build depth =
                if depth == 0
                then Leaf
                else Node(Leaf)(Leaf)

            build(1)
            """);

        List<bool> runtimeManagedFlags = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.AllocAdt>()
            .Select(alloc => alloc.RuntimeManaged)
            .Distinct()
            .ToList();

        runtimeManagedFlags.Count.ShouldBe(1);
    }

    // Sibling if/match arms constructing the same ADT must reconcile freshness by AGREEMENT, never by
    // OR-ing across arms (a trivially-fresh arm dragging a non-fresh sibling along mixes arena and RC
    // representations of the same type, and an arena cell's no-op drop never walks into RC children,
    // leaking them). This reconciliation is implemented by the shared AnyArmConsistentlyFresh engine
    // (Lowering.TopCellFreshness.cs) instead of a bespoke ProducesFreshRuntimeManageableAdt loop. This
    // pins the same invariant against a THREE-constructor sum type (the original bug and its fix were
    // only ever exercised with two constructors) to confirm the generalized reconciliation groups
    // arbitrarily many sibling arms by parent type name, not just a binary if/else pair.
    [Test]
    public void Self_recursive_adt_three_constructor_arms_share_runtime_managed_flag()
    {
        var ir = LowerProgram(
            """
            type Tree =
                | Leaf
                | Single(Tree)
                | Node(Tree, Tree)

            let recursive make depth =
                match depth with
                    | 0 -> Leaf
                    | 1 -> Single(make(depth - 1))
                    | _ -> Node(make(depth - 1))(make(depth - 1))

            make(3)
            """);

        List<bool> runtimeManagedFlags = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.AllocAdt>()
            .Select(alloc => alloc.RuntimeManaged)
            .Distinct()
            .ToList();

        runtimeManagedFlags.Count.ShouldBe(1,
            "Leaf/Single/Node must share one representation across all three sibling arms -- the " +
            "reconciliation must generalize past the original bug's binary if/else shape.");
    }

    // Adversarial false-positive guard (mirrors Phase 3's "rewrapping a match-extracted field" test):
    // `Node(l)(Leaf)` LOOKS top-cell-fresh syntactically (its root is a direct constructor
    // application), but `l` is a match-extracted field of an incoming (not provably fresh) tree, not a
    // literal constructor application -- the outer Node cell must not be treated as part of a fresh,
    // independently-RC-manageable recursive constructor tree on the strength of syntactic shape alone.
    // IsFreshConstructorTree correctly rejects it (the recursion into `l` fails, since a bare Var that
    // is not itself a constructor application is never top-cell-fresh), so this must resolve
    // conservative (uniform, non-mixed) exactly like the cases above -- confirming the new
    // engine cannot be tricked into a false positive by a syntactically-constructor-shaped rewrap of an
    // aliased, non-fresh child.
    [Test]
    public void Self_recursive_adt_rewrapped_match_extracted_child_stays_conservative()
    {
        var ir = LowerProgram(
            """
            type Tree =
                | Leaf
                | Node(Tree, Tree)

            let rebuildLeftOnly tree =
                match tree with
                    | Leaf -> Leaf
                    | Node(l, r) -> Node(l)(Leaf)

            rebuildLeftOnly(Node(Leaf)(Leaf))
            """);

        List<bool> runtimeManagedFlags = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.AllocAdt>()
            .Select(alloc => alloc.RuntimeManaged)
            .Distinct()
            .ToList();

        runtimeManagedFlags.Count.ShouldBe(1,
            "a rewrap of a match-extracted (aliased, not provably fresh) child must not be treated as " +
            "an independently fresh constructor tree just because its own root syntactically looks " +
            "like a direct constructor application.");
    }

    // Regression: AnyArmConsistentlyFresh's null group key for a non-constructing terminal used to be
    // silently EXCLUDED from the conflict check instead of treated as a real, conflicting key. A bare-Var
    // passthrough arm (`existing`, aliasing a pre-existing, not-provably-fresh Box) sibling to a fresh
    // `Full(flag)` construction let the fresh arm's verdict win unopposed, so `Full(flag)` got
    // `AllocAdt{RuntimeManaged: true}` with no reachable RcDrop anywhere in the lowered program -- a
    // leak, confirmed via a compiled-binary repro (linear RSS growth, ~5x/10x iteration count ->
    // ~5x/10x leaked bytes; flat for an all-fresh or all-passthrough control of the same shape). Fixed
    // by requiring a non-constructing terminal to conflict unless it is a genuine self-recursive tail
    // funnel (which this one, mkBox, is not -- it isn't even recursive).
    [Test]
    public void Adt_mixed_fresh_and_passthrough_sibling_arm_stays_conservative()
    {
        var ir = LowerProgram(
            """
            type Box =
                | Empty
                | Full(Int)

            let mkBox flag existing =
                if flag == 0
                then existing
                else Full(flag)

            mkBox(1)(Full(0))
            """);

        List<bool> runtimeManagedFlags = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.AllocAdt>()
            .Select(alloc => alloc.RuntimeManaged)
            .Distinct()
            .ToList();

        runtimeManagedFlags.Count.ShouldBe(1,
            "a bare-Var passthrough arm (`existing`) sibling to a fresh `Full(flag)` construction must " +
            "not let the fresh arm's verdict win unopposed -- every AllocAdt in the program must share " +
            "one representation, matching the established uniform-representation invariant.");
    }

    // Tuple sibling of the above: ProducesFreshTuple had the identical null-group-key gap (an existence
    // check, not even attempting reconciliation). `if flag == 0 then existing else (flag, flag)` sets
    // the ambient runtime-managed-tuple flag for the whole position on the strength of the fresh arm
    // alone, promoting the fresh tuple to the RC heap while the passthrough arm is never independently
    // verified owned -- the identical leak shape, confirmed via a compiled-binary repro.
    [Test]
    public void Tuple_mixed_fresh_and_passthrough_sibling_arm_stays_conservative()
    {
        var ir = LowerProgram(
            """
            let mkPair flag existing =
                if flag == 0
                then existing
                else (flag, flag)

            mkPair(1)((0, 0))
            """);

        List<bool> runtimeManagedFlags = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.Alloc>()
            .Select(alloc => alloc.RuntimeManaged)
            .Distinct()
            .ToList();

        runtimeManagedFlags.Count.ShouldBe(1,
            "a bare-Var passthrough arm (`existing`) sibling to a fresh tuple construction must not let " +
            "the fresh arm's verdict win unopposed -- every tuple Alloc in the program must share one " +
            "representation.");
    }

    // Control for the two tests above: a self-recursive tail-call sibling (not a bare-Var passthrough)
    // must remain exempt from the conflict -- the exact shape every hand-written parser's
    // `parseStrBody`-style loop depends on (see Tco_loop_string_accumulator_returned_in_tuple_survives_
    // next_call / ..._adt_wrapped_tuple_survives_next_call in EndToEndNativeBackendTests, which pin the
    // real Result-typed version of this same pattern). Pins that the fix (requiring a non-constructing
    // terminal to conflict) did not regress to treating funnel siblings as conflicting too, which would
    // wrongly force every such loop back to arena-managed.
    [Test]
    public void Adt_self_recursive_tail_funnel_sibling_still_escapes()
    {
        var ir = LowerProgram(
            """
            type Status =
                | Pending
                | Done(Int)

            let recursive poll n =
                if n == 0
                then Done(n)
                else poll(n - 1)

            poll(5)
            """);

        List<bool> runtimeManagedFlags = ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.AllocAdt>()
            .Select(alloc => alloc.RuntimeManaged)
            .Distinct()
            .ToList();

        runtimeManagedFlags.ShouldContain(true,
            "the self-recursive tail-call sibling (`poll(n - 1)`) must stay exempt from the conflict " +
            "check so `Done(n)`'s constructor application is still recognized as an escaping " +
            "runtime-managed ADT -- forcing it to conflict would regress every tail-recursive loop " +
            "shaped like this back to arena-managed.");
    }

    [Test]
    public void Mutually_recursive_fresh_adt_result_uses_proven_runtime_ownership_at_entry_call()
    {
        IrProgram ir = LowerProgram(
            """
            type Box =
                | Empty
                | Full(Int)

            let recursive first n =
                if n <= 0
                then Full(1)
                else second(n - 1)
            and second n =
                if n <= 0
                then Full(2)
                else first(n - 1)

            let result = first(2)
            in match result with
                | Empty -> 0
                | Full(value) -> value
            """);

        ir.Functions.SelectMany(function => function.Instructions).Any(instruction =>
            instruction is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue(
            "the fresh base arms should allocate their Box results with runtime ownership.");
        ir.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue(
            "the entry call should receive and release a proven runtime-managed Box result.");
        ir.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.CopyOutArena).ShouldBeFalse(
            "the SCC provenance proof should avoid the conditional arena-copy normalization path.");
    }

    // --- Fresh-RC-producer whitelist: BuiltinRegistry-driven lookup replaces AST pattern matching ---

    // The exact call-site shapes the pre-refactor `IsRuntimeRcStringProducer` / `IsRuntimeRcBytesProducer`
    // / `IsRuntimeRcBigIntProducer` recognized via hardcoded qualified-name string comparisons, now
    // declared as `BuiltinRegistry.BuiltinModuleMember.ProducesFreshRcResult` metadata instead. This is
    // the regression guard for the refactor: every one of these must still be recognized by the
    // BuiltinRegistry-driven predicates, and nothing outside this set (see
    // `NonWhitelistedFreshRcProducerControls` below) must newly be recognized.
    private static readonly (string Predicate, string Module, string Member, int Arity)[] FreshRcProducerWhitelist =
    [
        // String producers -- Ashes.Text.* value-rendering intrinsics, plus Ashes.Byte.subText (a
        // decoding call that always copies into a fresh Str).
        ("IsRuntimeRcStringProducer", "Ashes.Text", "fromInt", 1),
        ("IsRuntimeRcStringProducer", "Ashes.Text", "toHex", 1),
        ("IsRuntimeRcStringProducer", "Ashes.Text", "fromFloat", 1),
        ("IsRuntimeRcStringProducer", "Ashes.Text", "formatFloat", 2),
        ("IsRuntimeRcStringProducer", "Ashes.Text", "asciiUpper", 1),
        ("IsRuntimeRcStringProducer", "Ashes.Text", "asciiLower", 1),
        ("IsRuntimeRcStringProducer", "Ashes.Text", "fromBigInt", 1),
        ("IsRuntimeRcStringProducer", "Ashes.Byte", "subText", 3),

        // Bytes producers -- Ashes.Byte.* buffer-building intrinsics; deliberately never subView,
        // which returns a borrowed view rather than a fresh owned buffer (see the negative control).
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "append", 2),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "appendByte", 2),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "fromList", 1),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "singleton", 1),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "empty", 1),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "u16Le", 1),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "u32Le", 1),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "u64Le", 1),

        // BigInt producers -- Ashes.Number.BigInt.* construction/arithmetic (never a view: BigInt has
        // no borrowed/sub-view representation, unlike Bytes).
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "fromInt", 1),
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "add", 2),
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "sub", 2),
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "mul", 2),
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "div", 2),
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "mod", 2),
    ];

    // Calls with the same syntactic shape as a whitelist entry but naming a member outside it -- either
    // a genuinely unrelated intrinsic, or (the subView rows) the borrowed-view sibling of a whitelisted
    // buffer-building call, which must stay excluded because it is not a fresh owned allocation.
    private static readonly (string Predicate, string Module, string Member, int Arity)[] NonWhitelistedFreshRcProducerControls =
    [
        ("IsRuntimeRcStringProducer", "Ashes.Text", "parseInt", 1),
        ("IsRuntimeRcStringProducer", "Ashes.Byte", "subView", 3),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "get", 2),
        ("IsRuntimeRcBytesProducer", "Ashes.Byte", "subView", 3),
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "compare", 2),
        ("IsRuntimeRcBigIntProducer", "Ashes.Number.BigInt", "toInt", 1),
    ];

    [Test]
    public void Every_whitelisted_intrinsic_is_recognized_via_the_builtin_registry_driven_path()
    {
        foreach (var (predicate, module, member, arity) in FreshRcProducerWhitelist)
        {
            InvokeProducerPredicate(predicate, BuildQualifiedCall(module, member, arity))
                .ShouldBeTrue($"{predicate} should recognize {module}.{member} (arity {arity}) via BuiltinRegistry metadata.");
        }
    }

    [Test]
    public void Non_whitelisted_intrinsics_and_the_borrowed_bytes_view_are_never_recognized()
    {
        foreach (var (predicate, module, member, arity) in NonWhitelistedFreshRcProducerControls)
        {
            InvokeProducerPredicate(predicate, BuildQualifiedCall(module, member, arity))
                .ShouldBeFalse($"{predicate} must not recognize {module}.{member} (arity {arity}) -- outside the pre-refactor whitelist.");
        }
    }

    [Test]
    public void Bytes_materialization_gate_rejects_program_lifetime_views()
    {
        InvokeProducerPredicate(
            "CanMaterializeOwnedBytes",
            BuildQualifiedCall("Ashes.Byte", "singleton", 1)).ShouldBeTrue();
        InvokeProducerPredicate(
            "CanMaterializeOwnedBytes",
            BuildQualifiedCall("Ashes.Byte", "subView", 3)).ShouldBeTrue();
        InvokeProducerPredicate(
            "CanMaterializeOwnedBytes",
            BuildQualifiedCall("Ashes.IO.File", "mmap", 1)).ShouldBeFalse();
    }

    private static Expr BuildQualifiedCall(string module, string member, int arity)
    {
        Expr call = new Expr.QualifiedVar(module, member);
        for (int i = 0; i < arity; i++)
        {
            call = new Expr.Call(call, new Expr.IntLit(0));
        }

        return call;
    }

    private static bool InvokeProducerPredicate(string predicateName, Expr expression)
    {
        var lowering = new Lowering(new Diagnostics());
        MethodInfo method = typeof(Lowering).GetMethod(predicateName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method '{predicateName}' not found via reflection.");
        return (bool)method.Invoke(lowering, [expression])!;
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
