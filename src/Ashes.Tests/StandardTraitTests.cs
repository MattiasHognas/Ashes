using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class StandardTraitTests
{
    [Test]
    public void StandardTraitsAreAvailableWithoutAnImport()
    {
        Lower("Ashes.Trait.Eq.equal(1)(1)", out Lowering lowering, out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.TraitSymbols.Keys.ShouldContain("Ashes.Trait.Eq");
        lowering.TraitSymbols.Keys.ShouldContain("Ashes.Trait.Show");
        lowering.TraitSymbols.Keys.ShouldContain("Ashes.Trait.Add");
        lowering.TraitSymbols.Keys.ShouldContain("Ashes.Trait.Not");
    }

    [Test]
    public void PrimitiveBootstrapImplementationsResolve()
    {
        Lower(
            "(Ashes.Trait.Eq.equal(1)(1), Ashes.Trait.Show.show(1), Ashes.Trait.Default.default(Unit))",
            out Lowering lowering,
            out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.TraitInstances.ShouldContain(instance =>
            string.Equals(instance.Head.Trait.QualifiedName, "Ashes.Trait.Eq", StringComparison.Ordinal)
            && instance.Head.TypeArgs.Single() is TypeRef.TInt);
    }

    [Test]
    public void ConditionalListAndMaybeImplementationsResolve()
    {
        Lower(
            "(Ashes.Trait.Eq.equal([1, 2])([1, 2]), Ashes.Trait.Show.show(Some(1)))",
            out _,
            out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void FunctionTypesDeliberatelyHaveNoEqualityImplementation()
    {
        Lower(
            "let identity : Int -> Int = given (x) -> x in Ashes.Trait.Eq.equal(identity)(identity)",
            out _,
            out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldContain(diagnostic =>
            diagnostic.Message.Contains("No implementation supplies", StringComparison.Ordinal));
    }

    [Test]
    public void ConstrainedMapApisCompileWithStandardOrderingEvidence()
    {
        Lower(
            "import Ashes.Collection.Map\nAshes.Collection.Map.get(1)(Ashes.Collection.Map.empty)",
            out _,
            out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void ImportedMapApiCanForwardAbstractOrderingEvidence()
    {
        Lower(
            """
            import Ashes.Collection.Map
            import Ashes.Collection.Map.MapTree
            let lookup : k -> MapTree(k, v) -> Maybe(v) requires {Ord(k)} =
                given (key) -> given (map) -> Ashes.Collection.Map.get(key)(map)
            """,
            out _,
            out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    [Test]
    public void ImportedListSortCanForwardAbstractOrderingEvidence()
    {
        Lower(
            """
            import Ashes.Collection.List
            let ordered : List(a) -> List(a) requires {Ord(a)} =
                given (values) -> Ashes.Collection.List.sort(values)
            """,
            out _,
            out Diagnostics diagnostics);

        diagnostics.StructuredErrors.ShouldBeEmpty();
    }

    private static IrProgram Lower(string source, out Lowering lowering, out Diagnostics diagnostics)
    {
        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports,
            parsed.ImportNames,
            "<memory>");
        diagnostics = new Diagnostics();
        Ashes.Frontend.Program syntax = new Parser(layout.Source, diagnostics).ParseProgram();
        lowering = new Lowering(
            diagnostics,
            new HashSet<string>(["Ashes.Trait"], StringComparer.Ordinal),
            parsed.ImportAliases,
            layout.ConstructorModules);
        lowering.SetSourceContext(layout);
        return lowering.Lower(syntax);
    }
}
