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

    [Test]
    [Arguments("Some(('>', 1))", "header")]
    [Arguments("Some(('A', 1))", "other")]
    [Arguments("None", "other")]
    public async Task GroupLastCaseFailingNestedSubPattern_FallsThroughToTrailingDefault(string scrutinee, string expected)
    {
        // The Some group holds one case whose nested sub-pattern ('>', _) can fail even though the
        // outer tag matched. That failure must reach the trailing wildcard arm, not the
        // no-match path (which produced a null result and a segfault before the fix).
        string output = await CompileAndRunAsync(
            $$"""
            let classify = given value ->
                match value with
                | Some(('>', _)) -> "header"
                | _ -> "other"

            Ashes.IO.print(classify({{scrutinee}}))
            """).ConfigureAwait(false);

        output.ShouldBe($"{expected}\n");
    }

    [Test]
    [Arguments("Some(Def(name = \"a\", body = Some(\"b\")))", "default b")]
    [Arguments("Some(Def(name = \"a\", body = None))", "no default")]
    [Arguments("None", "missing")]
    public async Task DeadArmTrim_KeepsArmAfterNestedRecordSubPattern(string scrutinee, string expected)
    {
        // A record sub-pattern contributes no per-field constraint to the coverage engine, so
        // `None | Some(Def { body = None })` used to be judged exhaustive and the third arm dropped:
        // a Def with a body then fell off the match. The trim must decline once a record pattern
        // enters the prefix.
        string output = await CompileAndRunAsync(
            $$"""
            type Def =
                | name: Str
                | body: Maybe(Str)

            let classify = given value ->
                match value with
                | None -> "missing"
                | Some(Def { name = _n, body = None }) -> "no default"
                | Some(Def { name = _n, body = Some(b) }) -> "default " + b

            Ashes.IO.print(classify({{scrutinee}}))
            """).ConfigureAwait(false);

        output.ShouldBe($"{expected}\n");
    }

    [Test]
    [Arguments("(true, true)", "both")]
    [Arguments("(true, false)", "other")]
    [Arguments("(false, true)", "other")]
    [Arguments("(false, false)", "neither")]
    public async Task DeadArmTrim_KeepsWildcardAfterColumnwiseCoveredTuples(string scrutinee, string expected)
    {
        // The coverage engine checks tuple positions independently: after (true, true),
        // (true, false), and (false, true) each column has seen both literals, so the engine
        // reports nothing missing even though (false, false) is unmatched. The trailing wildcard
        // must survive, since literal sub-patterns are not an exact coverage shape.
        string output = await CompileAndRunAsync(
            $$"""
            let corners = given pair ->
                match pair with
                | (true, true) -> "both"
                | (true, false) -> "other"
                | (false, true) -> "other"
                | _ -> "neither"

            Ashes.IO.print(corners({{scrutinee}}))
            """).ConfigureAwait(false);

        output.ShouldBe($"{expected}\n");
    }

    [Test]
    public void DeadArmTrim_StillDropsWildcardAfterExactConstructorCoverage()
    {
        // The exact shape the trim exists for: every constructor covered by a catch-all-argument
        // pattern. The trailing wildcard is provably dead and its literal never reaches the program.
        var ir = LowerProgram(
            """
            type Shape =
                | Circle(Int)
                | Square(Int)

            match Circle(1) with
                | Circle(r) -> r
                | Square(s) -> s
                | _ -> 424242
            """);

        ir.Functions
            .Append(ir.EntryFunction)
            .SelectMany(f => f.Instructions)
            .OfType<IrInst.LoadConstInt>()
            .ShouldNotContain(load => load.Value == 424242, "the dead wildcard arm must be trimmed before lowering");
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
