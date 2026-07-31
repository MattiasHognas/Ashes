using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class PatternBindingOwnershipTests
{
    [Test]
    public void Plain_call_borrows_extracted_value_and_self_call_transfers_tail_to_same_parameter()
    {
        Lowering lowering = LowerProgram("""
            let describe value = value

            let recursive walk stop values =
                match values with
                    | [] -> "missing"
                    | value :: tail ->
                        if stop
                        then describe(value)
                        else walk(true)(tail)

            walk(false)(["a", "b"])
            """, "plain-call.ash");

        IReadOnlyList<PatternBindingOwnershipFact> facts = OwnershipFacts(lowering, "walk");
        PatternBindingOwnershipFact value = facts.Single(fact =>
            string.Equals(fact.BindingName, "value", StringComparison.Ordinal));
        value.Ownership.ShouldBe(PatternBindingOwnershipKind.BorrowedOnly);
        (value.Uses & PatternBindingOwnershipUse.OrdinaryCallBorrow)
            .ShouldBe(PatternBindingOwnershipUse.OrdinaryCallBorrow);
        value.RootParameterOrdinal.ShouldBe(1);
        value.RootParameterName.ShouldBe("values");
        value.Location.ShouldNotBeNull();
        value.Location.Value.FilePath.ShouldBe("plain-call.ash");

        PatternBindingOwnershipFact tail = facts.Single(fact =>
            string.Equals(fact.BindingName, "tail", StringComparison.Ordinal));
        tail.Ownership.ShouldBe(PatternBindingOwnershipKind.TransferredToSameParameter);
        (tail.Uses & PatternBindingOwnershipUse.SameParameterTransfer)
            .ShouldBe(PatternBindingOwnershipUse.SameParameterTransfer);

        PatternBindingOwnershipDecision valueDecision = Decision(lowering, "walk", "value");
        valueDecision.ShadowComparison.ShouldBe(PatternBindingShadowComparison.Agrees);
        valueDecision.PlacementOutcome.ShouldBe(PatternBindingPlacementOutcome.LegacyEscapeRejected);
        Decision(lowering, "walk", "tail").ShadowComparison
            .ShouldBe(PatternBindingShadowComparison.Agrees);
    }

    [Test]
    public void Structural_inspection_exposes_legacy_overprotection_in_shadow_comparison()
    {
        Lowering lowering = LowerProgram("""
            let recursive contains target values =
                match values with
                    | [] -> false
                    | value :: tail ->
                        if value == target
                        then true
                        else contains(target)(tail)

            contains("b")(["a", "b"])
            """, "inspection-shadow.ash");

        PatternBindingOwnershipFact value = OwnershipFacts(lowering, "contains")
            .Single(fact => string.Equals(fact.BindingName, "value", StringComparison.Ordinal));
        value.Ownership.ShouldBe(PatternBindingOwnershipKind.BorrowedOnly);
        (value.Uses & PatternBindingOwnershipUse.StructuralInspection)
            .ShouldBe(PatternBindingOwnershipUse.StructuralInspection);

        PatternBindingOwnershipDecision decision = Decision(lowering, "contains", "value");
        decision.LegacyRequiresProtectiveDup.ShouldBeTrue();
        decision.ShadowComparison.ShouldBe(PatternBindingShadowComparison.LegacyMoreConservative);
        decision.PlacementOutcome.ShouldBe(PatternBindingPlacementOutcome.RootNotRuntimeManaged);
    }

    [Test]
    public void Nested_list_and_tuple_extractions_retain_lineage_and_classify_embedding()
    {
        Lowering lowering = LowerProgram("""
            type Found =
                | text: Str
                | number: Int

            let recursive find entries =
                match entries with
                    | [] -> None
                    | (text, number) :: tail ->
                        if number == 0
                        then Some(Found(text = text, number = number))
                        else find(tail)

            find([("answer", 0)])
            """, "nested-pattern.ash");

        IReadOnlyList<PatternBindingOwnershipFact> facts = OwnershipFacts(lowering, "find");
        PatternBindingOwnershipFact text = facts.Single(fact =>
            string.Equals(fact.BindingName, "text", StringComparison.Ordinal));
        text.Ownership.ShouldBe(PatternBindingOwnershipKind.EmbeddedInOwner);
        text.ExtractionDepth.ShouldBe(2);
        text.RootParameterOrdinal.ShouldBe(0);
        text.Location.ShouldNotBeNull();

        PatternBindingOwnershipFact number = facts.Single(fact =>
            string.Equals(fact.BindingName, "number", StringComparison.Ordinal));
        number.Ownership.ShouldBe(PatternBindingOwnershipKind.EmbeddedInOwner);
        number.ExtractionDepth.ShouldBe(2);

        PatternBindingOwnershipFact tail = facts.Single(fact =>
            string.Equals(fact.BindingName, "tail", StringComparison.Ordinal));
        tail.Ownership.ShouldBe(PatternBindingOwnershipKind.TransferredToSameParameter);
        tail.ExtractionDepth.ShouldBe(1);

        PatternBindingOwnershipDecision textDecision = Decision(lowering, "find", "text");
        textDecision.ShadowComparison.ShouldBe(PatternBindingShadowComparison.Agrees);
        textDecision.PlacementOutcome.ShouldBe(PatternBindingPlacementOutcome.ProtectiveDupInserted);

        PatternBindingOwnershipDecision numberDecision = Decision(lowering, "find", "number");
        numberDecision.ShadowComparison.ShouldBe(PatternBindingShadowComparison.Agrees);
        numberDecision.PlacementOutcome.ShouldBe(PatternBindingPlacementOutcome.CopyType);
    }

    [Test]
    public void Nested_match_extractions_link_to_their_parent_binding()
    {
        Lowering lowering = LowerProgram("""
            type Entry =
                | Entry((Str, Int))

            let recursive first entries =
                match entries with
                    | [] -> "missing"
                    | entry :: tail ->
                        match entry with
                            | Entry(pair) ->
                                match pair with
                                    | (text, _) -> text

            first([Entry(("answer", 0))])
            """, "nested-match.ash");

        IReadOnlyList<PatternBindingOwnershipFact> facts = OwnershipFacts(lowering, "first");
        PatternBindingOwnershipFact entry = facts.Single(fact =>
            string.Equals(fact.BindingName, "entry", StringComparison.Ordinal));
        PatternBindingOwnershipFact pair = facts.Single(fact =>
            string.Equals(fact.BindingName, "pair", StringComparison.Ordinal));
        PatternBindingOwnershipFact text = facts.Single(fact =>
            string.Equals(fact.BindingName, "text", StringComparison.Ordinal));

        entry.ParentBindingOrdinal.ShouldBeNull();
        entry.ExtractionDepth.ShouldBe(1);
        pair.ParentBindingOrdinal.ShouldBe(entry.BindingOrdinal);
        pair.ExtractionDepth.ShouldBe(2);
        text.ParentBindingOrdinal.ShouldBe(pair.BindingOrdinal);
        text.ExtractionDepth.ShouldBe(3);
        text.Ownership.ShouldBe(PatternBindingOwnershipKind.EscapesIndependently);
    }

    [Test]
    public void Same_named_binders_in_disjoint_arms_keep_distinct_classifications_and_slots()
    {
        Lowering lowering = LowerProgram("""
            type Box =
                | value: Str

            let recursive choose keep values =
                match values with
                    | [] -> None
                    | value :: tail when keep -> Some(Box(value = value))
                    | value :: tail -> choose(keep)(tail)

            choose(true)(["a", "b"])
            """, "disjoint-arms.ash");

        IReadOnlyList<PatternBindingOwnershipFact> values = OwnershipFacts(lowering, "choose")
            .Where(fact => string.Equals(fact.BindingName, "value", StringComparison.Ordinal))
            .ToList();
        values.Count.ShouldBe(2);
        values.Select(fact => fact.BindingOrdinal).Distinct().Count().ShouldBe(2);
        values.Select(fact => fact.Location).Distinct().Count().ShouldBe(2);
        values.ShouldContain(fact => fact.Ownership == PatternBindingOwnershipKind.EmbeddedInOwner);
        values.ShouldContain(fact => fact.Ownership == PatternBindingOwnershipKind.BorrowedOnly);

        IReadOnlyList<PatternBindingOwnershipDecision> decisions = lowering.PatternBindingOwnershipDecisions
            .Where(decision => string.Equals(
                    decision.Function?.SourceName,
                    "choose",
                    StringComparison.Ordinal)
                && string.Equals(decision.BindingName, "value", StringComparison.Ordinal))
            .ToList();
        decisions.Count.ShouldBe(2);
        decisions.Select(decision => decision.LocalSlot).Distinct().Count().ShouldBe(2);
        decisions.ShouldContain(decision =>
            decision.PlacementOutcome == PatternBindingPlacementOutcome.ProtectiveDupInserted);
        decisions.ShouldContain(decision =>
            decision.PlacementOutcome == PatternBindingPlacementOutcome.LegacyEscapeRejected);
    }

    [Test]
    public void Closure_capture_is_an_independent_escape()
    {
        Lowering lowering = LowerProgram("""
            let recursive select keep values =
                match values with
                    | [] -> given _ -> "missing"
                    | value :: tail ->
                        if keep
                        then given _ -> value
                        else select(true)(tail)

            select(false)(["answer"])(0)
            """, "capture.ash");

        PatternBindingOwnershipFact value = OwnershipFacts(lowering, "select")
            .Single(fact => string.Equals(fact.BindingName, "value", StringComparison.Ordinal));
        value.Ownership.ShouldBe(PatternBindingOwnershipKind.EscapesIndependently);
        (value.Uses & PatternBindingOwnershipUse.CapturedByClosure)
            .ShouldBe(PatternBindingOwnershipUse.CapturedByClosure);
        value.RequiresProtectiveDup.ShouldBeTrue();
    }

    private static IReadOnlyList<PatternBindingOwnershipFact> OwnershipFacts(
        Lowering lowering,
        string function)
    {
        return lowering.GetOwnershipSummaries(function)
            .OrderByDescending(summary => summary.Parameters.Count)
            .First()
            .PatternBindingOwnership;
    }

    private static PatternBindingOwnershipDecision Decision(
        Lowering lowering,
        string function,
        string binding)
    {
        return lowering.PatternBindingOwnershipDecisions.Single(decision =>
            string.Equals(decision.Function?.SourceName, function, StringComparison.Ordinal)
                && string.Equals(decision.BindingName, binding, StringComparison.Ordinal));
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
