using Ashes.Lsp;
using Shouldly;

namespace Ashes.Tests;

public sealed class LspDocumentServiceTests
{
    [Test]
    public void Analyze_should_return_positioned_diagnostics()
    {
        var diagnostics = DocumentService.Analyze("if true then 1");

        diagnostics.Count.ShouldBeGreaterThan(0);
        diagnostics[0].Start.ShouldBe(14);
        diagnostics[0].End.ShouldBe(14);
        diagnostics[0].Message.ShouldContain("Expected Else");
    }

    [Test]
    public void Analyze_should_return_parse_diagnostic_for_empty_file_without_crashing()
    {
        var diagnostics = DocumentService.Analyze(string.Empty);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Code.ShouldBe("ASH003");
        diagnostics[0].Message.ShouldContain("Expected expression");
    }

    [Test]
    public void Analyze_should_return_parse_diagnostic_for_comment_only_file_without_crashing()
    {
        var diagnostics = DocumentService.Analyze("// comment\n");

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Code.ShouldBe("ASH003");
        diagnostics[0].Message.ShouldContain("Expected expression");
    }

    [Test]
    public void Format_should_return_canonical_output_for_valid_source()
    {
        var formatted = DocumentService.Format("Ashes.IO.print(40+2)");

        formatted.ShouldBe("Ashes.IO.print(40 + 2)\n");
    }

    [Test]
    public void Format_should_preserve_float_literal_text()
    {
        var formatted = DocumentService.Format("let x = 1.500 in x");

        formatted.ShouldBe("let x = 1.500\nin x\n");
    }

    [Test]
    public void Analyze_should_report_semantic_diagnostics()
    {
        var diagnostics = DocumentService.Analyze("x");

        diagnostics.Count.ShouldBeGreaterThan(0);
        diagnostics[0].Start.ShouldBe(0);
        diagnostics[0].End.ShouldBe(1);
        diagnostics[0].Message.ShouldContain("Undefined variable");
    }

    [Test]
    public void Analyze_should_require_import_for_unqualified_print()
    {
        var diagnostics = DocumentService.Analyze("print(1)");

        diagnostics.Count.ShouldBeGreaterThan(0);
        diagnostics[0].Message.ShouldContain("Undefined variable 'print'");
    }

    [Test]
    public void Format_should_return_null_for_invalid_source()
    {
        var formatted = DocumentService.Format("if true then 1");

        formatted.ShouldBeNull();
    }

    [Test]
    public void Format_should_escape_special_characters_in_strings()
    {
        var formatted = DocumentService.Format("Ashes.IO.print(\"a\\n\\t\\\"b\\\"\\\\\")");

        formatted.ShouldNotBeNull();
        formatted.ShouldContain("\\\"b\\\"");
        formatted.ShouldContain("\\\\");
    }

    [Test]
    public void Format_should_parenthesize_add_expression_in_call_function_position()
    {
        var formatted = DocumentService.Format("(1+2)(3)");

        formatted.ShouldBe("((1 + 2))(3)\n");
    }

    [Test]
    public void Format_should_render_multiline_let_consistently()
    {
        var formatted = DocumentService.Format("let x = if true then 1 else 2 in Ashes.IO.print(x)");

        formatted.ShouldBe("""
                           let x = 
                               if true
                               then 1
                               else 2
                           in Ashes.IO.print(x)
                           
                           """);
    }

    [Test]
    public void GetSemanticTokens_should_return_correct_type_and_constructor_and_type_parameter_spans()
    {
        const string source = "type Maybe =\n| None\n| Some(T)\nAshes.IO.print(1)";

        var tokens = DocumentService.GetSemanticTokens(source);

        tokens.ShouldContain(t => IsTokenWithText(source, t, DocumentService.TokenTypeType, "Maybe"));
        tokens.ShouldContain(t => IsTokenWithText(source, t, DocumentService.TokenTypeEnumMember, "None"));
        tokens.ShouldContain(t => IsTokenWithText(source, t, DocumentService.TokenTypeEnumMember, "Some"));
        tokens.ShouldContain(t => IsTokenWithText(source, t, DocumentService.TokenTypeTypeParameter, "T"));
    }

    [Test]
    public void GetSemanticTokens_should_return_empty_for_source_with_no_type_declarations()
    {
        var tokens = DocumentService.GetSemanticTokens("Ashes.IO.print(42)");

        tokens.ShouldBeEmpty();
    }

    [Test]
    public void GetCompletions_should_return_constructor_names_for_declared_types()
    {
        const string source = "type Maybe = | None | Some(T)\nAshes.IO.print(1)";

        var completions = DocumentService.GetCompletions(source);

        completions.ShouldContain("None");
        completions.ShouldContain("Some");
    }

    [Test]
    public void GetCompletions_should_return_builtin_constructors_for_source_with_no_type_declarations()
    {
        var completions = DocumentService.GetCompletions("Ashes.IO.print(42)");

        completions.ShouldBe(["Error", "None", "Ok", "Some", "Unit"]);
    }

    [Test]
    public void GetCompletions_should_return_constructors_from_multiple_types()
    {
        const string source = "type Outcome = | Ok(T) | Err(E)\ntype Maybe = | None | Some(T)\nAshes.IO.print(1)";

        var completions = DocumentService.GetCompletions(source);

        completions.ShouldContain("Ok");
        completions.ShouldContain("Err");
        completions.ShouldContain("None");
        completions.ShouldContain("Some");
    }

    [Test]
    public void Analyze_should_not_report_false_errors_for_import_lines()
    {
        const string source = "import Ashes.IO\nAshes.IO.print(1)";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Analyze_should_report_errors_at_correct_positions_after_import_lines()
    {
        const string source = "import Ashes.IO\nif true then 1";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.Count.ShouldBeGreaterThan(0);
        diagnostics[0].Start.ShouldBe(30);
        diagnostics[0].End.ShouldBe(30);
    }

    [Test]
    public void Analyze_should_allow_standalone_import_of_Ashes_IO_for_unqualified_print()
    {
        const string source = "import Ashes.IO\nprint(1)";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Analyze_should_allow_standalone_import_of_Ashes_Result_for_unqualified_helpers_and_constructors()
    {
        const string source = "import Ashes.Result\nif isOk(Ok(1)) then 1 else 0";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Analyze_should_allow_standalone_import_of_Ashes_Test()
    {
        const string source = "import Ashes.Test\nlet checked = assertEqual(1, 1)\nin checked";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Analyze_should_allow_standalone_import_of_Ashes_File()
    {
        const string source = "import Ashes.File\nmatch Ashes.File.exists(\"file.txt\") with | Ok(found) -> if found then 1 else 0 | Error(_) -> 0";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Analyze_should_allow_standalone_import_of_Ashes_Http()
    {
        const string source = "import Ashes.Http\nmatch Ashes.Http.get(\"http://example.com\") with | Ok(text) -> 1 | Error(_) -> 0";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Analyze_should_allow_standalone_import_of_Ashes_Net_Tcp()
    {
        const string source = "import Ashes.Net.Tcp\nmatch Ashes.Net.Tcp.connect(\"127.0.0.1\")(80) with | Ok(sock) -> 1 | Error(_) -> 0";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void Analyze_should_report_invalid_import_syntax()
    {
        const string source = "import ashes.io\nprint(1)";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Start.ShouldBe(0);
        diagnostics[0].End.ShouldBe(15);
        diagnostics[0].Message.ShouldContain("Invalid import syntax");
    }

    [Test]
    public void Analyze_should_report_user_module_imports_without_project_context()
    {
        const string source = "import Math\nprint(1)";

        var diagnostics = DocumentService.Analyze(source);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Start.ShouldBe(0);
        diagnostics[0].End.ShouldBe(11);
        diagnostics[0].Message.ShouldContain("Could not resolve module 'Math'");
    }

    [Test]
    public void Analyze_should_report_exact_identifier_span_for_unknown_identifier()
    {
        var diagnostics = DocumentService.Analyze("Ashes.IO.print(value)");

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Start.ShouldBe(15);
        diagnostics[0].End.ShouldBe(20);
        diagnostics[0].Message.ShouldContain("Undefined variable 'value'");
    }

    [Test]
    public void Format_should_preserve_import_lines_and_format_body()
    {
        const string source = "import Math\nAshes.IO.print(40+2)";

        var formatted = DocumentService.Format(source);

        formatted.ShouldNotBeNull();
        formatted.ShouldStartWith("import Math\n");
        formatted.ShouldContain("Ashes.IO.print(40 + 2)");
    }

    [Test]
    public void Format_should_preserve_leading_comment_lines()
    {
        const string source = "// expect: ok\n\nAshes.IO.print(40+2)";

        var formatted = DocumentService.Format(source);

        formatted.ShouldBe("// expect: ok\n\nAshes.IO.print(40 + 2)\n");
    }

    [Test]
    public void Format_should_preserve_standalone_comment_lines_inside_body()
    {
        const string source = "type Maybe =\n  | None\n  | Some(T)\n\n// body\nAshes.IO.print(40+2)";

        var formatted = DocumentService.Format(source);

        formatted.ShouldBe("type Maybe =\n    | None\n    | Some(T)\n\n// body\nAshes.IO.print(40 + 2)\n");
    }

    [Test]
    public void Format_should_return_null_for_invalid_source_with_imports()
    {
        const string source = "import Math\nif true then 1";

        var formatted = DocumentService.Format(source);

        formatted.ShouldBeNull();
    }

    [Test]
    public void GetSemanticTokens_should_report_token_positions_relative_to_original_source_with_import_header()
    {
        // "import Ashes.IO\n" is 16 chars; tokens in the body start on line 1.
        const string source = "import Ashes.IO\ntype Maybe =\n| None\n| Some(T)\nAshes.IO.print(1)";

        var tokens = DocumentService.GetSemanticTokens(source);

        // "Maybe" appears on line 1 (0-indexed) after the import header.
        tokens.ShouldContain(t => t.TokenType == DocumentService.TokenTypeType &&
                                  t.Line == 1 &&
                                  LspSemanticTokenTestHelpers.ExtractTokenText(source, t.Line, t.Character, t.Length) == "Maybe");
    }

    [Test]
    public void GetCompletions_should_include_imported_standard_library_constructors()
    {
        const string source = "import Ashes.Result\n0";

        var completions = DocumentService.GetCompletions(source);

        completions.ShouldContain("Ok");
        completions.ShouldContain("Error");
    }

    [Test]
    public void GetCompletions_should_return_root_module_members_after_Ashes_dot()
    {
        const string source = "Ashes.";

        var completions = DocumentService.GetCompletions(source, source.Length);

        completions.ShouldContain("IO");
        completions.ShouldContain("File");
        completions.ShouldContain("Http");
        completions.ShouldContain("Net");
        completions.ShouldContain("List");
    }

    [Test]
    public void GetCompletions_should_return_builtin_module_members_after_Ashes_IO_dot()
    {
        const string source = "Ashes.IO.";

        var completions = DocumentService.GetCompletions(source, source.Length);

        completions.ShouldContain("print");
        completions.ShouldContain("panic");
        completions.ShouldContain("args");
        completions.ShouldContain("readLine");
    }

    [Test]
    public void GetCompletions_should_return_imported_leaf_module_members()
    {
        const string source = "import Ashes.List\nList.";

        var completions = DocumentService.GetCompletions(source, source.Length);

        completions.ShouldContain("length");
        completions.ShouldContain("map");
        completions.ShouldContain("filter");
    }

    [Test]
    public void GetHover_should_return_type_for_let_binding_name()
    {
        const string source = "let id = fun (x) -> x in id(1)";

        var hover = DocumentService.GetHover(source, source.IndexOf("id", StringComparison.Ordinal));

        hover.ShouldNotBeNull();
        hover.Value.Start.ShouldBe(source.IndexOf("id", StringComparison.Ordinal));
        hover.Value.End.ShouldBe(source.IndexOf("id", StringComparison.Ordinal) + 2);
        hover.Value.Contents.ShouldBe("id : a -> a");
    }

    [Test]
    public void GetHover_should_return_type_for_lambda_parameter_name()
    {
        const string source = "let id = fun (x) -> x in id(1)";
        var parameterPosition = source.IndexOf("(x)", StringComparison.Ordinal) + 1;

        var hover = DocumentService.GetHover(source, parameterPosition);

        hover.ShouldNotBeNull();
        hover.Value.Start.ShouldBe(parameterPosition);
        hover.Value.End.ShouldBe(parameterPosition + 1);
        hover.Value.Contents.ShouldBe("x : a");
    }

    [Test]
    public void GetHover_should_return_type_for_call_expression()
    {
        const string source = "let id = fun (x) -> x in id(1)";
        var callStart = source.LastIndexOf("id(1)", StringComparison.Ordinal);
        var callEnd = callStart + "id(1)".Length;

        var hover = DocumentService.GetHover(source, callEnd - 1);

        hover.ShouldNotBeNull();
        hover.Value.Start.ShouldBe(callStart);
        hover.Value.End.ShouldBe(callEnd);
        hover.Value.Contents.ShouldBe("Int");
    }

    [Test]
    public void GetHover_should_return_type_for_float_literal()
    {
        const string source = "let x = 1.5 in x";
        var hover = DocumentService.GetHover(source, source.IndexOf("1.5", StringComparison.Ordinal) + 1);

        hover.ShouldNotBeNull();
        hover.Value.Contents.ShouldBe("Float");
    }

    [Test]
    public void GetHover_should_return_type_for_imported_standard_library_symbol()
    {
        const string source = "import Ashes.IO\nlet p = print in p(1)";
        var printPosition = source.IndexOf("print", StringComparison.Ordinal);

        var hover = DocumentService.GetHover(source, printPosition);

        hover.ShouldNotBeNull();
        hover.Value.Contents.ShouldBe("print : a -> Unit");
    }

    [Test]
    public void GetDefinition_should_return_local_let_binding_location()
    {
        var root = CreateTempProjectDirectory();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let x = 1 in x";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.LastIndexOf('x'), mainPath);

            definition.ShouldNotBeNull();
            definition.Value.FilePath.ShouldBe(mainPath);
            definition.Value.Start.ShouldBe(source.IndexOf("x", StringComparison.Ordinal));
            definition.Value.End.ShouldBe(source.IndexOf("x", StringComparison.Ordinal) + 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_return_lambda_parameter_location()
    {
        var root = CreateTempProjectDirectory();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "let id = fun (x) -> x in id(1)";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.LastIndexOf("x", StringComparison.Ordinal), mainPath);
            var parameterStart = source.IndexOf("(x)", StringComparison.Ordinal) + 1;

            definition.ShouldNotBeNull();
            definition.Value.FilePath.ShouldBe(mainPath);
            definition.Value.Start.ShouldBe(parameterStart);
            definition.Value.End.ShouldBe(parameterStart + 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_return_pattern_binding_location()
    {
        var root = CreateTempProjectDirectory();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "match [1] with | x :: xs -> x";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.LastIndexOf('x'), mainPath);
            var patternStart = source.IndexOf("x ::", StringComparison.Ordinal);

            definition.ShouldNotBeNull();
            definition.Value.FilePath.ShouldBe(mainPath);
            definition.Value.Start.ShouldBe(patternStart);
            definition.Value.End.ShouldBe(patternStart + 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_return_imported_module_value_location()
    {
        var root = CreateTempProjectDirectory("41");
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import Math\nAshes.IO.print(Math)";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.LastIndexOf("Math", StringComparison.Ordinal), mainPath);
            var mathPath = Path.Combine(root, "Math.ash");

            definition.ShouldNotBeNull();
            definition.Value.FilePath.ShouldBe(mathPath);
            definition.Value.Start.ShouldBe(0);
            definition.Value.End.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_return_qualified_imported_binding_location()
    {
        var root = CreateTempProjectDirectory("let add = fun (x) -> x + 1 in add");
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import Math\nAshes.IO.print(Math.add(1))";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.IndexOf("add", StringComparison.Ordinal), mainPath);
            var mathPath = Path.Combine(root, "Math.ash");

            definition.ShouldNotBeNull();
            definition.Value.FilePath.ShouldBe(mathPath);
            definition.Value.Start.ShouldBe(4);
            definition.Value.End.ShouldBe(7);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_return_leaf_qualified_imported_binding_location_for_multisegment_modules()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes_lsp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");
            Directory.CreateDirectory(Path.Combine(root, "M"));
            File.WriteAllText(Path.Combine(root, "M", "X.ash"), "let z = 1 in z");

            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import M.X\nAshes.IO.print(X.z)";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.IndexOf("z", StringComparison.Ordinal), mainPath);
            var modulePath = Path.Combine(root, "M", "X.ash");

            definition.ShouldNotBeNull();
            definition.Value.FilePath.ShouldBe(modulePath);
            definition.Value.Start.ShouldBe(4);
            definition.Value.End.ShouldBe(5);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetDefinition_should_return_unqualified_imported_binding_location()
    {
        var root = CreateTempProjectDirectory("let add = fun (x) -> x + 1 in add");
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import Math\nAshes.IO.print(add(1))";
            File.WriteAllText(mainPath, source);

            var definition = DocumentService.GetDefinition(source, source.IndexOf("add", StringComparison.Ordinal), mainPath);
            var mathPath = Path.Combine(root, "Math.ash");

            definition.ShouldNotBeNull();
            definition.Value.FilePath.ShouldBe(mathPath);
            definition.Value.Start.ShouldBe(4);
            definition.Value.End.ShouldBe(7);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsTokenWithText(string source, DocumentService.SemanticTokenItem token, int tokenType, string expectedText)
    {
        return token.TokenType == tokenType
               && token.Length == expectedText.Length
               && LspSemanticTokenTestHelpers.ExtractTokenText(source, token.Line, token.Character, token.Length) == expectedText;
    }

    private static string CreateTempProjectDirectory(string mathAshSource = "let add_one = fun (x) -> x + 1 in add_one")
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes_lsp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ashes.json"),
            """{"entry":"Main.ash","sourceRoots":["."]}""");
        File.WriteAllText(Path.Combine(root, "Math.ash"), mathAshSource);
        return root;
    }

    [Test]
    public void Analyze_with_project_context_should_not_report_errors_for_resolved_imports()
    {
        var root = CreateTempProjectDirectory();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            File.WriteAllText(mainPath, "import Math\nAshes.IO.print(Math(6))");

            var diagnostics = DocumentService.Analyze("import Math\nAshes.IO.print(Math(6))", mainPath);

            diagnostics.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Analyze_with_project_context_should_allow_imported_Ashes_IO_unqualified_names()
    {
        var root = CreateTempProjectDirectory();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import Ashes.IO\nprint(1)";
            File.WriteAllText(mainPath, source);

            var diagnostics = DocumentService.Analyze(source, mainPath);

            diagnostics.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Analyze_with_project_context_should_report_missing_module_errors_instead_of_fallback_semantic_errors()
    {
        var root = CreateTempProjectDirectory();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import Missing\nprint(1)";
            File.WriteAllText(mainPath, source);

            var diagnostics = DocumentService.Analyze(source, mainPath);

            diagnostics.Count.ShouldBe(1);
            diagnostics[0].Position.ShouldBe(0);
            diagnostics[0].Message.ShouldContain("Could not resolve module 'Missing'");
            diagnostics[0].Message.ShouldNotContain("Undefined variable");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void GetCompletions_with_project_context_should_include_entry_type_constructors()
    {
        // Even when a file has imports, type declarations in the entry body should be
        // available for completions. The entry body is parsed with ParseProgram so its
        // top-level type declarations are registered correctly.
        var root = CreateTempProjectDirectory();
        try
        {
            var mainPath = Path.Combine(root, "Main.ash");
            const string source = "import Math\ntype Color = | Red | Blue\nAshes.IO.print(Math(1))";
            File.WriteAllText(mainPath, source);

            var completions = DocumentService.GetCompletions(source, mainPath);

            completions.ShouldContain("Red");
            completions.ShouldContain("Blue");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
