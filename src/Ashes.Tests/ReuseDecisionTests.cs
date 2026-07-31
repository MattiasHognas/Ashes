using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ReuseDecisionTests
{
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
        IReadOnlyList<ReuseDecision> decisions = lowering.ReuseDecisions;

        decisions.Count.ShouldBe(2, "the cached specialization should be described once");
        ReuseDecision generated = decisions[0];
        generated.Decision.ShouldBe(ReuseDecisionKind.SpecializationGeneration);
        generated.Outcome.ShouldBe(ReuseDecisionOutcome.Generated);
        generated.Reason.ShouldBe(ReuseDecisionReason.SpecializableCall);
        generated.CandidateParameter.ShouldBe("items");
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
        qualified.CandidateParameter.ShouldBe("items");
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
        rejection.CandidateParameter.ShouldBe("items");
        rejection.Function.Source.ShouldNotBeNull();
        rejection.Function.Source.SourceName.ShouldBe("append");
        rejection.RelatedGeneratedLabel.ShouldNotBeNull();
        rejection.Location.ShouldNotBeNull();
        rejection.Location.Value.FilePath.ShouldBe("reuse-rejection.ash");
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
