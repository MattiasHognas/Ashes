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
