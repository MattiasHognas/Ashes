using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class SingleConstructorUnboxingTests
{
    [Test]
    public void Single_constructor_type_allocates_and_reads_tagless_cells_with_no_tag_test()
    {
        IrProgram ir = LowerProgram(
            """
            type Point =
                | Point(Int, Int)

            let manhattan = given p ->
                match p with
                    | Point(x, y) -> x + y

            Ashes.IO.print(manhattan(Point(3)(4)))
            """);

        List<IrInst> all = AllInstructions(ir);
        all.OfType<IrInst.AllocAdt>().Where(alloc => alloc.FieldCount == 2)
            .ShouldAllBe(alloc => alloc.Tagless, "the Point cell must be laid out without a tag word");
        all.OfType<IrInst.SetAdtField>().ShouldAllBe(store => store.Tagless);
        all.OfType<IrInst.GetAdtField>().ShouldAllBe(load => load.Tagless);
        all.ShouldNotContain(instruction => instruction is IrInst.GetAdtTag,
            "a one-constructor scrutinee has no tag to test");
        all.ShouldNotContain(instruction => instruction is IrInst.SwitchTag);
    }

    [Test]
    public void Record_type_is_tagless_and_field_access_reads_at_offset_zero()
    {
        IrProgram ir = LowerProgram(
            """
            type Person =
                | name: Str
                | age: Int

            let bob = Person(name = "bob", age = 41)
            let older = bob with age = bob.age + 1
            Ashes.IO.print(older.age)
            """);

        List<IrInst> all = AllInstructions(ir);
        all.OfType<IrInst.AllocAdt>().Where(alloc => alloc.FieldCount == 2).ShouldAllBe(alloc => alloc.Tagless);
        all.OfType<IrInst.GetAdtField>().ShouldAllBe(load => load.Tagless);
        all.OfType<IrInst.SetAdtField>().ShouldAllBe(store => store.Tagless);
    }

    [Test]
    public void Multi_constructor_and_nullary_types_keep_the_tagged_layout()
    {
        IrProgram ir = LowerProgram(
            """
            type Shape =
                | Circle(Int)
                | Rect(Int, Int)

            type Marker =
                | Marker

            let area = given shape ->
                match shape with
                    | Circle(r) -> r * r * 3
                    | Rect(w, h) -> w * h

            let m = Marker
            Ashes.IO.print(area(Rect(3)(4)) + area(Circle(2)))
            """);

        List<IrInst> all = AllInstructions(ir);
        all.OfType<IrInst.AllocAdt>().ShouldAllBe(alloc => !alloc.Tagless);
        all.OfType<IrInst.GetAdtField>().ShouldAllBe(load => !load.Tagless);
        all.OfType<IrInst.SetAdtField>().ShouldAllBe(store => !store.Tagless);
        all.ShouldContain(instruction => instruction is IrInst.GetAdtTag,
            "a two-constructor scrutinee still dispatches on its tag");
    }

    [Test]
    public void Builtin_option_and_result_keep_the_tagged_layout()
    {
        IrProgram ir = LowerProgram(
            """
            let value = Some(41)
            let shown =
                match value with
                    | Some(n) -> n + 1
                    | None -> 0
            let result = Ok(shown)
            Ashes.IO.print(
                match result with
                    | Ok(n) -> n
                    | Error(_) -> 0)
            """);

        List<IrInst> all = AllInstructions(ir);
        all.OfType<IrInst.AllocAdt>().ShouldAllBe(alloc => !alloc.Tagless);
        all.OfType<IrInst.GetAdtField>().ShouldAllBe(load => !load.Tagless);
    }

    [Test]
    [Arguments("Point(3)(4)", 7)]
    [Arguments("Point(0)(0)", 0)]
    public async Task Tagless_cells_produce_correct_results_at_runtime(string point, int expected)
    {
        string output = await CompileAndRunAsync(
            $$"""
            type Point =
                | Point(Int, Int)

            type Box =
                | Box(Point, Str)

            let manhattan = given p ->
                match p with
                    | Point(x, y) -> x + y

            let recursive walk = given k -> given b -> given acc ->
                if k == 0 then acc
                else
                    match b with
                        | Box(p, label) -> walk(k - 1)(Box(p)(label + "."))(acc + manhattan(p))

            Ashes.IO.print(walk(1000)(Box({{point}})(""))(0))
            """).ConfigureAwait(false);

        output.ShouldBe($"{expected * 1000}\n");
    }

    private static List<IrInst> AllInstructions(IrProgram program)
    {
        return program.Functions
            .Append(program.EntryFunction)
            .SelectMany(f => f.Instructions)
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

        var exePath = Path.Combine(tmpDir, $"unbox_{Guid.NewGuid():N}");
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
