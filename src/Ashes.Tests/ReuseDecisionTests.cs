using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ReuseDecisionTests
{
    private const string EntryCopySource = """
        type Tree =
            | Leaf
            | Node(Tree, Int, Tree)

        let recursive grow count tree =
            if count <= 0
            then tree
            else
                match tree with
                    | Leaf -> grow(count - 1)(Node(Leaf)(1)(Leaf))
                    | Node(left, value, right) ->
                        grow(count - 1)(Node(left)(value + 1)(right))

        let recursive outer batch batches tree =
            if batch >= batches
            then tree
            else outer(batch + 1)(batches)(grow(2)(tree))

        let nested = outer(0)(3)(Node(Leaf)(0)(Leaf))

        let update amount =
            (let recursive go tree =
                match tree with
                    | Leaf -> Leaf
                    | Node(left, value, right) ->
                        Node(go(left))(value + amount)(go(right))
            in go)

        let recursive specializedOuter batch batches tree =
            if batch >= batches
            then tree
            else
                specializedOuter(batch + 1)(batches)(update(1)(tree))

        let specialized =
            specializedOuter(0)(3)(Node(Leaf)(0)(Leaf))

        let recursive bump count tree =
            if count <= 0
            then tree
            else
                match tree with
                    | Leaf -> bump(count - 1)(Node(Leaf)(1)(Leaf))
                    | Node(left, value, right) ->
                        bump(count - 1)(Node(left)(value + 100)(right))

        let base = bump(1)(Leaf)
        let keep = base
        let updated = bump(2)(base)
        in (nested, specialized, keep, updated)
        """;

    private const string CandidateRejectionSource = """
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

        let recursive find target items =
            match items with
                | [] -> -1
                | Item(value) :: rest ->
                    if value == target then value else find(target)(rest)

        let shared = make(2)
        let keep = shared
        let rejectedUnique = increment(1)(shared)
        let rejectedShape = find(2)(make(3))
        in (keep, rejectedUnique, rejectedShape)
        """;

    [Test]
    public void GeneratedSpecialization_RetainsSourceCandidateAndResetSafetyDecision()
    {
        const string source = """
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

        Lowering lowering = LowerProgram(source, "reuse-decisions.ash");
        IReadOnlyList<ReuseDecision> decisions = lowering.ReuseDecisions
            .Where(decision =>
                decision.Decision is ReuseDecisionKind.SpecializationGeneration
                    or ReuseDecisionKind.ResetSafetyQualification)
            .ToList();

        decisions.Count.ShouldBe(2, "the cached specialization should be described once");
        ReuseDecision generated = decisions[0];
        generated.Decision.ShouldBe(ReuseDecisionKind.SpecializationGeneration);
        generated.Mechanism.ShouldBe(ReuseDecisionMechanism.Specialization);
        generated.Outcome.ShouldBe(ReuseDecisionOutcome.Generated);
        generated.Reason.ShouldBe(ReuseDecisionReason.SpecializableCall);
        generated.Candidate.ShouldNotBeNull();
        generated.Candidate.Kind.ShouldBe(ReuseCandidateKind.Parameter);
        generated.Candidate.SourceName.ShouldBe("items");
        generated.RelatedGeneratedLabel.ShouldBeNull();
        generated.Function.Kind.ShouldBe(IrFunctionOriginKind.ReuseSpecialization);
        generated.Function.Source.ShouldNotBeNull();
        generated.Function.Source.SourceName.ShouldBe("increment");
        generated.Location.ShouldNotBeNull();
        generated.Location.Value.FilePath.ShouldBe("reuse-decisions.ash");

        ReuseDecision qualified = decisions[1];
        qualified.Decision.ShouldBe(ReuseDecisionKind.ResetSafetyQualification);
        qualified.Outcome.ShouldBe(ReuseDecisionOutcome.Accepted);
        qualified.Reason.ShouldBe(ReuseDecisionReason.NoResetInvalidatingAllocation);
        qualified.Candidate.ShouldNotBeNull();
        qualified.Candidate.SourceName.ShouldBe("items");
        qualified.RelatedGeneratedLabel.ShouldNotBeNull();
        qualified.Function.ShouldBe(generated.Function);
        qualified.Location.ShouldBe(generated.Location);
    }

    [Test]
    public void ResetSafetyRejection_RetainsTheConcreteAllocationReasonAndLocation()
    {
        const string source = """
            type Item =
                | value: Str

            let recursive make count =
                if count <= 0
                then []
                else Item(value = "item") :: make(count - 1)

            let recursive append suffix items =
                match items with
                    | [] -> []
                    | Item(value) :: rest ->
                        Item(value = value + suffix) :: append(suffix)(rest)

            append("!", make(2))
            """;

        Lowering lowering = LowerProgram(source, "reuse-rejection.ash");
        ReuseDecision rejection = lowering.ReuseDecisions.Single(
            decision => decision.Decision == ReuseDecisionKind.ResetSafetyQualification);

        rejection.Outcome.ShouldBe(ReuseDecisionOutcome.Rejected);
        rejection.Reason.ShouldBe(ReuseDecisionReason.StringConcatenationAllocation);
        rejection.Candidate.ShouldNotBeNull();
        rejection.Candidate.SourceName.ShouldBe("items");
        rejection.Function.Source.ShouldNotBeNull();
        rejection.Function.Source.SourceName.ShouldBe("append");
        rejection.RelatedGeneratedLabel.ShouldNotBeNull();
        rejection.Location.ShouldNotBeNull();
        rejection.Location.Value.FilePath.ShouldBe("reuse-rejection.ash");
    }

    [Test]
    public void EntryCopies_RetainFinalDirectAndSpecializationOutcomes()
    {
        IReadOnlyList<ReuseDecision> decisions =
            LowerProgram(EntryCopySource, "entry-copy.ash").ReuseDecisions
                .Where(decision => decision.Decision == ReuseDecisionKind.EntryCopy)
                .ToList();

        ReuseDecision grow = EntryCopy(decisions, "grow");
        grow.Mechanism.ShouldBe(ReuseDecisionMechanism.DirectInPlace);
        grow.Outcome.ShouldBe(ReuseDecisionOutcome.Elided);
        grow.Reason.ShouldBe(ReuseDecisionReason.OwnershipMoveSafe);
        grow.MoveSafetyCauses.ShouldBe(ParameterMoveSafetyCause.None);

        ReuseDecision outer = EntryCopy(decisions, "specializedOuter");
        outer.Mechanism.ShouldBe(ReuseDecisionMechanism.Specialization);
        outer.Outcome.ShouldBe(ReuseDecisionOutcome.Elided);
        outer.Reason.ShouldBe(ReuseDecisionReason.OwnershipMoveSafe);

        ReuseDecision bump = EntryCopy(decisions, "bump");
        bump.Mechanism.ShouldBe(ReuseDecisionMechanism.DirectInPlace);
        bump.Outcome.ShouldBe(ReuseDecisionOutcome.Retained);
        bump.Reason.ShouldBe(ReuseDecisionReason.OwnershipMoveSafetyRejected);
        bump.MoveSafetyCauses.ShouldNotBe(ParameterMoveSafetyCause.None);

        foreach (ReuseDecision decision in new[] { grow, outer, bump })
        {
            decision.Candidate.ShouldNotBeNull();
            decision.Candidate.Kind.ShouldBe(ReuseCandidateKind.Parameter);
            decision.Candidate.SourceName.ShouldBe("tree");
            decision.Candidate.LocalSlot.ShouldNotBeNull();
            decision.Location.ShouldNotBeNull();
            decision.Location.Value.FilePath.ShouldBe("entry-copy.ash");
        }
    }

    [Test]
    public void DirectReader_OmitsEntryCopyWhenNoStructuralReuseSurvives()
    {
        const string source = """
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive find target tree =
                match tree with
                    | Leaf -> -1
                    | Node(left, value, _) ->
                        if value == target then value else find(target)(left)

            find(2)(Node(Node(Leaf)(1)(Leaf))(2)(Leaf))
            """;

        ReuseDecision decision = LowerProgram(source, "reader-copy.ash")
            .ReuseDecisions.Single(
                candidate => candidate.Decision == ReuseDecisionKind.EntryCopy);

        decision.Mechanism.ShouldBe(ReuseDecisionMechanism.DirectInPlace);
        decision.Outcome.ShouldBe(ReuseDecisionOutcome.Omitted);
        decision.Reason.ShouldBe(ReuseDecisionReason.NoStructuralReuse);
        decision.Candidate.ShouldNotBeNull();
        decision.Candidate.SourceName.ShouldBe("tree");
    }

    [Test]
    public void ReuseTokens_DistinguishRuntimeChecksFromStaticUniqueness()
    {
        const string runtimeSource = """
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Right(value + 1)
                | Right(value) -> Left(value - 1)
            """;
        IReadOnlyList<ReuseDecision> runtimeDecisions =
            LowerProgram(runtimeSource, "runtime-token.ash").ReuseDecisions
                .Where(decision =>
                    decision.Decision == ReuseDecisionKind.RuntimeUniquenessCheck)
                .ToList();

        runtimeDecisions.Count.ShouldBe(2);
        foreach (ReuseDecision decision in runtimeDecisions)
        {
            decision.Mechanism.ShouldBe(ReuseDecisionMechanism.ReuseToken);
            decision.Outcome.ShouldBe(ReuseDecisionOutcome.Required);
            decision.Reason.ShouldBe(
                ReuseDecisionReason.RuntimeManagedReuseCandidate);
            decision.Candidate.ShouldNotBeNull();
            decision.Candidate.Kind.ShouldBe(ReuseCandidateKind.Value);
            decision.Candidate.SourceName.ShouldBe("choice");
            decision.Candidate.Temp.ShouldNotBeNull();
            decision.Location.ShouldNotBeNull();
            decision.Location.Value.FilePath.ShouldBe("runtime-token.ash");
        }

        const string staticSource = """
            let recursive bumpAll values =
                match values with
                    | [] -> []
                    | value :: rest -> value + 1 :: bumpAll(rest)

            let recursive repeat turns values =
                if turns <= 0
                then values
                else repeat(turns - 1)(bumpAll(values))

            repeat(3)([1, 2, 3])
            """;
        ReuseDecision staticDecision = LowerProgram(
            staticSource,
            "static-token.ash").ReuseDecisions.Single(
                decision =>
                    decision.Decision == ReuseDecisionKind.RuntimeUniquenessCheck);

        staticDecision.Mechanism.ShouldBe(ReuseDecisionMechanism.ReuseToken);
        staticDecision.Outcome.ShouldBe(ReuseDecisionOutcome.Omitted);
        staticDecision.Reason.ShouldBe(
            ReuseDecisionReason.StaticallyUniqueReuseCandidate);
        staticDecision.Candidate.ShouldNotBeNull();
        staticDecision.Candidate.SourceName.ShouldBe("values");
        staticDecision.Location.ShouldNotBeNull();
        staticDecision.Location.Value.FilePath.ShouldBe("static-token.ash");
    }

    [Test]
    public void ReuseTokenLifecycle_CorrelatesProductionWithOneTerminalDisposition()
    {
        const string source = """
            let recursive bumpAll values =
                match values with
                    | [] -> []
                    | value :: rest -> value + 1 :: bumpAll(rest)

            let recursive repeat turns values =
                if turns <= 0
                then values
                else repeat(turns - 1)(bumpAll(values))

            repeat(3)([1, 2, 3])
            """;

        IReadOnlyList<ReuseDecision> decisions =
            LowerProgram(source, "token-lifecycle.ash").ReuseDecisions;
        IReadOnlyList<ReuseDecision> productions = decisions
            .Where(decision =>
                decision.Decision == ReuseDecisionKind.TokenProduction)
            .ToList();
        productions.ShouldNotBeEmpty();

        foreach (ReuseDecision production in productions)
        {
            production.Outcome.ShouldBe(ReuseDecisionOutcome.Produced);
            production.Reason.ShouldBe(ReuseDecisionReason.MatchedCellBecameDead);
            production.TokenLifecycle.ShouldNotBeNull();
            ReuseTokenLifecycle productionLifecycle = production.TokenLifecycle;
            productionLifecycle.TokenTemp.ShouldNotBeNull();
            productionLifecycle.SourceValueTemp.ShouldNotBeNull();
            productionLifecycle.FieldCount.ShouldBe(2);
            productionLifecycle.ListCell.ShouldBe(true);
            productionLifecycle.RuntimeManaged.ShouldBe(false);
            production.Location.ShouldNotBeNull();
            production.Location.Value.FilePath.ShouldBe("token-lifecycle.ash");

            IReadOnlyList<ReuseDecision> dispositions = decisions
                .Where(decision =>
                    decision.Decision == ReuseDecisionKind.TokenDisposition
                    && decision.Function == production.Function
                    && decision.TokenLifecycle?.TokenTemp
                        == productionLifecycle.TokenTemp)
                .ToList();
            dispositions.Count.ShouldBe(1);
            dispositions[0].Outcome.ShouldBe(ReuseDecisionOutcome.Consumed);
            dispositions[0].Reason.ShouldBe(
                ReuseDecisionReason.CompatibleTokenConsumed);
            dispositions[0].TokenLifecycle.ShouldNotBeNull();
            ReuseTokenLifecycle dispositionLifecycle =
                dispositions[0].TokenLifecycle!;
            dispositionLifecycle.AllocationTemp.ShouldNotBeNull();
            dispositionLifecycle.TargetConstructor.ShouldBe("::");
        }
    }

    [Test]
    public void RuntimeReuseFallback_CorrelatesNullTokenPathWithConsumption()
    {
        const string source = """
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Right(value + 1)
                | Right(value) -> Left(value - 1)
            """;

        IReadOnlyList<ReuseDecision> decisions =
            LowerProgram(source, "runtime-fallback.ash").ReuseDecisions;
        IReadOnlyList<ReuseDecision> consumptions = decisions
            .Where(decision =>
                decision.Decision == ReuseDecisionKind.TokenDisposition
                && decision.Outcome == ReuseDecisionOutcome.Consumed)
            .ToList();
        consumptions.Count.ShouldBe(2);

        foreach (ReuseDecision consumption in consumptions)
        {
            consumption.TokenLifecycle.ShouldNotBeNull();
            ReuseTokenLifecycle consumptionLifecycle =
                consumption.TokenLifecycle;
            consumptionLifecycle.RuntimeManaged.ShouldBe(true);
            ReuseDecision fallback = decisions.Single(decision =>
                decision.Decision == ReuseDecisionKind.FallbackAllocation
                && decision.Outcome == ReuseDecisionOutcome.Available
                && decision.Function == consumption.Function
                && decision.TokenLifecycle?.TokenTemp
                    == consumptionLifecycle.TokenTemp
                && decision.TokenLifecycle?.AllocationTemp
                    == consumptionLifecycle.AllocationTemp);
            fallback.Reason.ShouldBe(
                ReuseDecisionReason.RuntimeUniquenessFallback);
            fallback.TokenLifecycle.ShouldNotBeNull();
            fallback.TokenLifecycle.FallbackKind.ShouldBe(
                ReuseFallbackAllocationKind.RuntimeRc);
        }
    }

    [Test]
    public void ReuseSpecialization_RetainsPersistentToSpaceFallback()
    {
        const string source = """
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

            increment(1)(make(2))
            """;

        ReuseDecision fallback = LowerProgram(
            source,
            "to-space-fallback.ash").ReuseDecisions.Single(decision =>
                decision.Decision == ReuseDecisionKind.FallbackAllocation
                && decision.TokenLifecycle?.FallbackKind
                    == ReuseFallbackAllocationKind.ToSpace);

        fallback.Outcome.ShouldBe(ReuseDecisionOutcome.Allocated);
        fallback.Reason.ShouldBe(ReuseDecisionReason.NoCompatibleReuseToken);
        fallback.Function.Kind.ShouldBe(IrFunctionOriginKind.ClosureHelper);
        fallback.Function.Source.ShouldNotBeNull();
        fallback.Function.Source.SourceName.ShouldBe("increment");
        fallback.TokenLifecycle.ShouldNotBeNull();
        fallback.TokenLifecycle.TargetConstructor.ShouldBe("Item");
        fallback.TokenLifecycle.AllocationTemp.ShouldNotBeNull();
        fallback.Location.ShouldNotBeNull();
        fallback.Location.Value.FilePath.ShouldBe("to-space-fallback.ash");
    }

    [Test]
    public void DirectReader_RetainsFinalReversionToFreshAllocation()
    {
        const string source = """
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            type MaybeInt =
                | None
                | Some(Int)

            let recursive find target tree =
                match tree with
                    | Leaf -> None
                    | Node(left, value, _) ->
                        if value == target
                        then Some(value)
                        else find(target)(left)

            find(2)(Node(Node(Leaf)(1)(Leaf))(2)(Leaf))
            """;

        IReadOnlyList<ReuseDecision> decisions =
            LowerProgram(source, "nullary-reversion.ash").ReuseDecisions;
        IReadOnlyList<ReuseDecision> reverted = decisions
            .Where(decision =>
                decision.Decision == ReuseDecisionKind.TokenDisposition
                && decision.Outcome == ReuseDecisionOutcome.Discarded
                && decision.Reason == ReuseDecisionReason.NoStructuralReuse)
            .ToList();
        reverted.ShouldNotBeEmpty(string.Join(
            "; ",
            decisions.Select(decision =>
                $"{decision.Decision}:{decision.Outcome}:{decision.Reason}:"
                + $"{decision.TokenLifecycle?.FieldCount}:"
                + $"{decision.TokenLifecycle?.TargetConstructor}")));
        foreach (ReuseDecision disposition in reverted)
        {
            disposition.TokenLifecycle.ShouldNotBeNull();
            ReuseDecision fallback = decisions.Single(decision =>
                decision.Decision == ReuseDecisionKind.FallbackAllocation
                && decision.Outcome == ReuseDecisionOutcome.Allocated
                && decision.Reason == ReuseDecisionReason.NoStructuralReuse
                && decision.Function == disposition.Function
                && decision.TokenLifecycle?.TokenTemp
                    == disposition.TokenLifecycle.TokenTemp
                && decision.TokenLifecycle?.AllocationTemp
                    == disposition.TokenLifecycle.AllocationTemp);
            fallback.TokenLifecycle.ShouldNotBeNull();
            fallback.TokenLifecycle.FallbackKind.ShouldBe(
                ReuseFallbackAllocationKind.Arena);
            decisions.Any(decision =>
                decision.Decision == ReuseDecisionKind.FallbackAllocation
                && decision.Outcome == ReuseDecisionOutcome.Available
                && decision.Function == disposition.Function
                && decision.TokenLifecycle?.AllocationTemp
                    == disposition.TokenLifecycle.AllocationTemp)
                .ShouldBeFalse();
        }
    }

    [Test]
    public void IncompatibleToken_RetainsFreshFallbackAndDiscard()
    {
        const string source = """
            type Shape =
                | One(Int)
                | Pair(Int, Int)

            let recursive rotate turns shape =
                if turns <= 0
                then shape
                else
                    match shape with
                        | One(value) -> rotate(turns - 1)(Pair(value)(0))
                        | Pair(left, _) -> rotate(turns - 1)(One(left))

            rotate(4)(One(1))
            """;

        IReadOnlyList<ReuseDecision> decisions =
            LowerProgram(source, "fallback-allocation.ash").ReuseDecisions;
        IReadOnlyList<ReuseDecision> fallbacks = decisions
            .Where(decision =>
                decision.Decision == ReuseDecisionKind.FallbackAllocation
                && decision.Outcome == ReuseDecisionOutcome.Allocated
                && decision.Reason
                    == ReuseDecisionReason.NoCompatibleReuseToken)
            .ToList();
        fallbacks.Count.ShouldBe(2);
        foreach (ReuseDecision fallback in fallbacks)
        {
            fallback.TokenLifecycle.ShouldNotBeNull();
            fallback.TokenLifecycle.TokenTemp.ShouldBeNull();
            fallback.TokenLifecycle.AllocationTemp.ShouldNotBeNull();
            fallback.TokenLifecycle.FallbackKind.ShouldBe(
                ReuseFallbackAllocationKind.Arena);
            fallback.Location.ShouldNotBeNull();
            fallback.Location.Value.FilePath.ShouldBe("fallback-allocation.ash");
        }

        decisions.Count(decision =>
            decision.Decision == ReuseDecisionKind.TokenDisposition
            && decision.Outcome == ReuseDecisionOutcome.Discarded
            && decision.Reason
                == ReuseDecisionReason.UnconsumedArenaTokenDiscarded)
            .ShouldBe(2);
    }

    [Test]
    public void RejectedSpecializationCandidates_RetainConcreteCallSiteReasons()
    {
        IReadOnlyList<ReuseDecision> rejections =
            LowerProgram(
                CandidateRejectionSource,
                "specialization-candidates.ash").ReuseDecisions
                .Where(decision =>
                    decision.Decision
                        == ReuseDecisionKind.SpecializationCandidateQualification)
                .ToList();
        rejections.Count.ShouldBe(
            2,
            string.Join(
                "; ",
                rejections.Select(decision =>
                    $"{decision.TargetFunction}:{decision.Candidate?.SourceName}:{decision.Reason}")));

        ReuseDecision unique = rejections.Single(decision =>
            string.Equals(
                decision.TargetFunction,
                "increment",
                StringComparison.Ordinal));
        unique.Outcome.ShouldBe(ReuseDecisionOutcome.Rejected);
        unique.Reason.ShouldBe(ReuseDecisionReason.AccumulatorNotProvenUnique);
        unique.Candidate.ShouldNotBeNull();
        unique.Candidate.Kind.ShouldBe(ReuseCandidateKind.Value);
        unique.Candidate.SourceName.ShouldBe("shared");
        unique.Candidate.LocalSlot.ShouldNotBeNull();

        ReuseDecision shape = rejections.Single(decision =>
            string.Equals(
                decision.TargetFunction,
                "find",
                StringComparison.Ordinal));
        shape.Reason.ShouldBe(ReuseDecisionReason.ResultDoesNotRebuildAccumulator);
        shape.Candidate.ShouldNotBeNull();
        shape.Candidate.SourceName.ShouldBe("make");
        shape.Candidate.LocalSlot.ShouldBeNull();

        foreach (ReuseDecision decision in new[] { unique, shape })
        {
            decision.Mechanism.ShouldBe(ReuseDecisionMechanism.Specialization);
            decision.Function.Kind.ShouldBe(IrFunctionOriginKind.ProgramEntry);
            decision.Location.ShouldNotBeNull();
            decision.Location.Value.FilePath.ShouldBe(
                "specialization-candidates.ash");
        }
    }

    [Test]
    public void ConstructorLayouts_RetainCompatibleAndFieldCountDecisions()
    {
        const string source = """
            type Shape =
                | Zero
                | One(Int)
                | Other(Int)
                | Pair(Int, Int)

            let recursive rotate turns shape =
                if turns <= 0
                then shape
                else
                    match shape with
                        | Zero -> rotate(turns - 1)(Zero)
                        | One(value) -> rotate(turns - 1)(Other(value))
                        | Other(value) -> rotate(turns - 1)(Pair(value)(0))
                        | Pair(left, _) -> rotate(turns - 1)(One(left))

            rotate(4)(One(1))
            """;

        IReadOnlyList<ReuseDecision> decisions =
            LowerProgram(source, "constructor-layout.ash").ReuseDecisions
                .Where(decision =>
                    decision.Decision
                        == ReuseDecisionKind.ConstructorLayoutCompatibility)
                .ToList();

        decisions.Count.ShouldBe(4);
        decisions.Count(decision =>
            decision.Outcome == ReuseDecisionOutcome.Accepted).ShouldBe(2);
        decisions.Count(decision =>
            decision.Reason
                == ReuseDecisionReason.ConstructorFieldCountMismatch).ShouldBe(2);
        foreach (ReuseDecision decision in decisions)
        {
            decision.Candidate.ShouldNotBeNull();
            decision.Candidate.Kind.ShouldBe(ReuseCandidateKind.Token);
            decision.Candidate.SourceName.ShouldBe("shape");
            decision.Candidate.Temp.ShouldNotBeNull();
            decision.Layout.ShouldNotBeNull();
            decision.Layout.TargetConstructor.ShouldNotBeNullOrWhiteSpace();
            decision.Location.ShouldNotBeNull();
            decision.Location.Value.FilePath.ShouldBe("constructor-layout.ash");
        }

        ReuseDecision compatible = decisions.Single(decision =>
            string.Equals(
                decision.Layout?.TargetConstructor,
                "Other",
                StringComparison.Ordinal));
        compatible.Reason.ShouldBe(
            ReuseDecisionReason.CompatibleConstructorLayout);
        compatible.Layout.ShouldNotBeNull();
        compatible.Layout.ProducedFieldCount.ShouldBe(1);
        compatible.Layout.RequestedFieldCount.ShouldBe(1);
    }

    [Test]
    public void ConstructorLayouts_RetainCellKindMismatch()
    {
        const string cellKindSource = """
            type Pair =
                | Pair(Int, Int)

            type Item =
                | value: Int

            let recursive make count =
                if count <= 0
                then []
                else Item(value = count) :: make(count - 1)

            let recursive pairAll values =
                match values with
                    | [] -> []
                    | Item(value) :: rest ->
                        Pair(value)(value) :: pairAll(rest)

            pairAll(make(2))
            """;
        IReadOnlyList<ReuseDecision> cellKindDecisions =
            LowerProgram(cellKindSource, "cell-kind-layout.ash").ReuseDecisions;
        ReuseDecision cellKind = LayoutDecision(
            cellKindDecisions,
            ReuseDecisionReason.ConstructorCellKindMismatch);
        cellKind.Layout.ShouldNotBeNull();
        cellKind.Layout.ProducedFieldCount.ShouldBe(2);
        cellKind.Layout.RequestedFieldCount.ShouldBe(2);
        cellKind.Layout.ProducedListCell.ShouldBeTrue();
        cellKind.Layout.RequestedListCell.ShouldBeFalse();
    }

    [Test]
    public void ConstructorLayouts_RetainRuntimeRegimeMismatch()
    {
        const string runtimeSource = """
            type Choice =
                | Left(Int, Int)
                | Right(Int, Int)

            type Callback =
                | Callback(Int -> Int, Int)
                | NoCallback

            let choice = Left(1)(2)
            match choice with
                | Left(left, right) ->
                    let callback =
                        Callback(given value -> value + left)(right)
                    in Right(left)(right)
                | Right(left, right) ->
                    let callback =
                        Callback(given value -> value + left)(right)
                    in Left(left)(right)
            """;
        IReadOnlyList<ReuseDecision> runtimeDecisions = LowerProgram(
            runtimeSource,
            "runtime-layout.ash").ReuseDecisions.Where(decision =>
                decision.Reason
                    == ReuseDecisionReason.RuntimeManagedTokenNotAllowed)
                .ToList();
        runtimeDecisions.Count.ShouldBe(2);
        foreach (ReuseDecision runtime in runtimeDecisions)
        {
            runtime.Outcome.ShouldBe(ReuseDecisionOutcome.Rejected);
            runtime.Layout.ShouldNotBeNull();
            runtime.Layout.RuntimeManagedToken.ShouldBeTrue();
            runtime.Layout.RuntimeManagedAllowed.ShouldBeFalse();
            runtime.Layout.TargetConstructor.ShouldBe("Callback");
        }
    }

    private static ReuseDecision EntryCopy(
        IReadOnlyList<ReuseDecision> decisions,
        string sourceFunction)
    {
        return decisions.Single(decision =>
            string.Equals(
                decision.Function.Source?.SourceName,
                sourceFunction,
                StringComparison.Ordinal));
    }

    private static ReuseDecision LayoutDecision(
        IReadOnlyList<ReuseDecision> decisions,
        ReuseDecisionReason reason)
    {
        IReadOnlyList<ReuseDecision> matches = decisions
            .Where(decision =>
                decision.Decision
                    == ReuseDecisionKind.ConstructorLayoutCompatibility
                && decision.Reason == reason)
            .ToList();
        matches.Count.ShouldBe(
            1,
            string.Join(
                "; ",
                decisions
                    .Select(decision =>
                        $"{decision.Decision}:{decision.Reason}:"
                        + $"{decision.Layout?.TargetConstructor}:"
                        + $"{decision.Layout?.ProducedFieldCount}->"
                        + $"{decision.Layout?.RequestedFieldCount}:"
                        + $"{decision.Layout?.ProducedListCell}->"
                        + $"{decision.Layout?.RequestedListCell}")));
        return matches[0];
    }

    private static Lowering LowerProgram(string source, string filePath)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext(filePath, source);
        _ = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return lowering;
    }
}
