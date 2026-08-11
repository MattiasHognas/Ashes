using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class TraitTypeSchemeTests
{
    [Test]
    public void GeneralizationIncludesVariablesUsedOnlyByConstraints()
    {
        TraitSymbol eq = CreateTrait("Eq");
        TypeRef.TVar bodyVariable = new(7);
        TypeRef.TVar constraintVariable = new(11);

        TypeScheme scheme = TraitTypeOperations.Generalize(
            new TypeRef.TFun(bodyVariable, bodyVariable),
            [new TraitConstraint(eq, [constraintVariable])],
            new HashSet<int>());

        scheme.Quantified.Select(variable => variable.Id).ShouldBe([7, 11]);
    }

    [Test]
    public void InstantiationFreshensBodyAndConstraintsWithTheSameSubstitution()
    {
        TraitSymbol eq = CreateTrait("Eq");
        TypeRef.TVar variable = new(3);
        TypeScheme scheme = new(
            [new TypeVar(3, "a")],
            new TypeRef.TFun(variable, variable),
            [new TraitConstraint(eq, [variable])]);

        InstantiatedTypeScheme instantiated = TraitTypeOperations.Instantiate(
            scheme,
            () => new TypeRef.TVar(99));

        TypeRef.TFun function = instantiated.Body.ShouldBeOfType<TypeRef.TFun>();
        function.Arg.ShouldBe(new TypeRef.TVar(99));
        function.Ret.ShouldBe(new TypeRef.TVar(99));
        instantiated.Constraints.Single().TypeArgs.Single().ShouldBe(new TypeRef.TVar(99));
    }

    [Test]
    public void SubstitutionTraversesConstraintAggregateAndCapabilityTypes()
    {
        TraitSymbol show = CreateTrait("Show");
        TypeRef.TVar variable = new(4);
        TypeRef aggregate = new TypeRef.TTuple([new TypeRef.TList(variable), variable]);
        TraitConstraint constraint = new(show, [aggregate]);

        TraitConstraint substituted = TraitTypeOperations.Substitute(
            constraint,
            new Dictionary<int, TypeRef> { [4] = new TypeRef.TStr() });

        TypeRef.TTuple tuple = substituted.TypeArgs.Single().ShouldBeOfType<TypeRef.TTuple>();
        tuple.Elements[0].ShouldBe(new TypeRef.TList(new TypeRef.TStr()));
        tuple.Elements[1].ShouldBe(new TypeRef.TStr());
    }

    [Test]
    public void OccursCheckSeesVariablesInsideTraitConstraints()
    {
        TraitSymbol eq = CreateTrait("Eq");
        TraitConstraint constraint = new(eq, [new TypeRef.TList(new TypeRef.TVar(12))]);

        TraitTypeOperations.Occurs(12, constraint).ShouldBeTrue();
        TraitTypeOperations.Occurs(13, constraint).ShouldBeFalse();
    }

    [Test]
    public void ConstraintsAreDeduplicatedAndOrderedCanonically()
    {
        TraitSymbol show = CreateTrait("Show");
        TraitSymbol eq = CreateTrait("Eq");
        TraitConstraint eqInt = new(eq, [new TypeRef.TInt()]);

        TypeScheme scheme = new(
            [],
            new TypeRef.TInt(),
            [new TraitConstraint(show, [new TypeRef.TInt()]), eqInt, eqInt]);

        scheme.Constraints.Select(constraint => constraint.Trait.Name).ShouldBe(["Eq", "Show"]);
    }

    [Test]
    public void PrettyPrintingKeepsRequiresSeparateFromCapabilityNeeds()
    {
        TraitSymbol eq = CreateTrait("Eq");
        CapabilitySymbol log = new(
            "Log",
            [],
            new Dictionary<string, CapabilityOperationSymbol>(StringComparer.Ordinal),
            new CapabilityDecl("Log", [], []));
        TypeRef.TVar variable = new(1);
        TypeRef.TFun function = new(variable, variable)
        {
            Row = new TypeRef.TRow([new TypeRef.TCapability(log, [])], null),
        };
        TypeScheme scheme = new([new TypeVar(1, "a")], function, [new TraitConstraint(eq, [variable])]);
        Lowering lowering = new(new Diagnostics());

        lowering.FormatTypeScheme(scheme).ShouldBe("a -> a needs {Log} requires {Eq(a)}");
    }

    [Test]
    public void HoverMetadataRetainsCanonicalConstraintEvidence()
    {
        // Exercises the real inference/hover-recording path (RecordHoverScheme, called from
        // FinalizeLetTraitScheme during ordinary lowering) rather than constructing a
        // HoverTypeInfo by hand and asserting the field just set.
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            let same : a -> a -> Bool requires {Eq(a)} =
                given (left) -> given (right) -> Eq.equal(left)(right)

            same
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        Lowering.HoverTypeInfo hover = lowering.GetTypeAtPosition(
            source.IndexOf("same :", StringComparison.Ordinal)).ShouldNotBeNull();
        hover.Constraints.ShouldNotBeNull();
        hover.Constraints.Single().Trait.Name.ShouldBe("Eq");
        lowering.FormatType(hover.Type).ShouldBe("a -> a -> Bool");
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

    private static TraitSymbol CreateTrait(string name)
    {
        TraitDecl syntax = new(name, [new TypeParameter("a")], [], []);
        return new TraitSymbol(
            name,
            name,
            [new TypeParameterSymbol("a")],
            new Dictionary<string, TraitMethodSymbol>(StringComparer.Ordinal),
            [],
            syntax,
            new TraitDeclarationProvenance("test", "Test", "<memory>", new TextSpan(0, 1)));
    }
}
