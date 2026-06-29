using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Covers decision-tree pattern matching: matches over many single-ADT constructor arms are
/// lowered to a single <see cref="IrInst.SwitchTag"/> dispatch, while small or ineligible matches
/// keep the linear chain of tag comparisons.
/// </summary>
public sealed class DecisionTreeMatchTests
{
    [Test]
    public void ManyConstructorMatch_LowersToTagSwitch()
    {
        var switches = SwitchTags(LowerProgram(
            """
            type E =
                | A
                | B
                | C
                | D
                | F
                | G

            match A with
                | A -> 0
                | B -> 1
                | C -> 2
                | D -> 3
                | F -> 4
                | G -> 5
            """));

        switches.Count.ShouldBe(1, "a six-arm single-ADT match should dispatch via one tag switch");
        switches[0].Cases.Count.ShouldBe(6);
        switches[0].Cases.Select(c => c.Tag).ShouldBe(new long[] { 0, 1, 2, 3, 4, 5 });
    }

    [Test]
    public void ManyConstructorMatch_WithPayloadBindings_LowersToTagSwitch()
    {
        var switches = SwitchTags(LowerProgram(
            """
            type Color =
                | Red(Int)
                | Green(Int)
                | Blue(Int)
                | Yellow(Int)
                | Purple(Int)
                | Orange(Int)

            match Red(1) with
                | Red(x) -> x
                | Green(_) -> 0
                | Blue(_) -> 0
                | Yellow(_) -> 0
                | Purple(_) -> 0
                | Orange(_) -> 0
            """));

        switches.Count.ShouldBe(1, "trivial payload bindings stay eligible for the tag switch");
        switches[0].Cases.Count.ShouldBe(6);
    }

    [Test]
    public void SmallConstructorMatch_KeepsLinearChain()
    {
        var ir = LowerProgram(
            """
            type E =
                | A
                | B
                | C
                | D

            match A with
                | A -> 0
                | B -> 1
                | C -> 2
                | D -> 3
            """);

        SwitchTags(ir).ShouldBeEmpty("four or fewer arms stay below the decision-tree threshold");
    }

    [Test]
    public void NonTrivialNestedSubPattern_DisablesTagSwitch()
    {
        var ir = LowerProgram(
            """
            type Inner =
                | Wrap(Int)

            type Outer =
                | A(Inner)
                | B(Int)
                | C(Int)
                | D(Int)
                | F(Int)

            match B(0) with
                | A(Wrap(x)) -> x
                | B(_) -> 1
                | C(_) -> 2
                | D(_) -> 3
                | F(_) -> 4
            """);

        SwitchTags(ir).ShouldBeEmpty("a nested constructor sub-pattern can fail, so the linear path is required");
    }

    private static List<IrInst.SwitchTag> SwitchTags(IrProgram program)
    {
        return program.Functions
            .Append(program.EntryFunction)
            .SelectMany(f => f.Instructions)
            .OfType<IrInst.SwitchTag>()
            .ToList();
    }

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
