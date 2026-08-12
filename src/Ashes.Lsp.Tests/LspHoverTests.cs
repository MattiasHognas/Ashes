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
}
