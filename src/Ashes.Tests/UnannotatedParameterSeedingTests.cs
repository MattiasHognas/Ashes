using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Inference is interleaved with lowering, so an un-annotated parameter's type is still a variable
// when the entry-normalization decision is made ahead of the body. Seeding the parameter from the
// constructor field it is passed to directly lets that decision see the resolved type, so the
// record storing the parameter is placed on the reference-counted heap exactly as it is when the
// parameter carries an annotation.
public sealed class UnannotatedParameterSeedingTests
{
    private const string ItemType = """
        type Item =
            | name: Str
            | flag: Bool

        """;

    [Test]
    public void Unannotated_record_builder_parameter_is_normalized_like_an_annotated_one()
    {
        IrProgram annotated = LowerProgram(ItemType + """
            let toEntry (n: Str) = Item(name = n, flag = true)

            Ashes.IO.print("done")
            """);
        IrProgram unannotated = LowerProgram(ItemType + """
            let toEntry n = Item(name = n, flag = true)

            Ashes.IO.print("done")
            """);

        IrFunction unannotatedBuilder = BuilderFunction(unannotated);
        unannotatedBuilder.Instructions.Any(inst => inst is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        FormatInstructions(unannotatedBuilder).ShouldBe(FormatInstructions(BuilderFunction(annotated)));
    }

    [Test]
    public void Seed_reaches_a_field_inside_a_match_arm()
    {
        const string annotatedSource = ItemType + """
            let toEntry (n: Str) =
                match 1 with
                    | 1 -> Item(name = n, flag = true)
                    | _ -> Item(name = n, flag = false)

            Ashes.IO.print("done")
            """;
        const string unannotatedSource = ItemType + """
            let toEntry n =
                match 1 with
                    | 1 -> Item(name = n, flag = true)
                    | _ -> Item(name = n, flag = false)

            Ashes.IO.print("done")
            """;

        FormatInstructions(BuilderFunction(LowerProgram(unannotatedSource)))
            .ShouldBe(FormatInstructions(BuilderFunction(LowerProgram(annotatedSource))));
    }

    // The inner `n` is a string; the parameter itself is an integer. A seed that looked through
    // the shadowing binder would unify the parameter with `Str` and the addition would not type.
    [Test]
    public void Seed_stops_at_a_binder_that_shadows_the_parameter()
    {
        IrProgram ir = LowerProgram(ItemType + """
            let toEntry n =
                let bigger = n + 1
                in
                    let n = "fixed"
                    in Item(name = n, flag = bigger > 2)

            Ashes.IO.print("done")
            """);

        BuilderFunction(ir).Instructions.Any(inst => inst is IrInst.AllocAdt { FieldCount: 2 }).ShouldBeTrue();
    }

    // The source function itself, not the record copier synthesized beside a runtime-managed
    // builder (`__deepcopy_N`), which allocates the same record.
    private static IrFunction BuilderFunction(IrProgram ir)
        => ir.Functions.Single(function =>
            function.Label.StartsWith("lambda_", StringComparison.Ordinal)
            && function.Instructions.Any(inst => inst is IrInst.AllocAdt { FieldCount: 2 }));

    private static string FormatInstructions(IrFunction function)
        => string.Join("\n", function.Instructions.Select(inst => inst.ToString()));

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
