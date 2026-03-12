using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspCompletionTests
{
    [Test]
    public async Task Completion_should_return_root_module_members_after_Ashes_dot()
    {
        const string source = "Ashes.";
        await using var document = TempDocument.Create("CompletionRoot.ash", source);
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.Uri, source);
        var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

        completions.ShouldContain("IO");
        completions.ShouldContain("Http");
        completions.ShouldContain("List");
    }

    [Test]
    public async Task Completion_should_return_local_bindings_in_scope()
    {
        const string source = "let value = 1 in let next = value + 1 in ne";
        await using var document = TempDocument.Create("CompletionLocal.ash", source);
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.Uri, source);
        var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

        completions.ShouldContain("value");
        completions.ShouldContain("next");
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
