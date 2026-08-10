using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class TraitRegistrationTests
{
    [Test]
    public void RegistersMethodsDefaultsSupertraitsAndGenericImplementationsBeforeValueInference()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            trait ShowEq(a) requires {Eq(a)} =
                | showEqual : a -> a -> Bool = given (left) -> given (right) -> true

            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true

            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.TraitSymbols["ShowEq"].Supertraits.Single().Trait.Name.ShouldBe("Eq");
        lowering.TraitSymbols["ShowEq"].Methods["showEqual"].DefaultImplementation.ShouldNotBeNull();
        TraitInstanceSymbol instance = lowering.TraitInstances.Single();
        instance.Head.TypeArgs.Single().ShouldBeOfType<TypeRef.TList>();
        instance.Requirements.Single().Trait.Name.ShouldBe("Eq");
    }

    [Test]
    [Arguments("implement Missing(Int) = | equal = given (left) -> given (right) -> true", "Unknown trait")]
    [Arguments("trait Eq(a) = | equal : a -> a -> Bool\nimplement Eq(Int, Str) = | equal = given (left) -> given (right) -> true", "expects 1 type argument")]
    [Arguments("trait Eq(a) = | equal : a -> a -> Bool\nimplement Eq(Int) = | other = given (left) -> given (right) -> true", "Unknown method")]
    [Arguments("trait Eq(a) = | equal : a -> a -> Bool\nimplement Eq(Int) = | equal = given (left) -> given (right) -> true | equal = given (left) -> given (right) -> true", "more than once")]
    [Arguments("trait Eq(a) = | equal : a -> a -> Bool\nimplement Eq(Int) = |", "missing required method")]
    [Arguments("trait Eq(a) = | equal : a -> a -> Bool\nimplement Eq(List(a)) requires {Eq(b)} = | equal = given (left) -> given (right) -> true", "does not occur in the implementation head")]
    public void RejectsInvalidTraitImplementationDeclarations(string declarations, string expectedMessage)
    {
        _ = Lower($"{declarations}\n0", out Diagnostics diagnostics);

        diagnostics.StructuredErrors.Select(error => error.Message)
            .ShouldContain(message => message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Test]
    public void RejectsDuplicateAndUnifiableOverlappingHeads()
    {
        const string source = """
            trait Eq(a) =
                | equal : a -> a -> Bool

            implement Eq(List(a)) requires {Eq(a)} =
                | equal = given (left) -> given (right) -> true

            implement Eq(List(Int)) =
                | equal = given (left) -> given (right) -> true

            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        DiagnosticEntry overlap = diagnostics.StructuredErrors.Single(error => string.Equals(error.Code, DiagnosticCodes.TraitCoherence, StringComparison.Ordinal));
        overlap.Message.ShouldContain("Overlapping implementations");
        overlap.Message.ShouldContain("<memory>");
    }

    [Test]
    public void OverlapDiagnosticNamesBothConflictingModuleLocations()
    {
        const string traitSource = "trait Eq(a) =\n    | equal : a -> a -> Bool\n";
        const string first = "implement Eq(Int) =\n    | equal = given (left) -> given (right) -> true\n";
        const string second = "implement Eq(Int) =\n    | equal = given (left) -> given (right) -> false\n0";
        string source = traitSource + first + second;
        int firstStart = traitSource.Length;
        int secondStart = firstStart + first.Length;
        CombinedCompilationLayout layout = new(
            source,
            0,
            0,
            [
                ("/package/Traits.ash", 0, firstStart),
                ("/package/First.ash", firstStart, secondStart),
                ("/package/Second.ash", secondStart, source.Length),
            ],
            ModuleProvenanceByPath: new Dictionary<string, ModuleProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["/package/Traits.ash"] = new("package", "Traits"),
                ["/package/First.ash"] = new("package", "First"),
                ["/package/Second.ash"] = new("package", "Second"),
            });

        _ = Lower(source, layout, out Diagnostics diagnostics);

        DiagnosticEntry overlap = diagnostics.StructuredErrors.Single(error =>
            string.Equals(error.Code, DiagnosticCodes.TraitCoherence, StringComparison.Ordinal));
        overlap.Message.ShouldContain("first at /package/First.ash:");
        overlap.Message.ShouldContain("conflicting implementation at /package/Second.ash:");
    }

    [Test]
    public void RejectsSuperclassCyclesWithDeterministicTrace()
    {
        const string source = """
            trait A(a) requires {B(a)} =
                | a : a -> Bool
            trait B(a) requires {A(a)} =
                | b : a -> Bool
            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        DiagnosticEntry cycle = diagnostics.StructuredErrors.Single(error => string.Equals(error.Code, DiagnosticCodes.TraitCycle, StringComparison.Ordinal));
        cycle.Message.ShouldContain("A -> B -> A");
    }

    [Test]
    public void UsesManifestPackageIdentityForTheOrphanRule()
    {
        const string traitSource = "trait Eq(a) =\n    | equal : a -> a -> Bool\n";
        const string instanceSource = "implement Eq(Int) =\n    | equal = given (left) -> given (right) -> true\n0";
        string source = traitSource + instanceSource;
        CombinedCompilationLayout layout = new(
            source,
            traitSource.Length,
            traitSource.Length,
            [
                ("/packages/owner/Eq.ash", 0, traitSource.Length),
                ("/packages/consumer/Main.ash", traitSource.Length, source.Length),
            ],
            ModuleProvenanceByPath: new Dictionary<string, ModuleProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["/packages/owner/Eq.ash"] = new("owner", "Owner.Eq"),
                ["/packages/consumer/Main.ash"] = new("consumer", "Main"),
            });

        _ = Lower(source, layout, out Diagnostics diagnostics);

        DiagnosticEntry orphan = diagnostics.StructuredErrors.Single(error => string.Equals(error.Code, DiagnosticCodes.TraitCoherence, StringComparison.Ordinal));
        orphan.Message.ShouldContain("Orphan implementation");
        orphan.Message.ShouldContain("consumer");
    }

    [Test]
    public void PackageMayImplementAForeignTraitForItsOwnNominalType()
    {
        const string traitSource = "trait Render(a) =\n    | render : a -> Str\n";
        const string consumerSource = """
            type Card =
                | Card(Int)
            implement Render(Card) =
                | render = given (_) -> "card"
            Render.render(Card(1))
            """;
        string source = traitSource + consumerSource;
        CombinedCompilationLayout layout = new(
            source,
            traitSource.Length,
            traitSource.Length,
            [
                ("/packages/owner/Render.ash", 0, traitSource.Length),
                ("/packages/consumer/Main.ash", traitSource.Length, source.Length),
            ],
            ModuleProvenanceByPath: new Dictionary<string, ModuleProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["/packages/owner/Render.ash"] = new("owner", "Owner.Render"),
                ["/packages/consumer/Main.ash"] = new("consumer", "Main"),
            });

        Lowering lowering = Lower(source, layout, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        TraitInstanceSymbol instance = lowering.TraitInstances.ShouldHaveSingleItem();
        instance.Provenance.PackageId.ShouldBe("consumer");
        instance.Head.Trait.Provenance.PackageId.ShouldBe("owner");
    }

    [Test]
    public void KeepsSameNamedTraitsDistinctAcrossModulesAndIndependentOfDeclarationOrder()
    {
        const string a = "trait Eq(a) =\n    | equal : a -> a -> Bool\n";
        const string b = "trait Eq(a) =\n    | same : a -> a -> Bool\n";
        (IReadOnlyList<string> First, IReadOnlyList<string> Second) keys = (
            RegisterModuleTraits(a, b),
            RegisterModuleTraits(b, a, reverseModules: true));

        keys.First.ShouldBe(["A.Eq", "B.Eq"]);
        keys.Second.ShouldBe(keys.First);
    }

    [Test]
    public void ProjectModulesExportTraitsThroughSelectorsWhileImplementationsRemainProgramGlobal()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-trait-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """{"name":"trait-app","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Traits.ash"), """
                trait Equivalent(a) =
                    | equal : a -> a -> Bool
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Traits.Equivalent
                implement Equivalent(Int) =
                    | equal = given (left) -> given (right) -> true
                0
                """);

            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(
                ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(diagnostics);
            lowering.SetSourceContext(layout);
            _ = lowering.Lower(program);

            diagnostics.StructuredErrors.ShouldBeEmpty();
            lowering.TraitSymbols.Values.Select(trait => trait.QualifiedName)
                .ShouldContain("Traits.Equivalent");
            lowering.TraitInstances.Single(instance => string.Equals(
                instance.Head.Trait.QualifiedName,
                "Traits.Equivalent",
                StringComparison.Ordinal)).Head.Trait.QualifiedName.ShouldBe("Traits.Equivalent");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<string> RegisterModuleTraits(
        string first,
        string second,
        bool reverseModules = false)
    {
        string source = first + second + "0";
        string firstModule = reverseModules ? "B" : "A";
        string secondModule = reverseModules ? "A" : "B";
        string firstPath = $"/{firstModule}.ash";
        string secondPath = $"/{secondModule}.ash";
        CombinedCompilationLayout layout = new(
            source,
            first.Length + second.Length,
            first.Length + second.Length,
            [(firstPath, 0, first.Length), (secondPath, first.Length, first.Length + second.Length)],
            ModuleProvenanceByPath: new Dictionary<string, ModuleProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                [firstPath] = new("package", firstModule),
                [secondPath] = new("package", secondModule),
            });
        Lowering lowering = Lower(source, layout, out Diagnostics diagnostics);
        diagnostics.StructuredErrors.ShouldBeEmpty();
        return lowering.TraitSymbols.Values
            .Select(trait => trait.QualifiedName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static Lowering Lower(string source, out Diagnostics diagnostics)
    {
        CombinedCompilationLayout layout = new(
            source,
            0,
            0,
            [("<memory>", 0, source.Length)],
            ModuleProvenanceByPath: new Dictionary<string, ModuleProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["<memory>"] = new("standalone", "Main"),
            });
        return Lower(source, layout, out diagnostics);
    }

    private static Lowering Lower(
        string source,
        CombinedCompilationLayout layout,
        out Diagnostics diagnostics)
    {
        diagnostics = new Diagnostics();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext(layout);
        _ = lowering.Lower(program);
        return lowering;
    }
}
