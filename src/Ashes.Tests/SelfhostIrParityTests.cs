using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class SelfhostIrParityTests
{
    private static readonly ExplainKind[] SharedExplainKinds =
        [ExplainKind.Ownership, ExplainKind.Rc, ExplainKind.Reuse, ExplainKind.Memory];

    [Test]
    [Arguments("simple_arith")]
    [Arguments("let_bindings")]
    [Arguments("nested_let_scopes")]
    [Arguments("scalar_match")]
    [Arguments("ownerless_match")]
    [Arguments("closure_capture")]
    [Arguments("pattern_match")]
    [Arguments("mutual_recursion")]
    [Arguments("heap_result_list")]
    [Arguments("match_list_scrutinee_drop")]
    [Arguments("aggregate_children_retain")]
    [Arguments("call_argument_retain")]
    [Arguments("call_result_copy_out")]
    [Arguments("consumed_list_argument")]
    [Arguments("consumed_string_list_copied_release")]
    [Arguments("heap_result_builtin")]
    [Arguments("heap_result_let")]
    [Arguments("helper_call_without_inline_trigger")]
    [Arguments("inlined_entry_helper_under_back_edge")]
    [Arguments("inlined_helper_chain_under_back_edge")]
    [Arguments("inlined_helper_sibling_by_label")]
    [Arguments("inlined_helper_sibling_spliced")]
    [Arguments("match_arm_copy_out")]
    [Arguments("match_fresh_scrutinee_head_returned")]
    [Arguments("match_fresh_scrutinee_owner_release")]
    [Arguments("match_rc_scrutinee")]
    [Arguments("non_tail_self_call_list_result")]
    [Arguments("owned_let_list_drop")]
    [Arguments("producer_conses_record_string_head")]
    [Arguments("reuse_path_rebuild_declines_copy")]
    [Arguments("pattern_head_read_under_operator")]
    [Arguments("record_pattern")]
    [Arguments("reuse_list_map")]
    [Arguments("reuse_record_update")]
    [Arguments("reuse_shared_falls_back")]
    [Arguments("self_call_operand_string_result")]
    [Arguments("tag_group_arm_brackets")]
    [Arguments("tco_consumed_list_parameter_borrowed_head")]
    [Arguments("tco_consumed_list_parameter_returned_head")]
    [Arguments("tco_consumed_record_list_tuple_result")]
    [Arguments("tco_list_walk")]
    [Arguments("tco_non_tail_self_call_in_operator_operand")]
    [Arguments("tco_owned_child_record_accumulator")]
    [Arguments("tco_record_field_read_into_successor")]
    [Arguments("tco_record_head_consed_into_sibling_accumulator")]
    [Arguments("tco_record_head_stored_into_copy_adt_successor")]
    [Arguments("tco_record_parameter_exit_before_list_accumulator")]
    [Arguments("tco_record_string_field_into_successor")]
    [Arguments("tco_returned_record_head")]
    [Arguments("tco_scalar_loop")]
    [Arguments("tco_scalar_owned_let")]
    [Arguments("tco_str_parameter_fresh_successor")]
    [Arguments("tco_tuple_parameter_rebuild")]
    [Arguments("tco_unused_chain_parameter")]
    [Arguments("tco_variant_parameter_reused_in_place")]
    [Arguments("unannotated_parameter_record")]
    [Arguments("parameter_reaches_result_record_update")]
    [Arguments("tco_list_parameter_resolved_by_back_edge")]
    public async Task Stage_zero_lowering_matches_shared_lowered_ir_fixture(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostIrParity");
        string source = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".source")).ConfigureAwait(false);
        string expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".ir")).ConfigureAwait(false);

        (_, IrProgram ir) = LowerFixture(fixtureName, source);

        IReadOnlyList<string> lines = IrTextFormatter.Format(ir, IrDumpStage.Lowered, filter: null);
        string actual = string.Join('\n', lines) + '\n';

        if (UpdateFixtures)
        {
            string outPath = Path.Combine(fixtureDirectory, fixtureName + ".ir");
            await File.WriteAllTextAsync(outPath, actual).ConfigureAwait(false);

            string repoFixtureDir = RepoParityDirectory("lowered-ir");
            if (Directory.Exists(repoFixtureDir))
            {
                await File.WriteAllTextAsync(Path.Combine(repoFixtureDir, fixtureName + ".ir"), actual).ConfigureAwait(false);
            }

            expected = actual;
        }

        actual.ShouldBe(expected);
    }

    // The explain fixtures are the same reporter and formatter the CLI's `--explain` prints, run on
    // the un-stitched lowering the lowered-ir fixtures come from, so the self-hosted explain parity
    // test compares against the report of the program it actually lowers rather than one with the
    // shipped standard library stitched in.
    [Test]
    [Arguments("simple_arith")]
    [Arguments("let_bindings")]
    [Arguments("nested_let_scopes")]
    [Arguments("scalar_match")]
    [Arguments("ownerless_match")]
    [Arguments("closure_capture")]
    [Arguments("pattern_match")]
    [Arguments("mutual_recursion")]
    [Arguments("heap_result_builtin")]
    [Arguments("heap_result_let")]
    [Arguments("heap_result_list")]
    [Arguments("record_pattern")]
    [Arguments("tag_group_arm_brackets")]
    [Arguments("match_arm_copy_out")]
    [Arguments("call_result_copy_out")]
    [Arguments("call_argument_retain")]
    [Arguments("consumed_list_argument")]
    [Arguments("match_rc_scrutinee")]
    [Arguments("match_list_scrutinee_drop")]
    [Arguments("aggregate_children_retain")]
    [Arguments("call_argument_retain")]
    [Arguments("call_result_copy_out")]
    [Arguments("consumed_list_argument")]
    [Arguments("consumed_string_list_copied_release")]
    [Arguments("heap_result_builtin")]
    [Arguments("heap_result_let")]
    [Arguments("helper_call_without_inline_trigger")]
    [Arguments("inlined_entry_helper_under_back_edge")]
    [Arguments("inlined_helper_chain_under_back_edge")]
    [Arguments("inlined_helper_sibling_by_label")]
    [Arguments("inlined_helper_sibling_spliced")]
    [Arguments("match_arm_copy_out")]
    [Arguments("match_fresh_scrutinee_head_returned")]
    [Arguments("match_fresh_scrutinee_owner_release")]
    [Arguments("match_rc_scrutinee")]
    [Arguments("non_tail_self_call_list_result")]
    [Arguments("owned_let_list_drop")]
    [Arguments("producer_conses_record_string_head")]
    [Arguments("reuse_path_rebuild_declines_copy")]
    [Arguments("pattern_head_read_under_operator")]
    [Arguments("record_pattern")]
    [Arguments("reuse_list_map")]
    [Arguments("reuse_record_update")]
    [Arguments("reuse_shared_falls_back")]
    [Arguments("self_call_operand_string_result")]
    [Arguments("tag_group_arm_brackets")]
    [Arguments("tco_consumed_list_parameter_borrowed_head")]
    [Arguments("tco_consumed_list_parameter_returned_head")]
    [Arguments("tco_consumed_record_list_tuple_result")]
    [Arguments("tco_list_walk")]
    [Arguments("tco_non_tail_self_call_in_operator_operand")]
    [Arguments("tco_owned_child_record_accumulator")]
    [Arguments("tco_record_field_read_into_successor")]
    [Arguments("tco_record_head_consed_into_sibling_accumulator")]
    [Arguments("tco_record_head_stored_into_copy_adt_successor")]
    [Arguments("tco_record_parameter_exit_before_list_accumulator")]
    [Arguments("tco_record_string_field_into_successor")]
    [Arguments("tco_returned_record_head")]
    [Arguments("tco_scalar_loop")]
    [Arguments("tco_scalar_owned_let")]
    [Arguments("tco_str_parameter_fresh_successor")]
    [Arguments("tco_tuple_parameter_rebuild")]
    [Arguments("tco_unused_chain_parameter")]
    [Arguments("tco_variant_parameter_reused_in_place")]
    [Arguments("unannotated_parameter_record")]
    [Arguments("tco_scalar_loop")]
    [Arguments("tco_scalar_owned_let")]
    [Arguments("tco_unused_chain_parameter")]
    [Arguments("owned_let_list_drop")]
    [Arguments("aggregate_children_retain")]
    public async Task Stage_zero_explain_reports_match_shared_explain_fixtures(string fixtureName)
    {
        string sourceDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostIrParity");
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostExplainParity");
        string source = await File.ReadAllTextAsync(Path.Combine(sourceDirectory, fixtureName + ".source")).ConfigureAwait(false);

        (Lowering lowering, IrProgram ir) = LowerFixture(fixtureName, source);
        IrProgram finalIr = IrOptimizer.Optimize(ir);
        CompilationDecisionSnapshot snapshot = lowering.GetDecisionSnapshot();

        foreach (ExplainKind kind in SharedExplainKinds)
        {
            var request = new ExplainRequest(new HashSet<ExplainKind> { kind });
            CompilationExplainReport report = IrExplainReporter.Build(snapshot, finalIr, request);
            string actual = string.Join('\n', ExplainReportFormatter.Format(report, request)) + '\n';

            string fixtureFile = $"{fixtureName}.{kind.ToString().ToLowerInvariant()}.txt";
            string expected;
            if (UpdateFixtures)
            {
                Directory.CreateDirectory(fixtureDirectory);
                await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, fixtureFile), actual).ConfigureAwait(false);

                string repoFixtureDir = RepoParityDirectory("explain");
                Directory.CreateDirectory(repoFixtureDir);
                await File.WriteAllTextAsync(Path.Combine(repoFixtureDir, fixtureFile), actual).ConfigureAwait(false);

                expected = actual;
            }
            else
            {
                expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureFile)).ConfigureAwait(false);
            }

            actual.ShouldBe(expected, fixtureFile);
        }
    }

    private static bool UpdateFixtures
        => string.Equals(Environment.GetEnvironmentVariable("ASHES_UPDATE_PARITY_FIXTURES"), "1", StringComparison.Ordinal);

    private static string RepoParityDirectory(string leaf)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "selfhost", "parity", "semantics", leaf));

    private static (Lowering Lowering, IrProgram Ir) LowerFixture(string fixtureName, string source)
    {
        var diagnostics = new Diagnostics();
        var parser = new Parser(source, diagnostics);
        var program = parser.ParseProgram();
        diagnostics.StructuredErrors.ShouldBeEmpty();

        var lowering = new Lowering(diagnostics);
        lowering.SetSourceContext(fixtureName + ".ash", source);
        IrProgram ir = lowering.Lower(program);
        diagnostics.StructuredErrors.ShouldBeEmpty();
        return (lowering, ir);
    }
}
