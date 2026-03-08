using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspDefinitionTests
{
    [Test]
    public async Task Definition_should_return_local_binding_location()
    {
        const string source = "let x = 1 in x";
        await using var document = TempProjectDocument.Create(
            "DefinitionLocal",
            ("Main.ash", source));
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.MainUri, source);
        var definition = await harness.DefinitionAsync(document.MainUri, 0, source.LastIndexOf('x'));

        definition.ShouldNotBeNull();
        definition.Value.GetProperty("uri").GetString().ShouldBe(document.MainUri);

        var range = definition.Value.GetProperty("range");
        range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(source.IndexOf("x", StringComparison.Ordinal));
        range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(source.IndexOf("x", StringComparison.Ordinal) + 1);
    }

    [Test]
    public async Task Definition_should_return_imported_module_binding_location()
    {
        const string source = "import Math\nAshes.IO.print(Math.add(1))";
        await using var document = TempProjectDocument.Create(
            "DefinitionImported",
            ("Main.ash", source),
            ("Math.ash", "let add = fun (x) -> x + 1 in add"));
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.MainUri, source);
        var definition = await harness.DefinitionAsync(document.MainUri, 1, "Ashes.IO.print(".Length + "Math.".Length);

        definition.ShouldNotBeNull();
        definition.Value.GetProperty("uri").GetString().ShouldBe(document.GetUri("Math.ash"));

        var range = definition.Value.GetProperty("range");
        range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(4);
        range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task Definition_should_return_let_result_binding_location()
    {
        const string source = "type Result(E, A) = | Ok(A) | Error(E)\nlet x =\n    let? value = Ok(1)\n    in Ok(value)\nin x";
        var lines = source.Split('\n');
        await using var document = TempProjectDocument.Create(
            "DefinitionLetResult",
            ("Main.ash", source));
        await using var harness = await LspHarness.StartAsync();

        _ = await harness.DidOpenAsync(document.MainUri, source);
        var definition = await harness.DefinitionAsync(document.MainUri, 3, lines[3].LastIndexOf("value", StringComparison.Ordinal));

        definition.ShouldNotBeNull();
        definition.Value.GetProperty("uri").GetString().ShouldBe(document.MainUri);

        var expectedStart = lines[2].IndexOf("value", StringComparison.Ordinal);
        var range = definition.Value.GetProperty("range");
        range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(2);
        range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(expectedStart);
        range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(2);
        range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(expectedStart + "value".Length);
    }

    private sealed class TempProjectDocument : IAsyncDisposable
    {
        private readonly string _directory;

        private TempProjectDocument(string directory, string mainFilePath)
        {
            _directory = directory;
            MainFilePath = mainFilePath;
            MainUri = new Uri(mainFilePath).AbsoluteUri;
        }

        public string MainFilePath { get; }

        public string MainUri { get; }

        public static TempProjectDocument Create(string projectName, params (string FileName, string Content)[] files)
        {
            var directory = Path.Combine(Path.GetTempPath(), "ashes-lsp-tests", projectName + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "ashes.json"), """{"entry":"Main.ash","sourceRoots":["."]}""");

            string? mainFilePath = null;
            foreach (var (fileName, content) in files)
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

        public string GetUri(string fileName)
        {
            return new Uri(Path.Combine(_directory, fileName)).AbsoluteUri;
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
