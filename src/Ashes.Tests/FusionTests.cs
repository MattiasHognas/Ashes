using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Lowering-time fusions that recognize a stdlib call by resolved identity and lower it to a single
/// pass over its own arguments instead of a call plus a separate consumer.
/// </summary>
public sealed class FusionTests
{
    [Test]
    public void Byte_fromList_of_List_reverse_lowers_to_a_reversed_single_pass_fill()
    {
        IrProgram program = LowerStandaloneProgram(
            """
            import Ashes.Collection.List

            let bytes = Ashes.Byte.fromList(Ashes.Collection.List.reverse([1u8, 2u8, 3u8]))
            in Ashes.Byte.length(bytes)
            """);

        AllInstructions(program).OfType<IrInst.BytesFromList>()
            .ShouldContain(inst => inst.Reversed);
    }

    [Test]
    public void Byte_fromList_of_List_reverse_fires_through_an_unaliased_selector_import()
    {
        IrProgram program = LowerStandaloneProgram(
            """
            import Ashes.Collection.List.reverse

            let bytes = Ashes.Byte.fromList(reverse([1u8, 2u8, 3u8]))
            in Ashes.Byte.length(bytes)
            """);

        AllInstructions(program).OfType<IrInst.BytesFromList>()
            .ShouldContain(inst => inst.Reversed);
    }

    [Test]
    public void Byte_fromList_declines_fusion_when_reverse_is_the_users_own_binding()
    {
        // A same-named user function is not the stdlib's List.reverse, so the fusion — gated on
        // resolved callee identity, not the spelling `reverse` — must not fire for it.
        IrProgram program = LowerStandaloneProgram(
            """
            let recursive reverse xs acc =
                match xs with
                    | [] -> acc
                    | head :: tail -> reverse(tail)(head :: acc)

            let bytes = Ashes.Byte.fromList(reverse([1u8, 2u8, 3u8])([]))
            in Ashes.Byte.length(bytes)
            """);

        AllInstructions(program).OfType<IrInst.BytesFromList>()
            .ShouldAllBe(inst => !inst.Reversed);
    }

    [Test]
    public void Byte_fromList_declines_fusion_for_an_extra_consumer_between_reverse_and_fromList()
    {
        // reverse's result is bound and used a second time, so the argument to fromList is a Var,
        // not a saturated call to List.reverse — the fused shape does not match, and the call lowers
        // ordinarily instead of silently reinterpreting an unrelated later use.
        IrProgram program = LowerStandaloneProgram(
            """
            import Ashes.Collection.List

            let reversed = Ashes.Collection.List.reverse([1u8, 2u8, 3u8])
            let bytes = Ashes.Byte.fromList(reversed)
            in Ashes.Byte.length(bytes) + Ashes.Collection.List.length(reversed)
            """);

        AllInstructions(program).OfType<IrInst.BytesFromList>()
            .ShouldAllBe(inst => !inst.Reversed);
    }

    [Test]
    public void FoldLeft_of_map_fuses_into_one_pass_with_no_intermediate_cons_cells()
    {
        // The fused entry never calls into Ashes.Collection.List.map's own compiled function at all —
        // the whole point of the fusion is that the mapped list is never materialized, so nothing ever
        // needs to call map to build it. A raw Alloc(16) count is unusable here: both programs stitch
        // in the same stdlib module, whose own internal cons-cell allocations (present whether or not
        // any of its functions are actually called from this entry) would swamp any real signal.
        IrProgram fused = LowerStandaloneProgram(
            """
            import Ashes.Collection.List

            let square x = x * x
            let add a b = a + b

            Ashes.Collection.List.foldLeft(add)(0)(Ashes.Collection.List.map(square)([1, 2, 3]))
            """);
        IrProgram declined = LowerStandaloneProgram(
            """
            import Ashes.Collection.List

            let square x = x * x
            let add a b = a + b
            let mapped = Ashes.Collection.List.map(square)([1, 2, 3])

            Ashes.Collection.List.foldLeft(add)(0)(mapped) + Ashes.Collection.List.length(mapped)
            """);

        CallsQualifiedFunction(fused, "Ashes.Collection.List.map").ShouldBeFalse();
        CallsQualifiedFunction(declined, "Ashes.Collection.List.map").ShouldBeTrue();
    }

    [Test]
    public void FoldLeft_of_map_declines_fusion_when_foldLeft_and_map_are_the_users_own_bindings()
    {
        // Same-named user functions, unrelated to Ashes.Collection.List's — resolved callee identity,
        // not the literal spellings foldLeft/map, gates the fusion — must cost exactly as much as an
        // otherwise-identical program whose own fusion attempt is forced to decline for an unrelated
        // reason (an extra consumer of the mapped list), not the fewer allocations a real fusion would
        // give. Both programs declare the very same foldLeft/map/square/add, so their shared baseline
        // overhead (the user's own recursive-closure machinery) cancels out of the comparison.
        IrProgram shadowed = LowerStandaloneProgram(
            """
            let recursive foldLeft f init xs =
                match xs with
                    | [] -> init
                    | head :: tail -> foldLeft(f)(f(init)(head))(tail)

            let recursive map g xs =
                match xs with
                    | [] -> []
                    | head :: tail -> g(head) :: map(g)(tail)

            let square x = x * x
            let add a b = a + b

            foldLeft(add)(0)(map(square)([1, 2, 3]))
            """);
        IrProgram unfusable = LowerStandaloneProgram(
            """
            let recursive foldLeft f init xs =
                match xs with
                    | [] -> init
                    | head :: tail -> foldLeft(f)(f(init)(head))(tail)

            let recursive map g xs =
                match xs with
                    | [] -> []
                    | head :: tail -> g(head) :: map(g)(tail)

            let square x = x * x
            let add a b = a + b
            let mapped = map(square)([1, 2, 3])

            foldLeft(add)(0)(mapped) + foldLeft(add)(0)(mapped)
            """);

        (CountSixteenByteConsAllocs(shadowed) - CountSixteenByteConsAllocs(unfusable)).ShouldBe(0);
    }

    private static int CountSixteenByteConsAllocs(IrProgram program)
        => AllInstructions(program).OfType<IrInst.Alloc>().Count(inst => inst.SizeBytes == 16);

    // Approximate closure-provenance tracking: within each function's own straight-line instruction
    // order, remembers which qualified source function a temp/local most recently came from
    // (MakeClosure/MakeClosureStack create it, Borrow/StoreLocal/LoadLocal propagate it), and checks
    // whether any CallClosure's closure temp traces back to the target. A forward last-write-wins scan
    // is good enough here: it only needs to tell whether a specific stdlib function was ever actually
    // invoked, not perform general dataflow analysis.
    // Every compiled function the named source function produced, not just the first: an element
    // specialization is a copy of that same source lowered at one call site's concrete types, so a
    // call to the copy is a call to this function exactly as much as a call to the generic original is.
    private static bool CallsQualifiedFunction(IrProgram program, string qualifiedName)
    {
        HashSet<string> targetLabels = program.Functions
            .Where(f => string.Equals(f.Origin?.Source?.QualifiedName, qualifiedName, StringComparison.Ordinal))
            .Select(f => f.Label)
            .ToHashSet(StringComparer.Ordinal);
        if (targetLabels.Count == 0)
        {
            return false;
        }

        bool CallsInFunction(IEnumerable<IrInst> instructions)
        {
            var tempOrigin = new Dictionary<int, string>();
            var slotOrigin = new Dictionary<int, string>();
            foreach (IrInst inst in instructions)
            {
                switch (inst)
                {
                    case IrInst.MakeClosure mc:
                        tempOrigin[mc.Target] = mc.FuncLabel;
                        break;
                    case IrInst.MakeClosureStack mcs:
                        tempOrigin[mcs.Target] = mcs.FuncLabel;
                        break;
                    case IrInst.Borrow borrow when tempOrigin.TryGetValue(borrow.SourceTemp, out string? borrowedFrom):
                        tempOrigin[borrow.Target] = borrowedFrom;
                        break;
                    case IrInst.StoreLocal sl when tempOrigin.TryGetValue(sl.Source, out string? fromTemp):
                        slotOrigin[sl.Slot] = fromTemp;
                        break;
                    case IrInst.LoadLocal ll when slotOrigin.TryGetValue(ll.Slot, out string? fromSlot):
                        tempOrigin[ll.Target] = fromSlot;
                        break;
                    case IrInst.CallClosure cc when tempOrigin.TryGetValue(cc.ClosureTemp, out string? callee):
                        if (targetLabels.Contains(callee))
                        {
                            return true;
                        }
                        break;
                }
            }
            return false;
        }

        return CallsInFunction(program.EntryFunction.Instructions)
            || program.Functions.Any(f => CallsInFunction(f.Instructions));
    }

    private static IEnumerable<IrInst> AllInstructions(IrProgram program)
        => program.EntryFunction.Instructions.Concat(program.Functions.SelectMany(function => function.Instructions));

    private static IrProgram LowerStandaloneProgram(string sourceWithImports, string filePath = "<mem>")
    {
        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(sourceWithImports, filePath);
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports,
            parsed.ImportNames,
            filePath,
            parsed.ImportSelectors);

        Diagnostics diagnostics = new();
        Program syntax = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext(layout);
        IrProgram program = lowering.Lower(syntax);
        diagnostics.ThrowIfAny();
        return program;
    }
}
