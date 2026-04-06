using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ashes.Semantics;
using Ashes.Frontend;
using Shouldly;
using TUnit.Core;

namespace Ashes.Tests;

public sealed class ImportTests
{
    private static string GetImportTestsRoot([CallerFilePath] string? callerFile = null)
    {
        var sourceDir = Path.GetDirectoryName(callerFile)!;
        return Path.GetFullPath(Path.Combine(sourceDir, "..", "..", "tests", "imports"));
    }

    public static IEnumerable<string> ImportTestDirectories()
    {
        var importsRoot = GetImportTestsRoot();
        if (!Directory.Exists(importsRoot))
        {
            yield break;
        }

        foreach (var dir in Directory.GetDirectories(importsRoot).OrderBy(x => x))
        {
            yield return dir;
        }
    }

    [Test]
    [MethodDataSource(nameof(ImportTestDirectories))]
    public async Task Import_test(string testDir)
    {
        var mainFile = Path.Combine(testDir, "Main.ash");
        File.Exists(mainFile).ShouldBeTrue($"Main.ash not found in {testDir}");

        var (expected, expectedCompileError) = ReadAnnotations(mainFile);

        var project = new AshesProject(
            ProjectFilePath: Path.Combine(testDir, "ashes.json"),
            ProjectDirectory: testDir,
            EntryPath: mainFile,
            EntryModuleName: "Main",
            Name: null,
            SourceRoots: [testDir],
            Include: [],
            OutDir: Path.Combine(testDir, "out"),
            Target: null);

        if (expectedCompileError is not null)
        {
            var ex = Should.Throw<InvalidOperationException>(() =>
            {
                var plan = ProjectSupport.BuildCompilationPlan(project);
                ProjectSupport.BuildCompilationSource(plan);
            });
            ex.Message.ShouldContain(expectedCompileError);
            return;
        }

        expected.ShouldNotBeNull($"Test in {testDir} must have a // expect: or // expect-compile-error: annotation");

        var compilationPlan = ProjectSupport.BuildCompilationPlan(project);
        var combinedSource = ProjectSupport.BuildCompilationSource(compilationPlan);
        var stdout = await CompileRunCaptureAsync(combinedSource, compilationPlan.ImportedStdModules);
        stdout.TrimEnd().ShouldBe(expected);
    }

    private static (string? Expected, string? ExpectedCompileError) ReadAnnotations(string path)
    {
        string? expected = null;
        string? compileError = null;

        using var sr = new StreamReader(path);
        while (!sr.EndOfStream)
        {
            var line = sr.ReadLine() ?? "";
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            const string expectPrefix = "// expect: ";
            if (trimmed.StartsWith(expectPrefix, StringComparison.OrdinalIgnoreCase))
            {
                expected = trimmed[expectPrefix.Length..].Trim();
                continue;
            }

            const string errorPrefix = "// expect-compile-error: ";
            if (trimmed.StartsWith(errorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                compileError = trimmed[errorPrefix.Length..].Trim();
            }
        }

        return (expected, compileError);
    }

    private static async Task<string> CompileRunCaptureAsync(string source, IReadOnlySet<string>? importedStdModules = null)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules).Lower(ast);
        diag.ThrowIfAny();

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-import-tests");
        Directory.CreateDirectory(tmpDir);

        string exePath;
        if (OperatingSystem.IsWindows())
        {
            var exeBytes = new Ashes.Backend.Backends.WindowsX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"import_{Guid.NewGuid():N}.exe");
            TestProcessHelper.WriteExecutable(exePath, exeBytes);
        }
        else
        {
            var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"import_{Guid.NewGuid():N}");
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
        }

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi);;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
