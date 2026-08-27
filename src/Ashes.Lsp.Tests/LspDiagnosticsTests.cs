using Ashes.Frontend;
using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspDiagnosticsTests
{
    [Test]
    public async Task Diagnostics_should_be_published_when_an_invalid_document_is_opened()
    {
        var source = ReadFixture("unknown_identifier.ash");
        var document = TempDocument.Create("UnknownIdentifier.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
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
        }
    }

    [Test]
    public async Task Diagnostics_should_clear_after_document_changes_fix_the_error()
    {
        var invalidSource = ReadFixture("unknown_identifier.ash");
        var validSource = ReadFixture("valid_program.ash");
        var document = TempDocument.Create("ChangeLifecycle.ash", invalidSource);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                var openDiagnostics = await harness.DidOpenAsync(document.Uri, invalidSource);
                openDiagnostics.Diagnostics.Count.ShouldBeGreaterThan(0);

                var changedDiagnostics = await harness.DidChangeAsync(document.Uri, validSource);
                changedDiagnostics.Uri.ShouldBe(document.Uri);
                changedDiagnostics.Diagnostics.Count.ShouldBe(0);
            }
        }
    }

    [Test]
    public async Task Diagnostics_should_be_cleared_when_a_document_is_closed()
    {
        var source = ReadFixture("unknown_identifier.ash");
        var document = TempDocument.Create("CloseLifecycle.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                var openDiagnostics = await harness.DidOpenAsync(document.Uri, source);
                openDiagnostics.Diagnostics.Count.ShouldBeGreaterThan(0);

                var closeDiagnostics = await harness.DidCloseAsync(document.Uri);
                closeDiagnostics.Uri.ShouldBe(document.Uri);
                closeDiagnostics.Diagnostics.Count.ShouldBe(0);
            }
        }
    }

    [Test]
    public async Task Valid_documents_should_publish_an_empty_diagnostics_array()
    {
        var source = ReadFixture("valid_program.ash");
        var document = TempDocument.Create("ValidProgram.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                var published = await harness.DidOpenAsync(document.Uri, source);

                published.Uri.ShouldBe(document.Uri);
                published.Diagnostics.Count.ShouldBe(0);
            }
        }
    }

    [Test]
    public async Task Multiple_independent_errors_should_publish_multiple_diagnostics()
    {
        var source = ReadFixture("multiple_errors.ash");
        var document = TempDocument.Create("MultipleErrors.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                var published = await harness.DidOpenAsync(document.Uri, source);

                published.Uri.ShouldBe(document.Uri);
                published.Diagnostics.Count.ShouldBeGreaterThan(1);
                published.Diagnostics.All(d => d.GetProperty("severity").GetInt32() == 1).ShouldBeTrue();
                published.Diagnostics.All(d => string.Equals(d.GetProperty("source").GetString(), "Ashes", StringComparison.Ordinal)).ShouldBeTrue();
            }
        }
    }

    [Test]
    public async Task Syntax_errors_should_publish_the_parser_span_range()
    {
        var source = ReadFixture("syntax_error.ash");
        var document = TempDocument.Create("SyntaxError.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
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
        }
    }

    [Test]
    public async Task Type_mismatch_diagnostics_should_publish_a_stable_code()
    {
        var source = ReadFixture("type_mismatch.ash");
        var document = TempDocument.Create("TypeMismatch.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                var published = await harness.DidOpenAsync(document.Uri, source);

                published.Uri.ShouldBe(document.Uri);
                published.Diagnostics.Count.ShouldBe(1);
                published.Diagnostics[0].GetProperty("code").GetString().ShouldBe(DiagnosticCodes.TypeMismatch);
            }
        }
    }

    [Test]
    public void Selfhost_frontend_files_analyze_cleanly_in_lsp()
    {
        var repoRoot = FindRepoRoot();
        var tokenPath = Path.Combine(repoRoot, "selfhost/packages/frontend/src/AshesCompiler/Frontend/Token.ash");
        var tokenSource = File.ReadAllText(tokenPath);
        var tokenDiags = DocumentService.Analyze(tokenSource, tokenPath);
        tokenDiags.ShouldBeEmpty();

        var importPath = Path.Combine(repoRoot, "selfhost/packages/frontend/src/AshesCompiler/Frontend/ImportResolution.ash");
        var importSource = File.ReadAllText(importPath);
        var importDiags = DocumentService.Analyze(importSource, importPath);
        importDiags.ShouldBeEmpty();

        var moduleSourcePath = Path.Combine(repoRoot, "selfhost/packages/frontend/src/AshesCompiler/Frontend/ModuleSource.ash");
        var moduleSourceText = File.ReadAllText(moduleSourcePath);
        var moduleSourceDiags = DocumentService.Analyze(moduleSourceText, moduleSourcePath);
        moduleSourceDiags.ShouldBeEmpty();

        // lib/Ashes/Text.ash has no ashes.json above it, so it analyzes in standalone mode with no
        // explicit imports of its own — a regression case for RequiresTraitEvidence: it defines
        // `join`, whose helper `reduce` relies on the polymorphic `+`/generalization Ashes.Trait
        // must be stitched in for, with no `trait`/`implement`/`requires`/`deriving` keyword anywhere
        // in the file to hint at that need.
        var textPath = Path.Combine(repoRoot, "lib/Ashes/Text.ash");
        var textDiags = DocumentService.Analyze(File.ReadAllText(textPath), textPath);
        textDiags.ShouldBeEmpty();

        // A project-mode regression case for the same underlying gap, from the other direction: this
        // file lives inside the "formatter" package, uses Ashes.Text.trimEnd/join/split by qualified
        // name with no import of its own, and only ever resolved by accident (via a sibling test
        // project's driver file happening to import Ashes.Text, made visible to every module sharing
        // its stitched scope) until Formatter.ash gained its own explicit import.
        var formatterPath = Path.Combine(repoRoot, "selfhost/packages/formatter/src/AshesCompiler/Formatter/Formatter.ash");
        var formatterDiags = DocumentService.Analyze(File.ReadAllText(formatterPath), formatterPath);
        formatterDiags.ShouldBeEmpty();
    }

    [Test]
    public void Standalone_analysis_stitches_trait_evidence_for_a_bare_operator_with_no_import_or_trait_keyword()
    {
        DocumentService.Analyze("1 + 2", "/tmp/PlainArithmetic.ash").ShouldBeEmpty();
        DocumentService.Analyze("\"a\" == \"b\"", "/tmp/PlainEquality.ash").ShouldBeEmpty();

        // Tokens deliberately excluded from the RequiresTraitEvidence heuristic (Star, Pipe) because
        // they are ambiguous without real parsing (pointer types, ADT constructor separators) must
        // still analyze cleanly on their own ordinary, non-operator meaning.
        DocumentService.Analyze("type Color = | Red | Green\nRed", "/tmp/PlainAdt.ash").ShouldBeEmpty();
        DocumentService.Analyze("external type Handle\nexternal inspect(*u8) -> Int\n0", "/tmp/PlainPointer.ash").ShouldBeEmpty();
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
