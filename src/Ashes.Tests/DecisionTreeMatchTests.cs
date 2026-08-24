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
    public void NonTrivialNestedSubPattern_SharesOuterTagSwitch()
    {
        // A nested constructor sub-pattern (A's Wrap(x)) used to disable tag-switch
        // dispatch for the whole match, forcing every distinct-tag arm through a fully linear
        // chain even though B/C/D/F's own patterns are trivial. TryPlanTagGroupSwitch now shares
        // one outer GetAdtTag/SwitchTag across all five arms regardless, testing A's own nested
        // sub-pattern only within A's own switch arm.
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

        var switches = SwitchTags(ir);
        switches.Count.ShouldBe(1, "the outer A/B/C/D/F tag test should still be shared across every arm");
        switches[0].Cases.Count.ShouldBe(5);
    }

    [Test]
    public void RepeatedTagWithNestedSubPattern_SharesOuterTagAcrossBothArms()
    {
        // Two arms share the Node tag, one with a nested
        // sub-pattern (Node(Leaf,_,Leaf)) more specific than the other's trivial catch-all
        // (Node(l,_,r)). TryPlanTagGroupSwitch groups both under one Node case in the outer
        // switch, testing the nested sub-pattern only once, within that shared arm.
        var ir = LowerProgram(
            """
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            match Leaf with
                | Leaf -> 0
                | Node(Leaf, _, Leaf) -> 1
                | Node(l, _, r) -> 2
            """);

        var switches = SwitchTags(ir);
        switches.Count.ShouldBe(1, "Leaf and Node should share one outer tag switch");
        switches[0].Cases.Count.ShouldBe(2, "one case per distinct outer tag, not per source arm");
    }

    [Test]
    [Arguments("Leaf", 0)]
    [Arguments("Node(Leaf, 5, Leaf)", 1)]
    [Arguments("Node(Node(Leaf, 1, Leaf), 5, Leaf)", 2)]
    [Arguments("Node(Leaf, 5, Node(Leaf, 1, Leaf))", 2)]
    public async Task RepeatedTagWithNestedSubPattern_ProducesCorrectResultForEveryShape(string treeExpr, int expected)
    {
        string output = await CompileAndRunAsync(
            $$"""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let classify = given t ->
                match t with
                | Leaf -> 0
                | Node(Leaf, _, Leaf) -> 1
                | Node(l, _, r) -> 2

            let result = classify({{treeExpr}})
            in Ashes.IO.print(result)
            """).ConfigureAwait(false);

        output.ShouldBe($"{expected}\n");
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

    private static async Task<string> CompileAndRunAsync(string source)
    {
        var ir = IrOptimizer.Optimize(LowerProgram(source));
        var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"opt_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
        string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return stdout;
    }
}
