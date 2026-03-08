using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspHoverTests
{
    [Test]
    public async Task Hover_should_return_inferred_type_for_binding_name()
    {
        const string source = "let id = fun (x) -> x in id(1)";
        await using var document = TempDocument.Create("HoverBinding.ash", source);
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.Uri, source);
        var hover = await harness.HoverAsync(document.Uri, line: 0, character: source.IndexOf("id", StringComparison.Ordinal));

        hover.ShouldNotBeNull();
        hover.Value.GetProperty("contents").GetString().ShouldBe("id : a -> a");

        var range = hover.Value.GetProperty("range");
        range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(source.IndexOf("id", StringComparison.Ordinal));
        range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(source.IndexOf("id", StringComparison.Ordinal) + 2);
    }

    [Test]
    public async Task Hover_should_return_expression_type_for_call_result()
    {
        const string source = "let id = fun (x) -> x in id(1)";
        await using var document = TempDocument.Create("HoverExpression.ash", source);
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.Uri, source);
        var hover = await harness.HoverAsync(document.Uri, line: 0, character: source.LastIndexOf(')'));

        hover.ShouldNotBeNull();
        hover.Value.GetProperty("contents").GetString().ShouldBe("Int");

        var range = hover.Value.GetProperty("range");
        range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(source.LastIndexOf("id(1)", StringComparison.Ordinal));
        range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(source.LastIndexOf("id(1)", StringComparison.Ordinal) + "id(1)".Length);
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
