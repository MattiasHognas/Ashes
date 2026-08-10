using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class TraitResolutionTests
{
    [Test]
    public void ResolvesNestedConditionalImplementationsToAConcreteEvidenceTree()
    {
        const string source = """
            type Point =
                | Point(Int)

            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(Point) =
                | equal = given (left) -> given (right) -> true

            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true

            Eq.equal([[Point(1)]])([[Point(2)]])
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.LastTraitConstraints.ShouldBeEmpty();
        TraitEvidencePlan.Instance outer = lowering.ResolvedConcreteTraitEvidence
            .Single(plan => plan.Goal.TypeArgs.Single() is TypeRef.TList { Element: TypeRef.TList })
            .ShouldBeOfType<TraitEvidencePlan.Instance>();
        TraitEvidencePlan.Instance inner = outer.Requirements.Single()
            .ShouldBeOfType<TraitEvidencePlan.Instance>();
        inner.Requirements.Single().ShouldBeOfType<TraitEvidencePlan.Instance>()
            .Goal.TypeArgs.Single().ShouldBeOfType<TypeRef.TNamedType>()
            .Symbol.Name.ShouldBe("Point");
    }

    [Test]
    public void ReducesAStructuralAbstractGoalToItsRequiredParameterEvidence()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true

            let same : List(a) -> List(a) -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> Eq.equal(left)(right)

            same
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        TraitConstraint residual = lowering.LastTraitConstraints.Single();
        residual.Trait.Name.ShouldBe("Eq");
        residual.TypeArgs.Single().ShouldBeOfType<TypeRef.TVar>();
    }

    [Test]
    public void StrongerConstraintRemovesItsImpliedSupertrait()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            trait Ord(a) requires {Eq(a)} =
                | compare : a -> a -> Int

            let ordered : a -> a -> Int requires {Ord(a)} =
                given (left) -> given (right) ->
                    let ignored = Eq.equal(left)(right)
                    in Ord.compare(left)(right)

            ordered
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.LastTraitConstraints.Select(constraint => constraint.Trait.Name).ShouldBe(["Ord"]);
    }

    [Test]
    public void ResolvesSupertraitEvidenceFromTheSameCoherentRegistry()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            trait Ord(a) requires {Eq(a)} =
                | compare : a -> a -> Int
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> true
            implement Ord(Int) =
                | compare = given (left) -> given (right) -> 0
            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);
        TraitSymbol ord = lowering.TraitSymbols["Ord"];
        TraitEvidencePlan.Instance plan = lowering.ResolveTraitEvidence(
            new TraitConstraint(ord, [new TypeRef.TInt()]))
            .ShouldBeOfType<TraitEvidencePlan.Instance>();

        diagnostics.StructuredErrors.ShouldBeEmpty();
        plan.Supertraits.Single().ShouldBeOfType<TraitEvidencePlan.Instance>()
            .Goal.Trait.Name.ShouldBe("Eq");
    }

    [Test]
    public void ReportsMissingConcreteInstanceWithRequirementTrace()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true
            Eq.equal([1])([2])
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        DiagnosticEntry missing = diagnostics.StructuredErrors.Single(error =>
            error.Message.Contains("No implementation supplies", StringComparison.Ordinal));
        missing.Code.ShouldBe(DiagnosticCodes.TraitResolution);
        missing.Message.ShouldContain("Eq(List<Int>)");
        missing.Message.ShouldContain("Eq(Int)");
        missing.Message.ShouldContain("->");
    }

    [Test]
    public void RejectsNonDecreasingInstanceRequirementBeforeExpressionLowering()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            implement Eq(a) requires {Eq(List(a))} =
                | equal = given (left) -> given (right) -> true
            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        lowering.TraitInstances.ShouldBeEmpty();
        DiagnosticEntry termination = diagnostics.StructuredErrors.Single(error =>
            error.Message.Contains("structurally smaller", StringComparison.Ordinal));
        termination.Code.ShouldBe(DiagnosticCodes.TraitCycle);
        termination.Message.ShouldContain("Eq(List<a>)");
        termination.Message.ShouldContain("Eq(a)");
    }

    [Test]
    public void DepthLimitTerminatesADeepButDecreasingResolution()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> true
            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true
            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);
        TypeRef type = new TypeRef.TInt();
        for (int i = 0; i < 70; i++)
        {
            type = new TypeRef.TList(type);
        }

        TraitEvidencePlan? result = lowering.ResolveTraitEvidence(
            new TraitConstraint(lowering.TraitSymbols["Eq"], [type]));

        result.ShouldBeNull();
        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("maximum depth", StringComparison.Ordinal));
    }

    [Test]
    public void RuntimeCycleGuardReportsTheCompleteRequirementChain()
    {
        const string source = """
            trait A(a) =
                | value : a -> Bool
            trait B(a) =
                | value : a -> Bool
            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);
        TraitSymbol a = lowering.TraitSymbols["A"];
        TraitSymbol b = lowering.TraitSymbols["B"];
        var parameter = new TypeParameterSymbol("item");
        TypeRef parameterType = new TypeRef.TTypeParam(parameter);
        TypeRef listParameterType = new TypeRef.TList(parameterType);
        TraitDeclarationProvenance provenance = new("test", "Main", "<cycle>", new TextSpan(0, 1));
        lowering.AddRegisteredTraitInstance(CreateSyntheticInstance(
            a, listParameterType, b, parameterType, provenance));
        lowering.AddRegisteredTraitInstance(CreateSyntheticInstance(
            b, parameterType, a, listParameterType, provenance));

        TraitEvidencePlan? result = lowering.ResolveTraitEvidence(
            new TraitConstraint(a, [new TypeRef.TList(new TypeRef.TInt())]));

        result.ShouldBeNull();
        DiagnosticEntry cycle = diagnostics.StructuredErrors.Single(error =>
            error.Message.Contains("Cyclic trait resolution", StringComparison.Ordinal));
        cycle.Message.ShouldContain("A(List<Int>) -> B(Int) -> A(List<Int>)");
    }

    [Test]
    public void AbstractGoalRemainsAParameterWithoutRefiningItToAConcreteInstance()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> true
            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);
        var variable = new TypeRef.TVar(999_999);
        TraitEvidencePlan.Parameter plan = lowering.ResolveTraitEvidence(
            new TraitConstraint(lowering.TraitSymbols["Eq"], [variable]))
            .ShouldBeOfType<TraitEvidencePlan.Parameter>();

        diagnostics.StructuredErrors.ShouldBeEmpty();
        plan.Goal.TypeArgs.Single().ShouldBeSameAs(variable);
    }

    [Test]
    public void ConcreteEvidenceCacheIsStableAndReusesIdenticalPlans()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> true
            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true
            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);
        TraitSymbol eq = lowering.TraitSymbols["Eq"];
        TraitConstraint listGoal = new(eq, [new TypeRef.TList(new TypeRef.TInt())]);
        TraitConstraint intGoal = new(eq, [new TypeRef.TInt()]);
        TraitEvidencePlan first = lowering.ResolveTraitEvidence(listGoal).ShouldNotBeNull();
        _ = lowering.ResolveTraitEvidence(intGoal);
        TraitEvidencePlan second = lowering.ResolveTraitEvidence(listGoal).ShouldNotBeNull();

        diagnostics.StructuredErrors.ShouldBeEmpty();
        second.ShouldBeSameAs(first);
        lowering.ResolvedConcreteTraitEvidence.Count.ShouldBe(2);
        lowering.ResolvedConcreteTraitEvidence.Select(plan => plan.Goal.TypeArgs.Single().GetType().Name)
            .ShouldBe([nameof(TypeRef.TInt), nameof(TypeRef.TList)]);
    }

    [Test]
    public void OverlappingRegistryNeverSelectsBySpecificity()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool
            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true
            implement Eq(List(Int)) =
                | equal = given (left) -> given (right) -> true
            Eq.equal([1])([2])
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("Overlapping implementations", StringComparison.Ordinal));
        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains("Multiple implementations supply", StringComparison.Ordinal));
    }

    private static Lowering Lower(string source, out Diagnostics diagnostics)
    {
        diagnostics = new Diagnostics();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("<memory>", source);
        _ = lowering.Lower(program);
        return lowering;
    }

    private static TraitInstanceSymbol CreateSyntheticInstance(
        TraitSymbol head,
        TypeRef headType,
        TraitSymbol requirement,
        TypeRef requirementType,
        TraitDeclarationProvenance provenance)
    {
        return new TraitInstanceSymbol(
            new TraitInstanceHead(head, [headType]),
            [new TraitConstraint(requirement, [requirementType])],
            new Dictionary<string, Expr>(StringComparer.Ordinal),
            new TraitImplementationDecl(head.Name, [new TypeExpr.Named("Int")], [], []),
            provenance);
    }
}
