using Ashes.Frontend;
using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspDiagnosticsTests
{
    [Test]
    public async Task Diagnostics_should_be_published_when_an_invalid_document_is_opened()
    {
        var source = ReadFixture("unknown_identifier.ash");
        await using var document = TempDocument.Create("UnknownIdentifier.ash", source);
        await using var harness = await LspHarness.StartAsync();

        var published = await harness.DidOpenAsync(document.Uri, source);

        published.Uri.ShouldBe(document.Uri);
        published.Diagnostics.Count.ShouldBe(1);
        published.Diagnostics[0].GetProperty("severity").GetInt32().ShouldBe(1);
        published.Diagnostics[0].GetProperty("source").GetString().ShouldBe("Ashes");
        published.Diagnostics[0].GetProperty("code").GetString().ShouldBe(DiagnosticCodes.UnknownIdentifier);
        var message = published.Diagnostics[0].GetProperty("message").GetString();
        message.ShouldNotBeNull();
        message.ShouldContain("Undefined variable");

        var range = published.Diagnostics[0].GetProperty("range");
        range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(15);
        range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(20);
    }

    [Test]
    public async Task Diagnostics_should_clear_after_document_changes_fix_the_error()
    {
        var invalidSource = ReadFixture("unknown_identifier.ash");
        var validSource = ReadFixture("valid_program.ash");
        await using var document = TempDocument.Create("ChangeLifecycle.ash", invalidSource);
        await using var harness = await LspHarness.StartAsync();

        var openDiagnostics = await harness.DidOpenAsync(document.Uri, invalidSource);
        openDiagnostics.Diagnostics.Count.ShouldBeGreaterThan(0);

        var changedDiagnostics = await harness.DidChangeAsync(document.Uri, validSource);
        changedDiagnostics.Uri.ShouldBe(document.Uri);
        changedDiagnostics.Diagnostics.Count.ShouldBe(0);
    }

    [Test]
    public async Task Diagnostics_should_be_cleared_when_a_document_is_closed()
    {
        var source = ReadFixture("unknown_identifier.ash");
        await using var document = TempDocument.Create("CloseLifecycle.ash", source);
        await using var harness = await LspHarness.StartAsync();

        var openDiagnostics = await harness.DidOpenAsync(document.Uri, source);
        openDiagnostics.Diagnostics.Count.ShouldBeGreaterThan(0);

        var closeDiagnostics = await harness.DidCloseAsync(document.Uri);
        closeDiagnostics.Uri.ShouldBe(document.Uri);
        closeDiagnostics.Diagnostics.Count.ShouldBe(0);
    }

    [Test]
    public async Task Valid_documents_should_publish_an_empty_diagnostics_array()
    {
        var source = ReadFixture("valid_program.ash");
        await using var document = TempDocument.Create("ValidProgram.ash", source);
        await using var harness = await LspHarness.StartAsync();

        var published = await harness.DidOpenAsync(document.Uri, source);

        published.Uri.ShouldBe(document.Uri);
        published.Diagnostics.Count.ShouldBe(0);
    }

    [Test]
    public async Task Multiple_independent_errors_should_publish_multiple_diagnostics()
    {
        var source = ReadFixture("multiple_errors.ash");
        await using var document = TempDocument.Create("MultipleErrors.ash", source);
        await using var harness = await LspHarness.StartAsync();

        var published = await harness.DidOpenAsync(document.Uri, source);

        published.Uri.ShouldBe(document.Uri);
        published.Diagnostics.Count.ShouldBeGreaterThan(1);
        published.Diagnostics.All(d => d.GetProperty("severity").GetInt32() == 1).ShouldBeTrue();
        published.Diagnostics.All(d => d.GetProperty("source").GetString() == "Ashes").ShouldBeTrue();
    }

    [Test]
    public async Task Syntax_errors_should_publish_the_parser_span_range()
    {
        var source = ReadFixture("syntax_error.ash");
        await using var document = TempDocument.Create("SyntaxError.ash", source);
        await using var harness = await LspHarness.StartAsync();

        var published = await harness.DidOpenAsync(document.Uri, source);

        published.Uri.ShouldBe(document.Uri);
        published.Diagnostics.Count.ShouldBe(1);
        published.Diagnostics[0].GetProperty("code").GetString().ShouldBe(DiagnosticCodes.ParseError);
        var message = published.Diagnostics[0].GetProperty("message").GetString();
        message.ShouldNotBeNull();
        message.ShouldContain("Expected Else");

        var range = published.Diagnostics[0].GetProperty("range");
        range.GetProperty("start").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("start").GetProperty("character").GetInt32().ShouldBe(14);
        range.GetProperty("end").GetProperty("line").GetInt32().ShouldBe(0);
        range.GetProperty("end").GetProperty("character").GetInt32().ShouldBe(14);
    }

    [Test]
    public async Task Type_mismatch_diagnostics_should_publish_a_stable_code()
    {
        var source = ReadFixture("type_mismatch.ash");
        await using var document = TempDocument.Create("TypeMismatch.ash", source);
        await using var harness = await LspHarness.StartAsync();

        var published = await harness.DidOpenAsync(document.Uri, source);

        published.Uri.ShouldBe(document.Uri);
        published.Diagnostics.Count.ShouldBe(1);
        published.Diagnostics[0].GetProperty("code").GetString().ShouldBe(DiagnosticCodes.TypeMismatch);
    }

    private static string ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        File.Exists(path).ShouldBeTrue($"Expected fixture at '{path}'");
        return File.ReadAllText(path);
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
