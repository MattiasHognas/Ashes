using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspCompletionTests
{
    [Test]
    public async Task Completion_should_return_root_module_members_after_Ashes_dot()
    {
        const string source = "Ashes.";
        var document = TempDocument.Create("CompletionRoot.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("IO");
                completions.ShouldContain("Net");
                completions.ShouldContain("Collection");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_stderr_and_exit_intrinsics()
    {
        const string source = "Ashes.IO.";
        var document = TempDocument.Create("CompletionProcessControl.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("writeError");
                completions.ShouldContain("writeErrorLine");
                completions.ShouldContain("exit");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_local_bindings_in_scope()
    {
        const string source = "let value = 1 in let next = value + 1 in ne";
        var document = TempDocument.Create("CompletionLocal.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("value");
                completions.ShouldContain("next");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_foreign_memory_intrinsics()
    {
        const string source = "Ashes.Ffi.";
        var document = TempDocument.Create("CompletionFfi.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("copyBytes");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_immutable_binary_construction_intrinsics()
    {
        const string source = "Ashes.Byte.";
        var document = TempDocument.Create("CompletionBytes.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("allocate");
                completions.ShouldContain("copyRange");
                completions.ShouldContain("set");
                completions.ShouldContain("setU16Le");
                completions.ShouldContain("setU32Le");
                completions.ShouldContain("setU64Le");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_path_module_exports()
    {
        const string source = "Ashes.IO.Path.";
        var document = TempDocument.Create("CompletionPath.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("Unix");
                completions.ShouldContain("Windows");
                completions.ShouldContain("normalize");
                completions.ShouldContain("join");
                completions.ShouldContain("parent");
                completions.ShouldContain("basename");
                completions.ShouldContain("extension");
                completions.ShouldContain("relativeTo");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_environment_module_intrinsics()
    {
        const string source = "Ashes.IO.Environment.";
        var document = TempDocument.Create("CompletionEnvironment.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("currentDirectory");
                completions.ShouldContain("executableDirectory");
                completions.ShouldContain("temporaryDirectory");
                completions.ShouldContain("cacheDirectory");
                completions.ShouldContain("get");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_directory_module_intrinsics()
    {
        const string source = "Ashes.IO.Directory.";
        var document = TempDocument.Create("CompletionDirectory.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("entries");
                completions.ShouldContain("createAll");
                completions.ShouldContain("removeTree");
            }
        }
    }

    [Test]
    public async Task Completion_should_return_file_replacement_intrinsic()
    {
        const string source = "Ashes.IO.File.";
        var document = TempDocument.Create("CompletionFile.ash", source);
        await using (document.ConfigureAwait(false))
        {
            var harness = await LspHarness.StartAsync().ConfigureAwait(false);
            await using (harness.ConfigureAwait(false))
            {
                _ = await harness.DidOpenAsync(document.Uri, source);
                var completions = await harness.CompletionAsync(document.Uri, 0, source.Length);

                completions.ShouldContain("replace");
                completions.ShouldContain("makeExecutable");
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
