using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class DerivingTests
{
    [Test]
    public void ParameterizedAdtGeneratesOrdinaryConditionalImplementations()
    {
        const string source = """
            type Box(a) =
                | Box(a)
                deriving {Eq, Show}

            0
            """;

        Lowering lowering = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        TraitInstanceSymbol[] instances = lowering.TraitInstances
            .Where(instance => instance.Head.TypeArgs.SingleOrDefault() is TypeRef.TNamedType named
                && string.Equals(named.Symbol.Name, "Box", StringComparison.Ordinal))
            .OrderBy(instance => instance.Head.Trait.Name, StringComparer.Ordinal)
            .ToArray();
        instances.Select(instance => instance.Head.Trait.Name).ShouldBe(["Eq", "Show"]);
        instances.ShouldAllBe(instance => instance.Requirements.Count == 1);
        instances.ShouldAllBe(instance => instance.Requirements[0].Trait.Name == instance.Head.Trait.Name);
        instances.ShouldAllBe(instance => ReferenceEquals(
            instance.Requirements[0].TypeArgs.Single().ShouldBeOfType<TypeRef.TTypeParam>().Symbol,
            instance.Head.TypeArgs.Single().ShouldBeOfType<TypeRef.TNamedType>().TypeArgs.Single()
                .ShouldBeOfType<TypeRef.TTypeParam>().Symbol));
    }

    [Test]
    public void RecursiveAdtAndRecordDeriveAllSupportedTraits()
    {
        const string source = """
            type Tree(a) =
                | Leaf(a)
                | Branch(Tree(a), Tree(a))
                deriving {Eq, Ord, Show, Hash}

            type Point =
                | x: Int
                | y: Int
                deriving {Eq, Ord, Show, Hash}

            let tree = Branch(Leaf(1), Leaf(2))
            let point = Point(x = 3, y = 4)
            (tree == tree, tree < Branch(Leaf(1), Leaf(3)), Ashes.Trait.Show.show(point), Ashes.Trait.Hash.hash(tree))
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void DerivedAndHandwrittenImplementationsUseTheSameCoherenceRules()
    {
        const string source = """
            type Color =
                | Red
                deriving {Eq}

            implement Eq(Color) =
                | equal = given (left) -> given (right) -> true

            0
            """;

        _ = Lower(source, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldContain(diagnostic =>
            string.Equals(diagnostic.Code, DiagnosticCodes.TraitCoherence, StringComparison.Ordinal)
            && diagnostic.Message.Contains("Overlapping implementations", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("type Bad = | Bad(Int -> Int) deriving {Eq}\n0", "function type")]
    [Arguments("external type Handle\ntype Bad = | Bad(Handle) deriving {Show}\n0", "opaque external type 'Handle'")]
    [Arguments("type Bad = | Bad(Task(Str, Int)) deriving {Hash}\n0", "task type 'Task'")]
    [Arguments("type Bad(a) = | Bad(Bad(List(a))) deriving {Ord}\n0", "non-regular recursion")]
    public void UnsupportedDerivedFieldsHaveFocusedDiagnostics(string source, string expected)
    {
        _ = Lower(source, out Diagnostics diagnostics);

        DiagnosticEntry diagnostic = diagnostics.StructuredErrors.ShouldHaveSingleItem();
        diagnostic.Message.ShouldContain("Cannot derive");
        diagnostic.Message.ShouldContain(expected);
    }

    [Test]
    public void DerivedImplementationIsVisibleAcrossProjectModules()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-deriving-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """{"name":"deriving-app","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Model.ash"), """
                type Color =
                    | Red
                    | Blue
                    deriving {Eq, Show}

                let favorite = Blue
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Model
                (Model.favorite == Model.favorite, Ashes.Trait.Show.show(Model.favorite))
                """);

            AshesProject project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project);
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(diagnostics, plan.ImportedStdModules);
            lowering.SetSourceContext(layout);
            _ = lowering.Lower(program);

            diagnostics.StructuredErrors.ShouldBeEmpty();
            lowering.TraitInstances.Any(instance =>
                string.Equals(instance.Head.Trait.QualifiedName, "Ashes.Trait.Eq", StringComparison.Ordinal)
                && instance.Head.TypeArgs.SingleOrDefault() is TypeRef.TNamedType named
                && named.Symbol.Name.EndsWith("Color", StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Lowering Lower(string source, out Diagnostics diagnostics)
    {
        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports,
            parsed.ImportNames,
            "<memory>");
        diagnostics = new Diagnostics();
        Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        Lowering lowering = new(
            diagnostics,
            new HashSet<string>(["Ashes.Trait"], StringComparer.Ordinal),
            parsed.ImportAliases,
            layout.ConstructorModules);
        lowering.SetSourceContext(layout);
        _ = lowering.Lower(program);
        return lowering;
    }
}
