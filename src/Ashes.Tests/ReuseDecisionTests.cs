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
