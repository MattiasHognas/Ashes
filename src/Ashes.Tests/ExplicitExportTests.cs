using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ExplicitExportTests
{
    [Test]
    public void Parser_and_formatter_round_trip_explicit_interfaces()
    {
        const string source = "export (value make, type Box, type Choice(Yes), type Color(..), module Nested)\nlet make = 1\n";
        var diagnostics = new Diagnostics();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        ExportDecl declaration = program.Items[0].ShouldBeOfType<TopLevelItem.Export>().Decl;
        declaration.Items.Count.ShouldBe(5);
        declaration.Items[1].ShouldBe(new ExportItem.Type("Box", new ExportConstructors.Hidden()));
        ExportItem.Type selected = declaration.Items[2].ShouldBeOfType<ExportItem.Type>();
        selected.Name.ShouldBe("Choice");
        selected.Constructors.ShouldBeOfType<ExportConstructors.Selected>().Names.ShouldBe(["Yes"]);
        declaration.Items[3].ShouldBe(new ExportItem.Type("Color", new ExportConstructors.All()));

        Ashes.Formatter.Formatter.Format(program).ShouldStartWith(
            "export (\n    value make,\n    type Box,\n    type Choice(Yes),\n    type Color(..),\n    module Nested,\n)\n");
    }

    [Test]
    public void Explicit_interface_hides_values_but_keeps_them_available_inside_the_module()
    {
        string directory = WriteProject(
            ("Library", "export (value public)\nlet secret = 40\nlet public = secret + 2\n"),
            ("Main", "import Library\nLibrary.public\n"));

        CombinedCompilationLayout layout = BuildLayout(directory);
        layout.Source.ShouldContain("Library_public");
        layout.Source.ShouldContain("__ashes_private_value_Library_secret");
        layout.Source.ShouldNotContain("let Library_secret");
        Lower(directory, layout);
    }

    [Test]
    public void Private_value_renaming_does_not_rewrite_same_named_record_fields()
    {
        string directory = WriteProject(
            ("Library", "export (value make, value read, type Packet(..))\n" +
                "type Packet =\n" +
                "    | item: Int\n" +
                "let item = 40\n" +
                "let make = Packet(item = item + 2)\n" +
                "let read = given (packet) ->\n" +
                "    match packet with\n" +
                "        | Packet { item = value } -> packet with item = packet.item + value\n"),
            ("Main", "import Library\nmatch Library.read(Library.make) with | Packet(value) -> value\n"));

        CombinedCompilationLayout layout = BuildLayout(directory);
        layout.Source.ShouldContain("| item: Int");
        layout.Source.ShouldContain("Packet(item =");
        layout.Source.ShouldContain("__ashes_private_value_Library_item");
        layout.Source.ShouldContain("Packet { item = value }");
        layout.Source.ShouldContain("with item = packet.item + value");
        Lower(directory, layout);
    }

    [Test]
    public void Hidden_value_is_indistinguishable_from_an_unknown_qualified_member()
    {
        string directory = WriteProject(
            ("Library", "export (value public)\nlet secret = 40\nlet public = secret + 2\n"),
            ("Main", "import Library\nLibrary.secret\n"));

        CombinedCompilationLayout layout = BuildLayout(directory);
        CompileDiagnosticException exception = Should.Throw<CompileDiagnosticException>(() => Lower(directory, layout));
        exception.Message.ShouldContain("does not export 'secret'");
    }

    [Test]
    public void Abstract_type_flows_through_public_functions_but_hides_its_constructor()
    {
        string directory = WriteProject(
            ("Library", "export (value make, type Box)\ntype Box =\n    | BoxValue(Int)\nlet make = given (value) -> BoxValue(value)\n"),
            ("Main", "import Library\nlet box : Box = Library.make(1)\n0\n"));

        CombinedCompilationLayout layout = BuildLayout(directory);
        Lower(directory, layout);
        layout.ConstructorModules!["Library"].ShouldNotContain("BoxValue");

        string patternDirectory = WriteProject(
            ("Library", "export (value make, type Box)\ntype Box =\n    | BoxValue(Int)\nlet make = given (value) -> BoxValue(value)\n"),
            ("Main", "import Library\nmatch Library.make(1) with | BoxValue(value) -> value\n"));
        CombinedCompilationLayout patternLayout = BuildLayout(patternDirectory);
        CompileDiagnosticException exception = Should.Throw<CompileDiagnosticException>(() =>
            Lower(patternDirectory, patternLayout));
        exception.Message.ShouldContain("Unknown constructor 'BoxValue'");
    }

    [Test]
    public void Abstract_zero_cost_type_and_exported_alias_follow_the_module_interface()
    {
        string directory = WriteProject(
            ("Library", "export (value make, type Identifier, type UserId)\n" +
                "type alias Identifier(a) = a\n" +
                "type UserId = UserId(Identifier(Int))\n" +
                "let make = given (value) -> UserId(value)\n"),
            ("Main", "import Library\nlet id : UserId = Library.make(42)\n0\n"));

        CombinedCompilationLayout layout = BuildLayout(directory);
        Lower(directory, layout);
        layout.ConstructorModules!["Library"].ShouldNotContain("UserId");

        string hiddenDirectory = WriteProject(
            ("Library", "export (value make, type UserId)\n" +
                "type UserId = UserId(Int)\n" +
                "let make = given (value) -> UserId(value)\n"),
            ("Main", "import Library\nmatch Library.make(42) with | UserId(value) -> value\n"));
        CombinedCompilationLayout hiddenLayout = BuildLayout(hiddenDirectory);
        CompileDiagnosticException exception = Should.Throw<CompileDiagnosticException>(() =>
            Lower(hiddenDirectory, hiddenLayout));
        exception.Message.ShouldContain("Unknown constructor 'UserId'");
    }

    [Test]
    public void Selected_constructor_exports_filter_qualified_access()
    {
        string directory = WriteProject(
            ("Library", "export (type Choice(Yes))\ntype Choice =\n    | Yes\n    | No\n"),
            ("Main", "import Library\nLibrary.No\n"));

        CombinedCompilationLayout layout = BuildLayout(directory);
        layout.ConstructorModules!["Library"].ShouldContain("Yes");
        layout.ConstructorModules["Library"].ShouldNotContain("No");
        CompileDiagnosticException exception = Should.Throw<CompileDiagnosticException>(() => Lower(directory, layout));
        exception.Message.ShouldContain("does not export 'No'");

        string matchDirectory = WriteProject(
            ("Library", "export (type Choice(Yes))\ntype Choice =\n    | Yes\n    | No\n"),
            ("Main", "import Library\nmatch Library.Yes with | Yes -> 1\n"));
        CombinedCompilationLayout matchLayout = BuildLayout(matchDirectory);
        CompileDiagnosticException matchException = Should.Throw<CompileDiagnosticException>(() =>
            Lower(matchDirectory, matchLayout));
        matchException.Message.ShouldContain("require a catch-all pattern");
        matchException.Message.ShouldNotContain(ProjectSupport.PrivateConstructorPrefix);
    }

    [Test]
    public void Unknown_and_duplicate_interface_entries_have_stable_diagnostics()
    {
        string unknownDirectory = WriteProject(
            ("Library", "export (value missing)\nlet present = 1\n"),
            ("Main", "import Library\n0\n"));
        Should.Throw<InvalidOperationException>(() => BuildLayout(unknownDirectory))
            .Message.ShouldContain("[ASH038]");

        string duplicateDirectory = WriteProject(
            ("Library", "export (value present, value present)\nlet present = 1\n"),
            ("Main", "import Library\n0\n"));
        Should.Throw<InvalidOperationException>(() => BuildLayout(duplicateDirectory))
            .Message.ShouldContain("[ASH037]");
    }

    [Test]
    public void Nested_inline_modules_must_be_exported_by_their_parent()
    {
        string directory = WriteProject(
            ("Parent", "export (module Inner)\nmodule Inner =\n    export (value answer)\n    let hidden = 1\n    let answer = 42\n"),
            ("Main", "import Parent.Inner\nParent.Inner.answer\n"));
        CombinedCompilationLayout layout = BuildLayout(directory);
        Lower(directory, layout);

        string hiddenDirectory = WriteProject(
            ("Parent", "export (value visible)\nlet visible = 1\nmodule Inner =\n    let answer = 42\n"),
            ("Main", "import Parent.Inner\nParent.Inner.answer\n"));
        Should.Throw<InvalidOperationException>(() => BuildLayout(hiddenDirectory))
            .Message.ShouldContain("Module 'Parent' does not export 'Inner'");
    }

    private static CombinedCompilationLayout BuildLayout(string directory)
    {
        ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(BuildProject(directory));
        return ProjectSupport.BuildCompilationLayout(plan);
    }

    private static void Lower(string directory, CombinedCompilationLayout layout)
    {
        ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(BuildProject(directory));
        var diagnostics = new Diagnostics();
        Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        _ = new Lowering(
            diagnostics,
            plan.ImportedStdModules,
            plan.MergedAliases,
            layout.ConstructorModules).Lower(program);
        diagnostics.ThrowIfAny();
    }

    private static AshesProject BuildProject(string directory) => new(
        Path.Combine(directory, "ashes.json"),
        directory,
        Path.Combine(directory, "Main.ash"),
        "Main",
        null,
        [directory],
        [],
        Path.Combine(directory, "out"),
        null);

    private static string WriteProject(params (string Module, string Source)[] modules)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ashes-explicit-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ashes.json"), "{\"entry\":\"Main.ash\"}");
        foreach ((string module, string source) in modules)
        {
            File.WriteAllText(Path.Combine(directory, module + ".ash"), source);
        }

        return directory;
    }
}
