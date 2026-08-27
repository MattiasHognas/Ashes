using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspHoverTests
{
    [Test]
    public void Embedded_documentation_catalog_reuses_standard_library_markdown()
    {
        StandardLibraryDocumentation.TryGet(
            "Ashes.Collection.List.map",
            out StandardLibraryDocumentation.Entry map).ShouldBeTrue();
        map.Summary.ShouldBe("Apply `f` to each element.");
        map.Url.ShouldEndWith("standard-library#ashes-collection-list");

        StandardLibraryDocumentation.TryGet(
            "Ashes.IO.print",
            out StandardLibraryDocumentation.Entry print).ShouldBeTrue();
        print.Summary.ShouldContain("Write a printable scalar");
        StandardLibraryDocumentation.TryGet("Ashes.IO.Unit", out _).ShouldBeFalse();

        StandardLibraryDocumentation.TryGet(
            "Ashes.IO.Path.normalize",
            out StandardLibraryDocumentation.Entry normalize).ShouldBeTrue();
        normalize.Summary.ShouldContain("Collapse repeated separators");
        normalize.Url.ShouldEndWith("standard-library#ashes-io-path");
    }

    [Test]
    public async Task Hover_should_document_path_module_functions()
    {
        const string source = "import Ashes.IO.Path\n\nAshes.IO.Path.normalize(Ashes.IO.Path.Unix)(\"a/../b\")";
        var document = TempDocument.Create("HoverPath.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(
                    document.Uri,
                    line: 2,
                    character: "Ashes.IO.Path.".Length);

                hover.ShouldNotBeNull();
                string markdown = hover.Value
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString()!;
                markdown.ShouldContain("Ashes.IO.Path.normalize");
                markdown.ShouldContain("Collapse repeated separators");
                markdown.ShouldContain("standard-library#ashes-io-path");
            }
        }
    }

    [Test]
    public async Task Hover_should_return_inferred_type_for_binding_name()
    {
        const string source = "let id = given (x) -> x in id(1)";
        var document = TempDocument.Create("HoverBinding.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(document.Uri, line: 0, character: source.IndexOf("id", StringComparison.Ordinal));

                hover.ShouldNotBeNull();
                var contents = hover.Value.GetProperty("contents");
                contents.GetProperty("kind").GetString().ShouldBe("markdown");
                contents.GetProperty("value").GetString().ShouldBe(
                    "```ashes\nid : a -> a\n```\n\n*function*\n\n**Parameters**\n\n- `x` : `a`\n\n**Returns:** `a`");

                var range = hover.Value.GetProperty("range");
                range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
                range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(source.IndexOf("id", StringComparison.Ordinal));
                range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
                range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(source.IndexOf("id", StringComparison.Ordinal) + 2);
            }
        }
    }

    [Test]
    public async Task Hover_should_return_expression_type_for_call_result()
    {
        const string source = "let id = given (x) -> x in id(1)";
        var document = TempDocument.Create("HoverExpression.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(document.Uri, line: 0, character: source.LastIndexOf(')'));

                hover.ShouldNotBeNull();
                var contents = hover.Value.GetProperty("contents");
                contents.GetProperty("kind").GetString().ShouldBe("markdown");
                contents.GetProperty("value").GetString().ShouldBe("```ashes\nInt\n```\n\n*expression*");

                var range = hover.Value.GetProperty("range");
                range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
                range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(source.LastIndexOf("id(1)", StringComparison.Ordinal));
                range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
                range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(source.LastIndexOf("id(1)", StringComparison.Ordinal) + "id(1)".Length);
            }
        }
    }

    [Test]
    public async Task Hover_should_show_embedded_standard_library_documentation_and_link()
    {
        const string source = "import Ashes.Collection.List as list\n\nlist.map(given (x) -> x + 1)([1])";
        var document = TempDocument.Create("HoverStandardLibrary.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(
                    document.Uri,
                    line: 2,
                    character: "list.".Length);

                hover.ShouldNotBeNull();
                var contents = hover.Value.GetProperty("contents");
                contents.GetProperty("kind").GetString().ShouldBe("markdown");
                string markdown = contents.GetProperty("value").GetString()!;
                markdown.ShouldContain("Ashes.Collection.List.map : (Int -> Int) -> List<Int> -> List<Int>");
                markdown.ShouldContain("- `f` : `Int -> Int`");
                markdown.ShouldContain("**Returns:** `List<Int> -> List<Int>`");
                markdown.ShouldContain("Apply `f` to each element.");
                markdown.ShouldContain("standard-library#ashes-collection-list");
            }
        }
    }

    [Test]
    public async Task Hover_should_document_unqualified_compiler_intrinsics()
    {
        const string source = "import Ashes.IO\n\nprint(1)";
        var document = TempDocument.Create("HoverIntrinsic.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(document.Uri, line: 2, character: 0);

                hover.ShouldNotBeNull();
                string markdown = hover.Value
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString()!;
                markdown.ShouldContain("Ashes.IO.print : a -> Unit needs {ConsoleIO}");
                markdown.ShouldContain("Write a printable scalar");
                markdown.ShouldContain("standard-library#ashes-io");
            }
        }
    }

    [Test]
    public async Task Hover_should_document_qualified_intrinsic_call_targets()
    {
        const string source = "let getOrDefault res def =\n    match res with\n        | Ok(x) -> x\n        | Error(_) -> def\nin Ashes.IO.print(getOrDefault(Ok(1))(0))";
        var document = TempDocument.Create("HoverQualifiedIntrinsic.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(
                    document.Uri,
                    line: 4,
                    character: "in Ashes.IO.".Length);

                hover.ShouldNotBeNull();
                string markdown = hover.Value
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString()!;
                markdown.ShouldContain("Ashes.IO.print : a -> Unit needs {ConsoleIO}");
                markdown.ShouldContain("Write a printable scalar");
                markdown.ShouldContain("standard-library#ashes-io");
            }
        }
    }

    [Test]
    public async Task Hover_should_document_qualified_print_in_adt_function_fixture()
    {
        const string source = "// expect: 44\nimport Ashes.IO\nimport Ashes.Text\ntype Callback =\n    | Callback(Int -> Int, Int)\n\ntype Boxed =\n    | Boxed(List(Int), Str)\n\nlet apply cb =\n    match cb with\n        | Callback(f, x) -> f(x)\n\nlet describe b =\n    match b with\n        | Boxed(_xs, label) -> label\n\nlet r =\n    apply(Callback(given (n) -> n * 3)(14))\nin Ashes.IO.print(r + Ashes.Text.byteLength(describe(Boxed(1 :: 2 :: [])(\"hi\"))))";
        var document = TempDocument.Create("adt_function_typed_fields.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(
                    document.Uri,
                    line: 19,
                    character: "in Ashes.IO.".Length);

                hover.ShouldNotBeNull();
                string markdown = hover.Value
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString()!;
                markdown.ShouldContain("Ashes.IO.print : a -> Unit");

                var byteLengthHover = await harness.HoverAsync(
                    document.Uri,
                    line: 19,
                    character: "in Ashes.IO.print(r + Ashes.Text.".Length);
                byteLengthHover.ShouldNotBeNull();
                string byteLengthMarkdown = byteLengthHover.Value
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString()!;
                byteLengthMarkdown.ShouldContain("Ashes.Text.byteLength : Str -> Int");
            }
        }
    }

    [Test]
    public async Task Hover_should_show_declared_parameter_names_and_inferred_types_at_references()
    {
        const string source = "let banana (transform: Str -> Str) (text: Str) (count: u8) = transform(text) :: [] in banana";
        var document = TempDocument.Create("HoverFunctionParameters.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var hover = await harness.HoverAsync(
                    document.Uri,
                    line: 0,
                    character: source.LastIndexOf("banana", StringComparison.Ordinal));

                hover.ShouldNotBeNull();
                string markdown = hover.Value
                    .GetProperty("contents")
                    .GetProperty("value")
                    .GetString()!;
                markdown.ShouldContain("banana : (Str -> Str) -> Str -> u8 -> List<Str>");
                markdown.ShouldContain("- `transform` : `Str -> Str`");
                markdown.ShouldContain("- `text` : `Str`");
                markdown.ShouldContain("- `count` : `u8`");
                markdown.ShouldContain("**Returns:** `List<Str>`");
            }
        }
    }

    [Test]
    public void Selfhost_frontend_files_hover_cleanly_in_lsp()
    {
        var repoRoot = FindRepoRoot();
        var tokenPath = Path.Combine(repoRoot, "selfhost/packages/frontend/src/AshesCompiler/Frontend/Token.ash");
        var tokenSource = File.ReadAllText(tokenPath);
        int gtPos = tokenSource.IndexOf("left > right", StringComparison.Ordinal) + 5;
        var tokenHover = DocumentService.GetHover(tokenSource, gtPos, tokenPath);
        tokenHover.ShouldNotBeNull();
        tokenHover.Value.Contents.ShouldNotContain("ASH010");
        tokenHover.Value.Contents.ShouldNotContain("ambiguous");

        var importPath = Path.Combine(repoRoot, "selfhost/packages/frontend/src/AshesCompiler/Frontend/ImportResolution.ash");
        var importSource = File.ReadAllText(importPath);
        int neqPos = importSource.IndexOf("candidateModule != existingModule", StringComparison.Ordinal) + 16;
        var importHover = DocumentService.GetHover(importSource, neqPos, importPath);
        importHover.ShouldNotBeNull();
        importHover.Value.Contents.ShouldNotContain("ASH010");
        importHover.Value.Contents.ShouldNotContain("ambiguous");

        var moduleSourcePath = Path.Combine(repoRoot, "selfhost/packages/frontend/src/AshesCompiler/Frontend/ModuleSource.ash");
        var moduleSourceText = File.ReadAllText(moduleSourcePath);
        int exportPos = moduleSourceText.IndexOf("export (", StringComparison.Ordinal);
        var exportHover = DocumentService.GetHover(moduleSourceText, exportPos, moduleSourcePath);
        if (exportHover is not null)
        {
            exportHover.Value.Contents.ShouldNotContain("must be called directly");
        }
    }

    [Test]
    public void Hover_reuses_cached_project_plan_but_still_reflects_a_changed_dependency()
    {
        var document = TempProjectDocument.Create(
            "HoverPlanCache",
            ("Main.ash", "import Helper\nHelper.value"),
            ("Helper.ash", "let value = 1"));
        try
        {
            var mainSource = File.ReadAllText(document.MainFilePath);
            int position = mainSource.LastIndexOf("value", StringComparison.Ordinal);

            var first = DocumentService.GetHover(mainSource, position, document.MainFilePath);
            first.ShouldNotBeNull();
            first.Value.Contents.ShouldContain("Int");

            // Same file, same position, nothing on disk changed: must still be served correctly
            // whether or not the cached ProjectCompilationPlan was reused.
            var second = DocumentService.GetHover(mainSource, position, document.MainFilePath);
            second.ShouldNotBeNull();
            second.Value.Contents.ShouldBe(first.Value.Contents);

            // Change the dependency Helper.ash relies on and force its mtime forward — a coarse
            // filesystem mtime clock could otherwise leave it indistinguishable from the original
            // write within the same test run. The cached plan must be invalidated and rebuilt so
            // this shows Str, not the stale cached Int.
            var helperPath = Path.Combine(document.Directory, "Helper.ash");
            File.WriteAllText(helperPath, "let value = \"hi\"");
            File.SetLastWriteTimeUtc(helperPath, DateTime.UtcNow.AddSeconds(5));

            var third = DocumentService.GetHover(mainSource, position, document.MainFilePath);
            third.ShouldNotBeNull();
            third.Value.Contents.ShouldContain("Str");
            third.Value.Contents.ShouldNotContain("Int");
        }
        finally
        {
            Directory.Delete(document.Directory, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ashes.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? Directory.GetCurrentDirectory();
    }

    private sealed class TempDocument : IAsyncDisposable
    {
        private readonly string _directory;

        private TempDocument(string directory, string filePath)
        {
            _directory = directory;
            FilePath = filePath;
            Uri = new Uri(filePath).AbsoluteUri;
        }

        public string FilePath { get; }

        public string Uri { get; }

        public static TempDocument Create(string fileName, string source)
        {
            var directory = Path.Combine(Path.GetTempPath(), "ashes-lsp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory, fileName);
            // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine -- test-only helper; path is a fresh per-test temp dir plus a test-controlled fileName, never user input
            File.WriteAllText(filePath, source);
            return new TempDocument(directory, filePath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TempProjectDocument
    {
        private TempProjectDocument(string directory, string mainFilePath)
        {
            Directory = directory;
            MainFilePath = mainFilePath;
        }

        public string Directory { get; }

        public string MainFilePath { get; }

        public static TempProjectDocument Create(string projectName, params (string FileName, string Content)[] files)
        {
            var directory = Path.Combine(Path.GetTempPath(), "ashes-lsp-tests", projectName + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");

            string? mainFilePath = null;
            foreach ((string fileName, string content) in files)
            {
                var filePath = Path.Combine(directory, fileName);
                File.WriteAllText(filePath, content);
                if (string.Equals(fileName, "Main.ash", StringComparison.OrdinalIgnoreCase))
                {
                    mainFilePath = filePath;
                }
            }

            if (mainFilePath is null)
            {
                throw new InvalidOperationException("Main.ash is required.");
            }

            return new TempProjectDocument(directory, mainFilePath);
        }
    }
}
