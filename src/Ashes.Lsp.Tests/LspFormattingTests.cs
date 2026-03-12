using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspFormattingTests
{
    [Test]
    public async Task Formatting_should_return_single_full_document_edit()
    {
        const string source = "Ashes.IO.print(40+2)";
        await using var document = TempDocument.Create("Formatting.ash", source);
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.Uri, source);
        var edits = await harness.FormatAsync(document.Uri);

        edits.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Array);
        edits.GetArrayLength().ShouldBe(1);
        edits[0].GetProperty("newText").GetString().ShouldBe("Ashes.IO.print(40 + 2)\n");
    }

    [Test]
    public async Task Diagnostics_should_not_crash_for_empty_or_comment_only_documents()
    {
        await using var emptyDocument = TempDocument.Create("Empty.ash", string.Empty);
        await using var commentDocument = TempDocument.Create("Comment.ash", "// comment\n");
        await using var harness = await LspHarness.StartAsync();

        var emptyDiagnostics = await harness.DidOpenAsync(emptyDocument.Uri, string.Empty);
        var commentDiagnostics = await harness.DidOpenAsync(commentDocument.Uri, "// comment\n");

        emptyDiagnostics.Diagnostics.Count.ShouldBe(1);
        emptyDiagnostics.Diagnostics[0].GetProperty("code").GetString().ShouldBe("ASH003");
        commentDiagnostics.Diagnostics.Count.ShouldBe(1);
        commentDiagnostics.Diagnostics[0].GetProperty("code").GetString().ShouldBe("ASH003");
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