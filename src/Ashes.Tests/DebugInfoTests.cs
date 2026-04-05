using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class DebugInfoTests
{
    // ── SourceTextUtils tests ────────────────────────────────────────────

    [Test]
    public void GetLineStarts_returns_offsets_of_line_beginnings()
    {
        var text = "abc\ndef\nghi";
        var starts = SourceTextUtils.GetLineStarts(text);
        starts.ShouldBe([0, 4, 8]);
    }

    [Test]
    public void GetLineStarts_single_line_returns_zero()
    {
        var starts = SourceTextUtils.GetLineStarts("hello");
        starts.ShouldBe([0]);
    }

    [Test]
    public void GetLineStarts_empty_string_returns_zero()
    {
        var starts = SourceTextUtils.GetLineStarts("");
        starts.ShouldBe([0]);
    }

    [Test]
    public void ToLineColumn_first_character_is_1_1()
    {
        var starts = SourceTextUtils.GetLineStarts("hello");
        var (line, col) = SourceTextUtils.ToLineColumn(starts, 5, 0);
        line.ShouldBe(1);
        col.ShouldBe(1);
    }

    [Test]
    public void ToLineColumn_second_line_start()
    {
        var text = "ab\ncd";
        var starts = SourceTextUtils.GetLineStarts(text);
        var (line, col) = SourceTextUtils.ToLineColumn(starts, text.Length, 3);
        line.ShouldBe(2);
        col.ShouldBe(1);
    }

    [Test]
    public void ToLineColumn_middle_of_second_line()
    {
        var text = "ab\ncd";
        var starts = SourceTextUtils.GetLineStarts(text);
        var (line, col) = SourceTextUtils.ToLineColumn(starts, text.Length, 4);
        line.ShouldBe(2);
        col.ShouldBe(2);
    }

    [Test]
    public void ToLineColumn_position_at_end_of_text()
    {
        var text = "ab\ncd";
        var starts = SourceTextUtils.GetLineStarts(text);
        var (line, col) = SourceTextUtils.ToLineColumn(starts, text.Length, text.Length);
        line.ShouldBe(2);
        col.ShouldBe(3);
    }

    [Test]
    public void ToLineColumn_clamps_negative_position()
    {
        var starts = SourceTextUtils.GetLineStarts("hello");
        var (line, col) = SourceTextUtils.ToLineColumn(starts, 5, -10);
        line.ShouldBe(1);
        col.ShouldBe(1);
    }

    [Test]
    public void ToLineColumn_clamps_beyond_end_position()
    {
        var text = "abc";
        var starts = SourceTextUtils.GetLineStarts(text);
        var (line, col) = SourceTextUtils.ToLineColumn(starts, text.Length, 100);
        line.ShouldBe(1);
        col.ShouldBe(4);
    }

    // ── SourceLocation propagation tests ─────────────────────────────────

    [Test]
    public void IrInst_Location_defaults_to_null()
    {
        var inst = new IrInst.LoadConstInt(0, 42);
        inst.Location.ShouldBeNull();
    }

    [Test]
    public void IrInst_Location_can_be_set()
    {
        var inst = new IrInst.LoadConstInt(0, 42);
        inst.Location = new SourceLocation("test.ash", 1, 1);
        inst.Location.ShouldNotBeNull();
        inst.Location.Value.FilePath.ShouldBe("test.ash");
        inst.Location.Value.Line.ShouldBe(1);
        inst.Location.Value.Column.ShouldBe(1);
    }

    [Test]
    public void Lowering_with_source_context_tags_instructions()
    {
        var source = "let x = 42 in x + 1";
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.Errors.Count.ShouldBe(0);

        var lowering = new Lowering(diag);
        lowering.SetSourceContext("test.ash", source);
        var ir = lowering.Lower(program);

        // At least some instructions should have locations
        var locatedInstructions = ir.EntryFunction.Instructions
            .Where(inst => inst.Location is not null)
            .ToList();
        locatedInstructions.Count.ShouldBeGreaterThan(0);

        // All locations should reference test.ash
        foreach (var inst in locatedInstructions)
        {
            inst.Location!.Value.FilePath.ShouldBe("test.ash");
            inst.Location.Value.Line.ShouldBeGreaterThan(0);
            inst.Location.Value.Column.ShouldBeGreaterThan(0);
        }
    }

    [Test]
    public void Lowering_without_source_context_leaves_locations_null()
    {
        var source = "let x = 42 in x + 1";
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.Errors.Count.ShouldBe(0);

        var lowering = new Lowering(diag);
        var ir = lowering.Lower(program);

        // No instructions should have locations when context is not set
        ir.EntryFunction.Instructions
            .Any(inst => inst.Location is not null)
            .ShouldBeFalse();
    }

    [Test]
    public void Lowering_tags_lambda_function_instructions()
    {
        var source = "let f = fun (x) -> x + 1 in f(41)";
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.Errors.Count.ShouldBe(0);

        var lowering = new Lowering(diag);
        lowering.SetSourceContext("lambda.ash", source);
        var ir = lowering.Lower(program);

        // Lambda functions should also have tagged instructions
        ir.Functions.Count.ShouldBeGreaterThan(0);
        var lambdaLocated = ir.Functions[0].Instructions
            .Where(inst => inst.Location is not null)
            .ToList();
        lambdaLocated.Count.ShouldBeGreaterThan(0);
    }

    // ── BackendCompileOptions tests ──────────────────────────────────────

    [Test]
    public void BackendCompileOptions_EmitDebugInfo_defaults_to_false()
    {
        BackendCompileOptions.Default.EmitDebugInfo.ShouldBeFalse();
    }

    [Test]
    public void BackendCompileOptions_with_debug_true()
    {
        var options = new BackendCompileOptions(BackendOptimizationLevel.O0, EmitDebugInfo: true);
        options.EmitDebugInfo.ShouldBeTrue();
        options.OptimizationLevel.ShouldBe(BackendOptimizationLevel.O0);
    }

    // ── CombinedCompilationLayout ModuleOffsets tests ────────────────────

    [Test]
    public void Standalone_layout_includes_entry_module_offset()
    {
        var source = "42";
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(source, []);
        layout.ModuleOffsets.Count.ShouldBeGreaterThan(0);

        // The entry module should cover the entire source
        var entryOffset = layout.ModuleOffsets[^1];
        entryOffset.FilePath.ShouldBe("<memory>");
        entryOffset.StartOffset.ShouldBeGreaterThanOrEqualTo(0);
        entryOffset.EndOffset.ShouldBeGreaterThanOrEqualTo(entryOffset.StartOffset);
    }

    // ── CLI flag parsing tests ───────────────────────────────────────────

    [Test]
    public async Task Compile_accepts_debug_flag()
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync("compile", "--debug", "--expr", "42");
        startInfo.ShouldNotBeNull();
    }

    [Test]
    public async Task Compile_accepts_g_flag()
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync("compile", "-g", "--expr", "42");
        startInfo.ShouldNotBeNull();
    }
}
