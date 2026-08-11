using System.Diagnostics;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// The <c>--emit-ir</c> dump. Its text is a debugging artifact rather than a contract, so nothing here
/// snapshots it — the assertions are about which stage was shown, which functions, and that asking
/// changed nothing.
/// </summary>
public sealed class IrDumpTests
{
    // Driven through the CLI, which resolves the import header.
    private const string Program = """
        import Ashes.IO
        import Ashes.Text

        let build n = Ashes.Text.fromInt(n) + "-tail"

        let recursive loop n total =
            if n <= 0
            then total
            else loop(n - 1)(total + Ashes.Text.byteLength(build(n)))

        Ashes.IO.print(loop(3)(0))
        """;

    // Lowered directly, where the bare parser has no import resolution, so the same program without
    // its header.
    private const string UnitProgram = """
        let build n = Ashes.Text.fromInt(n) + "-tail"

        let recursive loop n total =
            if n <= 0
            then total
            else loop(n - 1)(total + Ashes.Text.byteLength(build(n)))

        Ashes.IO.print(loop(3)(0))
        """;

    private const string TraitProgram = """
        trait Render(a) =
            | render : a -> Str

        implement Render(Int) =
            | render = given (value) -> Ashes.Text.fromInt(value)

        let show : a -> Str requires {Render(a)} =
            given (value) -> Render.render(value)

        show(42)
        """;

    [Test]
    public void A_known_stage_parses_and_an_unknown_one_is_rejected()
    {
        IrDumpRequest.TryParseValue("final", out var stage, out var filter, out var error).ShouldBeTrue();
        stage.ShouldBe(IrDumpStage.Final);
        filter.ShouldBeNull();
        error.ShouldBeNull();

        IrDumpRequest.TryParseValue("optimised", out _, out _, out var failure).ShouldBeFalse();
        failure.ShouldBe("Unknown IR stage 'optimised'.");
    }

    [Test]
    public void A_selector_is_parsed_off_the_stage()
    {
        IrDumpRequest.TryParseValue("lowered:Map.set", out var stage, out var filter, out _).ShouldBeTrue();
        stage.ShouldBe(IrDumpStage.Lowered);
        filter.ShouldBe("Map.set");
    }

    [Test]
    public void The_dump_shows_instructions_operands_and_source_locations()
    {
        IReadOnlyList<string> lines = Dump(IrDumpStage.Lowered, "build");
        string text = string.Join('\n', lines);

        text.ShouldContain("IR (lowered)");
        text.ShouldContain("function ");
        text.ShouldContain("ConcatStr");
        // Operands, not just opcodes: a dump without them cannot answer which value was involved.
        text.ShouldContain("Target=");
        // Locations, so a line can be traced back to source.
        text.ShouldContain("explain.ash:");
    }

    [Test]
    public void Unset_operands_are_omitted()
    {
        string text = string.Join('\n', Dump(IrDumpStage.Final, filter: null));

        // The IR spells "unset" as false, null, or -1; printing those buries the operands that carry
        // meaning, so they are left out entirely rather than shown as noise.
        text.ShouldNotContain("=false");
        text.ShouldNotContain("=null");
        text.ShouldNotContain("=-1");
    }

    [Test]
    public void Instructions_carry_no_ordinal_so_stages_diff_cleanly()
    {
        IReadOnlyList<string> lowered = Dump(IrDumpStage.Lowered, "loop");
        IReadOnlyList<string> final = Dump(IrDumpStage.Final, "loop");

        // The optimizer removes instructions here. With absolute indices every following line would
        // register as changed; without them, only the removed ones differ.
        lowered.Count.ShouldBeGreaterThan(final.Count);
        int shared = lowered.Intersect(final, StringComparer.Ordinal).Count();
        shared.ShouldBeGreaterThan(final.Count / 2);
    }

    [Test]
    public void A_selector_restricts_the_dump()
    {
        IReadOnlyList<string> all = Dump(IrDumpStage.Final, filter: null);
        IReadOnlyList<string> filtered = Dump(IrDumpStage.Final, "build");

        int Functions(IEnumerable<string> lines) =>
            lines.Count(line => line.StartsWith("function ", StringComparison.Ordinal));

        Functions(filtered).ShouldBeGreaterThan(0);
        Functions(filtered).ShouldBeLessThan(Functions(all));
    }

    [Test]
    public void A_selector_matching_nothing_says_so()
    {
        string text = string.Join('\n', Dump(IrDumpStage.Final, "no_such_function"));

        text.ShouldContain("(no functions matched)");
    }

    [Test]
    public void Trait_evidence_identifies_hidden_dictionary_parameters_and_resolved_implementations()
    {
        string text = string.Join('\n', Dump(TraitProgram, IrDumpStage.Lowered, filter: null));

        text.ShouldContain("trait evidence");
        text.ShouldContain("dictionary-parameter function=show source=explain.ash:");
        text.ShouldContain("index=0 trait=Render methods=[render]");
        text.ShouldContain("resolved requirement=Render(Int)");
        text.ShouldContain("implementation=Main");
        text.ShouldNotContain("0x");
    }

    [Test]
    public async Task The_dump_goes_to_stderr_and_leaves_program_output_alone()
    {
        await WithProgramAsync(async path =>
        {
            var plain = await RunCliAsync("run", path).ConfigureAwait(false);
            var dumped = await RunCliAsync("run", path, "--emit-ir", "final").ConfigureAwait(false);

            dumped.Stdout.ShouldBe(plain.Stdout);
            dumped.Stderr.ShouldContain("IR (final)");
            dumped.Stdout.ShouldNotContain("IR (final)");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Both_stages_can_be_requested_at_once()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync(
                "compile", path, "-o", path + ".out",
                "--emit-ir", "lowered",
                "--emit-ir", "final").ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Stderr.ShouldContain("IR (lowered)");
            result.Stderr.ShouldContain("IR (final)");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task An_unknown_stage_is_a_usage_error()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("compile", path, "-o", path + ".out", "--emit-ir", "bogus")
                .ConfigureAwait(false);

            result.ExitCode.ShouldNotBe(0);
            result.Stderr.ShouldContain("Unknown IR stage 'bogus'.");
            result.Stderr.ShouldContain("lowered");
            result.Stderr.ShouldContain("final");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Nothing_is_dumped_without_the_option()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("compile", path, "-o", path + ".out").ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldNotContain("IR (");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Dumping_does_not_change_the_compiled_image()
    {
        await WithProgramAsync(async path =>
        {
            string plainPath = path + ".plain";
            string dumpedPath = path + ".dumped";

            (await RunCliAsync("compile", path, "-o", plainPath).ConfigureAwait(false)).ExitCode.ShouldBe(0);
            (await RunCliAsync("compile", path, "-o", dumpedPath, "--emit-ir", "lowered", "--emit-ir", "final")
                .ConfigureAwait(false)).ExitCode.ShouldBe(0);

            (await File.ReadAllBytesAsync(dumpedPath).ConfigureAwait(false))
                .ShouldBe(await File.ReadAllBytesAsync(plainPath).ConfigureAwait(false));
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Help_documents_the_option()
    {
        var result = await RunCliAsync("--help").ConfigureAwait(false);

        result.Output.ShouldContain("--emit-ir");
        result.Output.ShouldContain("lowered");
    }

    private static IReadOnlyList<string> Dump(IrDumpStage stage, string? filter)
        => Dump(UnitProgram, stage, filter);

    private static IReadOnlyList<string> Dump(string source, IrDumpStage stage, string? filter)
    {
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("explain.ash", source);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();

        return IrTextFormatter.Format(
            stage == IrDumpStage.Final ? IrOptimizer.Optimize(ir) : ir,
            stage,
            filter);
    }

    private static async Task WithProgramAsync(Func<string, Task> body)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ashes-emitir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "dump_program.ash");
            await File.WriteAllTextAsync(path, Program).ConfigureAwait(false);
            await body(path).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task<CliCommandResult> RunCliAsync(params string[] args)
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync(args).ConfigureAwait(false);
        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CliCommandResult(process.ExitCode, stdout, stderr, stdout + stderr);
    }

    private sealed record CliCommandResult(int ExitCode, string Stdout, string Stderr, string Output);
}
