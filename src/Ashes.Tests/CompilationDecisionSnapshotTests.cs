using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// The compiler-decision handoff: what lowering decided, readable by a consumer that reports on
/// compilation rather than participating in it.
/// </summary>
public sealed class CompilationDecisionSnapshotTests
{
    // Rebuilds a list of single-field records in place, which is the shape the reuse pass takes its
    // specialization, uniqueness, layout, and token decisions on.
    private const string ReuseProgram = """
        type Item =
            | value: Int

        let recursive make count =
            if count <= 0
            then []
            else Item(value = count) :: make(count - 1)

        let recursive increment amount items =
            match items with
                | [] -> []
                | Item(value) :: rest ->
                    Item(value = value + amount) :: increment(amount)(rest)

        let first = increment(1)(make(2))
        let second = increment(2)(make(3))
        in (first, second)
        """;

    // Builds owned strings through a recursive accumulator, which is where ordinary values reach
    // reference counting rather than staying region-backed.
    private const string PlacementProgram = """
        let recursive build n acc =
            if n <= 0
            then acc
            else build(n - 1)(acc + "x")

        Ashes.IO.print(Ashes.Text.byteLength(build(3)("")))
        """;

    // Extracts references out of a recursive function's list parameter, which is what gives pattern
    // bindings their own ownership classification and placement outcome.
    private const string PatternProgram = """
        type Found =
            | items: List(Str)
            | number: Int

        let recursive find entries =
            match entries with
                | [] -> None
                | (items, number) :: tail ->
                    if number == 0
                    then Some(Found(items = items, number = number))
                    else find(tail)

        find([(["answer"], 0)])
        """;

    [Test]
    public void Records_are_retrievable_without_console_interception_or_an_environment_variable()
    {
        // Reading the snapshot is an ordinary call: no console interception, no environment variable.
        Snapshot(ReuseProgram).FunctionOwnership.ShouldNotBeEmpty();
        Snapshot(ReuseProgram).ReuseDecisions.ShouldNotBeEmpty();
        Snapshot(PlacementProgram).ValuePlacements.ShouldNotBeEmpty();
        Snapshot(PatternProgram).PatternBindings.ShouldNotBeEmpty();
    }

    [Test]
    public void Every_record_maps_to_a_reportable_origin()
    {
        CompilationDecisionSnapshot snapshot = Merge(ReuseProgram, PlacementProgram, PatternProgram);

        snapshot.FunctionOwnership.ShouldAllBe(record => record.Origin != null);
        snapshot.ValuePlacements.ShouldAllBe(record => record.Function != null);
        snapshot.ReuseDecisions.ShouldAllBe(decision => decision.Function != null);
        snapshot.CoroutineRepresentations.ShouldAllBe(record => record.Function != null);
        snapshot.PatternBindings.ShouldAllBe(record => record.Function != null);
    }

    [Test]
    public void A_site_specific_record_carries_its_source_location()
    {
        CompilationDecisionSnapshot patterns = Snapshot(PatternProgram);
        patterns.PatternBindings.ShouldNotBeEmpty();
        patterns.PatternBindings.ShouldAllBe(record => record.Location != null);
        Snapshot(PlacementProgram).ValuePlacements.ShouldContain(record => record.Location != null);
    }

    [Test]
    public void Ordinals_impose_a_total_order_independent_of_collection_order()
    {
        foreach (string program in new[] { ReuseProgram, PlacementProgram, PatternProgram })
        {
            CompilationDecisionSnapshot snapshot = Snapshot(program);
            OrdinalsAreDense(snapshot.FunctionOwnership.Select(record => record.Ordinal));
            OrdinalsAreDense(snapshot.ValuePlacements.Select(record => record.Ordinal));
            OrdinalsAreDense(snapshot.PatternBindings.Select(record => record.Ordinal));
        }
    }

    [Test]
    public void Two_compilations_of_one_program_produce_the_same_records()
    {
        // Deterministic iteration is part of the contract: a report that reorders between runs is not
        // usable as a diff.
        CompilationDecisionSnapshot first = Snapshot(ReuseProgram);
        CompilationDecisionSnapshot second = Snapshot(ReuseProgram);

        Describe(first).ShouldBe(Describe(second));
    }

    [Test]
    public void Reading_the_snapshot_does_not_change_the_compiled_program()
    {
        // Observational only: the records are appended during lowering and read afterwards, so a
        // consumer that asks for them gets the same IR as one that never does.
        (Lowering read, IrProgram readIr) = Lower(ReuseProgram);
        _ = read.GetDecisionSnapshot();
        _ = read.GetDecisionSnapshot();
        (_, IrProgram untouched) = Lower(ReuseProgram);

        DescribeIr(IrOptimizer.Optimize(readIr)).ShouldBe(DescribeIr(IrOptimizer.Optimize(untouched)));
    }

    [Test]
    public void Generated_functions_retain_their_source_lineage_through_the_optimizer()
    {
        (Lowering lowering, IrProgram ir) = Lower(ReuseProgram);
        IrProgram optimized = IrOptimizer.Optimize(ir);

        IReadOnlyList<IrFunction> generated = [.. optimized.Functions.Where(function =>
            function.Origin is { Kind: not IrFunctionOriginKind.SourceFunction })];

        generated.ShouldNotBeEmpty();
        // A generated function names either the source function it came from or, when no single
        // source function owns it, an explicit compiler owner. Neither may be absent.
        generated.Select(function => function.Origin)
            .ShouldAllBe(origin => origin!.Source != null || origin.CompilerOwner != null);

        CompilationDecisionSnapshot snapshot = lowering.GetDecisionSnapshot();
        foreach (IrFunction function in optimized.Functions)
        {
            snapshot.PlacementsIn(function.Label)
                .ShouldAllBe(record => record.Function!.GeneratedLabel == function.Label);
        }
    }

    [Test]
    public void Ownership_is_indexed_by_origin_rather_than_by_name()
    {
        // Two local functions can share a name; the origin is what separates them, so the lookup is
        // by origin and a name collision must not merge two functions' records.
        CompilationDecisionSnapshot snapshot = Snapshot("""
            let outer flag =
                let helper value = value + "-a"
                in helper(flag)

            let inner flag =
                let helper value = value + "-bb"
                in helper(flag)

            Ashes.IO.print(outer("x") + inner("y"))
            """);

        IReadOnlyList<FunctionOwnershipRecord> helpers = [.. snapshot.FunctionOwnership
            .Where(record => string.Equals(record.Function, "helper", StringComparison.Ordinal))];

        helpers.Count.ShouldBeGreaterThanOrEqualTo(2);
        helpers.Select(record => record.Origin).Distinct().Count().ShouldBe(helpers.Count);
        foreach (FunctionOwnershipRecord helper in helpers)
        {
            snapshot.OwnershipFor(helper.Origin).ShouldContain(helper);
        }
    }

    [Test]
    public void Placements_report_the_categories_that_exist_after_the_migration()
    {
        CompilationDecisionSnapshot snapshot = Snapshot(PlacementProgram);

        IReadOnlySet<ValuePlacementCategory> observed =
            snapshot.ValuePlacements.Select(record => record.Placement).ToHashSet();

        observed.ShouldContain(ValuePlacementCategory.RuntimeRc);
        observed.ShouldContain(ValuePlacementCategory.Region);
        observed.ShouldContain(ValuePlacementCategory.CopyValue);
        // Conservative-unknown is one of the categories the contract asks for rather than a hole:
        // a value the migration has not narrowed is reported as such, not omitted.
        observed.ShouldContain(ValuePlacementCategory.ConservativeUnknown);
        snapshot.ValuePlacements
            .Where(record => record.Placement != ValuePlacementCategory.ConservativeUnknown)
            .ShouldAllBe(record => record.DropKind != LoweredTempDropKind.Unknown
                || record.Placement == ValuePlacementCategory.CopyValue);
    }

    [Test]
    public void Reuse_decisions_come_from_their_own_decision_sites()
    {
        CompilationDecisionSnapshot snapshot = Snapshot(ReuseProgram);

        snapshot.ReuseDecisions.ShouldNotBeEmpty();
        // Each decision names the site it was taken at, and its reason is a stable code rather than
        // prose: the snapshot carries no formatted text anywhere.
        snapshot.ReuseDecisions.ShouldAllBe(decision => decision.Location != null || decision.Candidate != null);
        snapshot.ReuseDecisions.Select(decision => decision.Reason).Distinct().ShouldNotBeEmpty();
    }

    private static void OrdinalsAreDense(IEnumerable<int> ordinals)
        => ordinals.ShouldBe(Enumerable.Range(0, ordinals.Count()));

    /// <summary>
    /// The snapshots of several programs, concatenated. Assertions that hold for every record of a
    /// kind read better over one collection than repeated per program.
    /// </summary>
    private static CompilationDecisionSnapshot Merge(params string[] programs)
    {
        IReadOnlyList<CompilationDecisionSnapshot> snapshots = [.. programs.Select(Snapshot)];
        return new CompilationDecisionSnapshot(
            [.. snapshots.SelectMany(snapshot => snapshot.FunctionOwnership)],
            [.. snapshots.SelectMany(snapshot => snapshot.ValuePlacements)],
            [.. snapshots.SelectMany(snapshot => snapshot.ReuseDecisions)],
            [.. snapshots.SelectMany(snapshot => snapshot.CoroutineRepresentations)],
            [.. snapshots.SelectMany(snapshot => snapshot.PatternBindings)]);
    }

    private static IReadOnlyList<string> Describe(CompilationDecisionSnapshot snapshot) =>
    [
        .. snapshot.FunctionOwnership.Select(record => $"own {record.Ordinal} {record.Function}"),
        .. snapshot.ValuePlacements.Select(record =>
            $"place {record.Ordinal} {record.Function?.GeneratedLabel} {record.Temp} {record.Placement} {record.Reason}"),
        .. snapshot.ReuseDecisions.Select(decision =>
            $"reuse {decision.Function.GeneratedLabel} {decision.Decision} {decision.Outcome} {decision.Reason}"),
        .. snapshot.PatternBindings.Select(record =>
            $"pattern {record.Ordinal} {record.BindingName} {record.Ownership} {record.PlacementOutcome}"),
    ];

    private static IReadOnlyList<string> DescribeIr(IrProgram ir) =>
    [
        .. ir.Functions.Select(function =>
            $"{function.Label} {function.Instructions.Count} {function.LocalCount} {function.TempCount}"),
        $"entry {ir.EntryFunction.Instructions.Count}",
    ];

    private static CompilationDecisionSnapshot Snapshot(string source)
        => Lower(source).Lowering.GetDecisionSnapshot();

    private static (Lowering Lowering, IrProgram Ir) Lower(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("snapshot.ash", source);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return (lowering, ir);
    }
}
