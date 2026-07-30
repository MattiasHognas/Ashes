using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class OwnershipConservativeCauseTests
{
    [Test]
    public void CollidingLocalFunctions_RetainIndependentEscapeCensuses()
    {
        const string source = """
            type Box =
                | Empty
                | Full(Box)
            let apply f value = f(value)
            let first seed =
                let target value = value
                in apply(target)(seed)
            let second seed =
                let target value = value
                in target(seed)
            in (first(Full(Empty)), second(Full(Empty)))
            """;

        IReadOnlyList<FunctionOwnershipSummary> targets =
            LowerProgram(source).GetOwnershipSummaries("target");

        targets.Count.ShouldBe(2);
        // Same-named local bindings intentionally have no distinct source-qualified names.
        // Stable declaration offsets preserve their source order.
        FunctionOwnershipSummary escaped = targets[0];
        FunctionOwnershipSummary direct = targets[1];

        escaped.CallCensus.Causes.HasFlag(FunctionCallCensusCause.EscapedAsValue).ShouldBeTrue();
        escaped.ParameterMoveSafety["value"].Causes
            .HasFlag(ParameterMoveSafetyCause.FunctionEscaped).ShouldBeTrue();
        direct.CallCensus.Complete.ShouldBeTrue();
        direct.ParameterMoveSafety["value"].IsMoveSafe.ShouldBeTrue();
        direct.UniqueParameters.ShouldContain("value");
    }

    [Test]
    public void PartialApplication_RetainsTheIncompleteCensusCause()
    {
        const string source = """
            type Box =
                | Empty
                | Full(Box)
            let target left right = left
            in target(Full(Empty))
            """;

        FunctionOwnershipSummary summary = Summary(LowerProgram(source), "target");

        summary.CallCensus.Complete.ShouldBeFalse();
        summary.CallCensus.DirectCallCount.ShouldBe(0);
        summary.CallCensus.Causes
            .HasFlag(FunctionCallCensusCause.IncompleteApplication).ShouldBeTrue();
        summary.ParameterMoveSafety["left"].Causes
            .HasFlag(ParameterMoveSafetyCause.IncompleteCallCensus).ShouldBeTrue();
        summary.ParameterMoveSafety["left"].Causes
            .HasFlag(ParameterMoveSafetyCause.NoDirectCallSites).ShouldBeTrue();
    }

    [Test]
    public void MoveSafetyFailure_DistinguishesLinearityCaptureAndTransitivity()
    {
        const string source = """
            type Box =
                | Empty
                | Full(Box)
            let nonlinearTarget value = value
            let nonlinearCaller seed = (nonlinearTarget(seed), seed)
            let capturedTarget value = value
            let capturedCaller seed =
                let hold = given ignored -> seed
                in (capturedTarget(seed), hold(0))
            let transitiveTarget value = value
            let relay seed = transitiveTarget(seed)
            let shared = Full(Empty)
            in (nonlinearCaller(Full(Empty)), capturedCaller(Full(Empty)), relay(shared), shared)
            """;

        Lowering lowering = LowerProgram(source);

        MoveSafetyCauses(lowering, "nonlinearTarget")
            .ShouldBe(ParameterMoveSafetyCause.MoveLinearity);
        MoveSafetyCauses(lowering, "capturedTarget")
            .ShouldBe(ParameterMoveSafetyCause.CapturedByClosure);
        MoveSafetyCauses(lowering, "transitiveTarget")
            .ShouldBe(ParameterMoveSafetyCause.TransitiveParameterUnsafe);
    }

    [Test]
    public void MoveSafetyFailure_DetectsCaptureInsideARecursiveGroup()
    {
        const string source = """
            type Box =
                | Empty
                | Full(Box)
            let seed = Full(Empty)
            let recursive first count =
                if count <= 0
                then given ignored -> seed
                else second(count - 1)
            and second count = first(count - 1)
            let target value = value
            in (target(seed), first(1)(0))
            """;

        MoveSafetyCauses(LowerProgram(source), "target")
            .ShouldBe(ParameterMoveSafetyCause.CapturedByClosure);
    }

    [Test]
    public void UnsafeConstructorSeed_IsReportedWithoutChangingFailClosedResult()
    {
        const string source = """
            type Choice =
                | First
                | Second
            let target value = value
            in target(First)
            """;

        FunctionOwnershipSummary summary = Summary(LowerProgram(source), "target");

        summary.ParameterMoveSafety["value"].IsMoveSafe.ShouldBeFalse();
        summary.ParameterMoveSafety["value"].Causes
            .HasFlag(ParameterMoveSafetyCause.SeedNotSafe).ShouldBeTrue();
        summary.UniqueParameters.ShouldNotContain("value");
    }

    [Test]
    public void ResultReachCauses_DistinguishGlobalUnmodelledAndInternalSharing()
    {
        const string source = """
            type Box =
                | Empty
                | Full(Box)
            type Pair =
                | Pair(Box, Box)
            let global = Full(Empty)
            let reachesGlobal ignored = global
            let unmodelled f value = f(value)
            let shared value = Pair(value)(value)
            in (reachesGlobal(0), unmodelled(given item -> item)(Empty), shared(Empty))
            """;

        Lowering lowering = LowerProgram(source);

        Summary(lowering, "reachesGlobal").ResultReachFacts.Causes
            .HasFlag(ResultReachCause.GlobalOrTopLevelReach).ShouldBeTrue();
        Summary(lowering, "unmodelled").ResultReachFacts.Causes
            .HasFlag(ResultReachCause.UnmodelledReach).ShouldBeTrue();
        FunctionOwnershipSummary shared = Summary(lowering, "shared");
        shared.ResultReachFacts.Causes.HasFlag(ResultReachCause.InternalSharing).ShouldBeTrue();
        shared.ResultPoisoned.ShouldBeTrue();
        shared.ResultReaches("value").ShouldBeTrue();
    }

    [Test]
    public void ResultReachCauseFlags_PropagateAndUnionAcrossCalls()
    {
        const string source = """
            type Box =
                | Empty
                | Full(Box)
            type Pair =
                | Pair(Box, Box)
            let global = Full(Empty)
            let mixed value = (global, Pair(value)(value))
            let forward value = mixed(value)
            in forward(Empty)
            """;

        FunctionOwnershipSummary summary = Summary(LowerProgram(source), "forward");

        summary.ResultReachFacts.Causes.HasFlag(ResultReachCause.GlobalOrTopLevelReach).ShouldBeTrue();
        summary.ResultReachFacts.Causes.HasFlag(ResultReachCause.InternalSharing).ShouldBeTrue();
        summary.ResultPoisoned.ShouldBeTrue();
        summary.ResultReaches("value").ShouldBeTrue();
    }

    [Test]
    public void RuntimeManagedCallResult_RetainsTheProvenanceFactItConsumed()
    {
        const string source = """
            type Box =
                | Full(Int)
            let make value = Full(value)
            let forward value = make(value)
            in forward(1)
            """;

        OwnershipFactConsumption decision = LowerProgram(source).OwnershipFactConsumptions.Single(
            consumption =>
                string.Equals(consumption.Function.SourceName, "make", StringComparison.Ordinal)
                && consumption.Decision == OwnershipDecisionKind.RuntimeManagedCallResult);

        decision.EvaluatedFacts.ShouldBe(
            OwnershipDecisionFact.ResultProvenance
                | OwnershipDecisionFact.RuntimeManageableResultType);
        decision.PositiveFacts.ShouldBe(decision.EvaluatedFacts);
        decision.Parameter.ShouldBeNull();
        decision.Outcome.ShouldBeTrue();
    }

    [Test]
    public void RuntimeManagedCallResult_RetainsAConcreteLayoutRejection()
    {
        const string source = """
            let make ignored = [given value -> value]
            let forward ignored = make(ignored)
            in forward(0)
            """;

        OwnershipFactConsumption decision = LowerProgram(source).OwnershipFactConsumptions.Single(
            consumption =>
                string.Equals(consumption.Function.SourceName, "make", StringComparison.Ordinal)
                && consumption.Decision == OwnershipDecisionKind.RuntimeManagedCallResult);

        decision.EvaluatedFacts.ShouldBe(
            OwnershipDecisionFact.ResultProvenance
                | OwnershipDecisionFact.RuntimeManageableResultType);
        decision.PositiveFacts.ShouldBe(OwnershipDecisionFact.ResultProvenance);
        decision.Outcome.ShouldBeFalse();
    }

    [Test]
    public void ReuseCopyElision_RetainsTheMoveSafetyFactItConsumed()
    {
        const string source = """
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive grow n tree =
                if n <= 0
                then tree
                else
                    match tree with
                        | Leaf -> grow(n - 1)(Node(Leaf)(1)(Leaf))
                        | Node(left, value, right) ->
                            grow(n - 1)(Node(left)(value + 1)(right))

            let recursive outer batch batches tree =
                if batch >= batches
                then tree
                else outer(batch + 1)(batches)(grow(3)(tree))

            let nested = outer(0)(4)(Node(Leaf)(0)(Leaf))

            let recursive bump n tree =
                if n <= 0
                then tree
                else
                    match tree with
                        | Leaf -> bump(n - 1)(Node(Leaf)(1)(Leaf))
                        | Node(left, value, right) ->
                            bump(n - 1)(Node(left)(value + 100)(right))

            let base = bump(1)(Leaf)
            let keep = base
            let bumped = bump(2)(base)
            in (nested, keep, bumped)
            """;

        IReadOnlyList<OwnershipFactConsumption> decisions = LowerProgram(source).OwnershipFactConsumptions;
        OwnershipFactConsumption elided = decisions.Single(
            consumption =>
                string.Equals(consumption.Function.SourceName, "grow", StringComparison.Ordinal)
                && consumption.Decision == OwnershipDecisionKind.ReuseEntryCopyElision);
        OwnershipFactConsumption retained = decisions.Single(
            consumption =>
                string.Equals(consumption.Function.SourceName, "bump", StringComparison.Ordinal)
                && consumption.Decision == OwnershipDecisionKind.ReuseEntryCopyElision);

        elided.EvaluatedFacts.ShouldBe(OwnershipDecisionFact.ParameterMoveSafety);
        elided.PositiveFacts.ShouldBe(OwnershipDecisionFact.ParameterMoveSafety);
        elided.Parameter.ShouldBe("tree");
        elided.Outcome.ShouldBeTrue();
        retained.EvaluatedFacts.ShouldBe(OwnershipDecisionFact.ParameterMoveSafety);
        retained.PositiveFacts.ShouldBe(OwnershipDecisionFact.None);
        retained.Parameter.ShouldBe("tree");
        retained.Outcome.ShouldBeFalse();
    }

    private static ParameterMoveSafetyCause MoveSafetyCauses(
        Lowering lowering,
        string function)
    {
        FunctionOwnershipSummary summary = Summary(lowering, function);
        return summary.ParameterMoveSafety[summary.Parameters[^1]].Causes;
    }

    private static FunctionOwnershipSummary Summary(Lowering lowering, string function)
    {
        return lowering.GetOwnershipSummary(function)
            ?? throw new InvalidOperationException($"Missing summary for '{function}'.");
    }

    private static Lowering LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        var lowering = new Lowering(diagnostics);
        lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return lowering;
    }
}
