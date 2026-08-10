using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// What the reports say, as opposed to how the CLI carries them. These build the report directly so a
/// claim about a decision is checked against the decision rather than against printed text.
/// </summary>
public sealed class ExplainReportTests
{
    [Test]
    public void An_unknown_kind_is_rejected_and_a_known_one_parses()
    {
        ExplainRequest.TryParseValue("rc", out var kind, out var filter, out var error).ShouldBeTrue();
        kind.ShouldBe(ExplainKind.Rc);
        filter.ShouldBeNull();
        error.ShouldBeNull();

        ExplainRequest.TryParseValue("foo", out _, out _, out var failure).ShouldBeFalse();
        failure.ShouldBe("Unknown explain type 'foo'.");
    }

    [Test]
    public void A_selector_is_parsed_off_the_kind()
    {
        ExplainRequest.TryParseValue("memory:Map.set", out var kind, out var filter, out _).ShouldBeTrue();
        kind.ShouldBe(ExplainKind.Memory);
        filter.ShouldBe("Map.set");

        // A trailing colon with nothing after it is no filter rather than a filter matching nothing.
        ExplainRequest.TryParseValue("memory:", out _, out var empty, out _).ShouldBeTrue();
        empty.ShouldBeNull();
    }

    [Test]
    public void Kinds_are_case_insensitive()
    {
        ExplainRequest.TryParseValue("OwNeRsHiP", out var kind, out _, out _).ShouldBeTrue();
        kind.ShouldBe(ExplainKind.Ownership);
    }

    [Test]
    public void Traits_kind_parses_and_reports_stable_dictionary_evidence()
    {
        ExplainRequest.TryParseValue("traits", out ExplainKind kind, out string? filter, out string? error)
            .ShouldBeTrue();
        kind.ShouldBe(ExplainKind.Traits);
        filter.ShouldBeNull();
        error.ShouldBeNull();

        const string source = """
            trait Render(a) =
                | render : a -> Str

            implement Render(Int) =
                | render = given (value) -> Ashes.Text.fromInt(value)

            let show : a -> Str requires {Render(a)} =
                given (value) -> Render.render(value)

            show(42)
            """;
        var request = new ExplainRequest(new HashSet<ExplainKind> { ExplainKind.Traits });
        CompilationExplainReport report = Report(source, request);
        TraitDictionaryAbiAnnotation parameter = report.TraitEvidence.DictionaryParameters
            .Single(candidate => string.Equals(candidate.Function, "show", StringComparison.Ordinal));
        parameter.ParameterIndex.ShouldBe(0);
        parameter.Trait.ShouldBe("Render");
        parameter.Methods.ShouldBe(["render"]);
        report.TraitEvidence.ResolvedImplementations
            .ShouldContain(candidate => string.Equals(candidate.Requirement, "Render(Int)", StringComparison.Ordinal));

        IReadOnlyList<string> first = ExplainReportFormatter.Format(report, request);
        IReadOnlyList<string> second = ExplainReportFormatter.Format(Report(source, request), request);
        first.ShouldBe(second);
        string text = string.Join('\n', first);
        text.ShouldContain("Trait evidence report");
        text.ShouldContain("dictionary parameter 0: Render");
        text.ShouldContain("Resolved: Render(Int)");
        text.ShouldNotContain("0x");
    }

    [Test]
    public void Reported_parameter_ownership_mirrors_the_analysis_partition()
    {
        // Borrowed and consumed partition the parameter list, and the report derives one from the
        // other rather than carrying its own opinion. This checks the derivation against the source
        // lists instead of asserting a classification the analysis is free to change.
        const string source = """
            let measure value = Ashes.Text.byteLength(value)
            let pick flag left right = if flag then left else right
            Ashes.IO.print(measure(pick(true)("a")("b")))
            """;

        (Lowering lowering, IrProgram ir) = Lower(source);
        CompilationDecisionSnapshot snapshot = lowering.GetDecisionSnapshot();
        CompilationExplainReport report = IrExplainReporter.Build(
            snapshot,
            IrOptimizer.Optimize(ir),
            new ExplainRequest(new HashSet<ExplainKind> { ExplainKind.Ownership }));

        report.Ownership.ShouldNotBeEmpty();
        foreach (OwnershipFunctionReport function in report.Ownership)
        {
            FunctionOwnershipRecord record = snapshot.FunctionOwnership
                .First(candidate => candidate.Origin == function.Origin);

            // Order and membership both preserved: a report that reorders parameters is unusable.
            function.Parameters.Select(parameter => parameter.Name).ShouldBe(record.Parameters);
            foreach (OwnershipParameterReport parameter in function.Parameters)
            {
                bool borrowed = record.BorrowedParameters.Contains(parameter.Name, StringComparer.Ordinal);
                parameter.Ownership.ShouldBe(borrowed ? ParameterOwnership.Borrowed : ParameterOwnership.Consumed);
                // Move-safe is exactly "the callee takes it", so it tracks consumed and nothing else.
                parameter.MoveSafe.ShouldBe(!borrowed);
            }
        }
    }

    [Test]
    public void A_transferred_parameter_is_reported_as_consumed_and_move_safe()
    {
        // `value` becomes part of the result, so ownership moves into the callee.
        CompilationExplainReport report = Report("""
            let wrap value = value + "!"
            Ashes.IO.print(wrap("abc"))
            """, ExplainKind.Ownership, "wrap");

        OwnershipParameterReport parameter = report.Ownership
            .Single(function => function.Function.Contains("wrap", StringComparison.Ordinal))
            .Parameters.Single();
        parameter.Ownership.ShouldBe(ParameterOwnership.Consumed);
        parameter.MoveSafe.ShouldBeTrue();
    }

    [Test]
    public void A_result_that_reaches_a_parameter_reports_it_as_an_alias()
    {
        CompilationExplainReport report = Report("""
            let choose flag left right = if flag then left else right
            Ashes.IO.print(choose(true)("a")("b"))
            """, ExplainKind.Ownership, "choose");

        OwnershipFunctionReport choose = report.Ownership
            .Single(function => function.Function.Contains("choose", StringComparison.Ordinal));
        choose.ResultAliases.ShouldContain("left");
        choose.ResultAliases.ShouldContain("right");
        // Aliases are listed in parameter order so two runs cannot disagree.
        choose.ResultAliases.ShouldBe(choose.Parameters
            .Select(parameter => parameter.Name)
            .Where(choose.ResultAliases.Contains)
            .ToList());
    }

    [Test]
    public void Rc_counts_describe_the_ir_it_was_handed_not_the_one_lowering_emitted()
    {
        // The optimizer removes both of this program's drops. Reporting the freshly lowered IR would
        // therefore describe operations that never reach the backend, which is the whole reason the
        // report observes after optimization.
        const string source = """
            type Item =
                | value: Int

            let recursive bump items =
                match items with
                    | [] -> []
                    | Item(value) :: rest -> Item(value = value + 1) :: bump(rest)

            let rebuilt = bump([Item(value = 1), Item(value = 2)])
            in Ashes.IO.print(2)
            """;

        (Lowering lowering, IrProgram raw) = Lower(source);
        var request = new ExplainRequest(new HashSet<ExplainKind> { ExplainKind.Rc });
        CompilationDecisionSnapshot snapshot = lowering.GetDecisionSnapshot();

        int rawDrops = IrExplainReporter.Build(snapshot, raw, request).Rc.Sum(function => function.Drops);
        int optimizedDrops = IrExplainReporter.Build(snapshot, IrOptimizer.Optimize(raw), request)
            .Rc.Sum(function => function.Drops);

        rawDrops.ShouldBeGreaterThan(optimizedDrops);
        optimizedDrops.ShouldBe(0);
    }

    [Test]
    public void Rc_omits_functions_with_no_operations()
    {
        CompilationExplainReport report = Report("""
            let addOne n = n + 1
            Ashes.IO.print(addOne(1))
            """, ExplainKind.Rc, "addOne");

        // A copy-typed helper allocates and releases nothing, so it is noise in a report about
        // reference counting and is left out rather than printed as a row of zeroes.
        report.Rc.ShouldBeEmpty();
    }

    [Test]
    public void Reuse_reports_decisions_with_their_reason()
    {
        CompilationExplainReport report = Report("""
            type Item =
                | value: Int

            let recursive make count =
                if count <= 0 then []
                else Item(value = count) :: make(count - 1)

            let recursive increment amount items =
                match items with
                    | [] -> []
                    | Item(value) :: rest ->
                        Item(value = value + amount) :: increment(amount)(rest)

            let first = increment(1)(make(2))
            in Ashes.IO.print(2)
            """, ExplainKind.Reuse, filter: null);

        report.Reuse.ShouldNotBeEmpty();
        report.Reuse.ShouldAllBe(decision => decision.Origin != null);
        // Every decision carries a stable code rather than prose the semantics had to compose.
        report.Reuse.Select(decision => decision.Reason).Distinct().ShouldNotBeEmpty();
    }

    [Test]
    public void Memory_correlates_the_other_three_without_recomputing_them()
    {
        const string source = """
            let build n = Ashes.Text.fromInt(n) + "-tail"
            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + Ashes.Text.byteLength(build(n)))
            Ashes.IO.print(loop(3)(0))
            """;

        CompilationExplainReport memory = Report(source, ExplainKind.Memory, filter: null);
        CompilationExplainReport separate = Report(source, ExplainKind.Ownership, filter: null);

        // The memory report is the same ownership data, not a second derivation of it.
        memory.Ownership.Count.ShouldBe(separate.Ownership.Count);
        memory.Representation.ShouldNotBeEmpty();
        memory.Rc.ShouldNotBeEmpty();
    }

    [Test]
    public void Representation_reports_the_categories_a_value_can_receive()
    {
        CompilationExplainReport report = Report("""
            let build n = Ashes.Text.fromInt(n) + "-tail"
            Ashes.IO.print(Ashes.Text.byteLength(build(3)))
            """, ExplainKind.Memory, filter: null);

        IReadOnlySet<ValuePlacementCategory> observed = report.Representation
            .SelectMany(function => function.Placements.Keys)
            .ToHashSet();

        observed.ShouldContain(ValuePlacementCategory.RuntimeRc);
        observed.ShouldContain(ValuePlacementCategory.CopyValue);
    }

    [Test]
    public void A_selector_reaches_a_generated_function_through_its_source_function()
    {
        // Reuse specializations, droppers and coroutines are named for the compiler, not the user, so
        // a selector naming the source function has to find what was generated for it.
        CompilationExplainReport report = Report("""
            let build n = Ashes.Text.fromInt(n) + "-tail"
            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + Ashes.Text.byteLength(build(n) + "!"))
            Ashes.IO.print(loop(3)(0))
            """, ExplainKind.Rc, "loop");

        report.Rc.ShouldNotBeEmpty();
        report.Rc.ShouldAllBe(function =>
            function.Label.Contains("loop", StringComparison.OrdinalIgnoreCase)
                || function.Origin!.Source!.SourceName.Contains("loop", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void An_empty_request_produces_no_report()
    {
        (Lowering lowering, IrProgram ir) = Lower("Ashes.IO.print(1)");

        CompilationExplainReport report = IrExplainReporter.Build(
            lowering.GetDecisionSnapshot(),
            ir,
            ExplainRequest.None);

        report.Ownership.ShouldBeEmpty();
        report.Rc.ShouldBeEmpty();
        report.Reuse.ShouldBeEmpty();
        report.Representation.ShouldBeEmpty();
    }

    [Test]
    public void Formatting_is_stable_across_repeated_builds()
    {
        const string source = """
            let build n = Ashes.Text.fromInt(n) + "-tail"
            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + Ashes.Text.byteLength(build(n)))
            Ashes.IO.print(loop(3)(0))
            """;

        var request = new ExplainRequest(new HashSet<ExplainKind> { ExplainKind.Memory });
        IReadOnlyList<string> first = ExplainReportFormatter.Format(Report(source, request), request);
        IReadOnlyList<string> second = ExplainReportFormatter.Format(Report(source, request), request);

        first.ShouldBe(second);
        first.ShouldNotBeEmpty();
    }

    [Test]
    public void Formatted_output_carries_no_nondeterministic_content()
    {
        var request = new ExplainRequest(new HashSet<ExplainKind>
        {
            ExplainKind.Ownership,
            ExplainKind.Rc,
            ExplainKind.Reuse,
            ExplainKind.Traits,
            ExplainKind.Memory,
        });
        IReadOnlyList<string> lines = ExplainReportFormatter.Format(
            Report("""
                let build n = Ashes.Text.fromInt(n) + "-tail"
                Ashes.IO.print(Ashes.Text.byteLength(build(3)))
                """, request),
            request);

        lines.ShouldNotBeEmpty();
        // No addresses and no ANSI colour: a report that changes between runs cannot be diffed, and
        // one carrying escape codes cannot be redirected to a file.
        lines.ShouldAllBe(line => !line.Contains("0x", StringComparison.Ordinal));
        lines.ShouldAllBe(line => !line.Contains(''));
    }

    private static CompilationExplainReport Report(string source, ExplainKind kind, string? filter)
        => Report(source, new ExplainRequest(new HashSet<ExplainKind> { kind }, filter));

    private static CompilationExplainReport Report(string source, ExplainRequest request)
    {
        (Lowering lowering, IrProgram ir) = Lower(source);
        return IrExplainReporter.Build(lowering.GetDecisionSnapshot(), IrOptimizer.Optimize(ir), request);
    }

    private static (Lowering Lowering, IrProgram Ir) Lower(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("explain.ash", source);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return (lowering, ir);
    }
}
