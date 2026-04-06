using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;
using TUnit.Core;

namespace Ashes.Tests;

public sealed class ProjectFixtureTests
{
    private static string GetProjectsRoot([CallerFilePath] string? callerFile = null)
    {
        var sourceDir = Path.GetDirectoryName(callerFile)!;
        return Path.GetFullPath(Path.Combine(sourceDir, "..", "..", "tests", "projects"));
    }

    public static IEnumerable<string> ProjectTestDirectories()
    {
        var root = GetProjectsRoot();
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var dir in Directory.GetDirectories(root).OrderBy(x => x))
        {
            yield return dir;
        }
    }

    [Test]
    [MethodDataSource(nameof(ProjectTestDirectories))]
    public async Task Project_fixture_test(string testDir)
    {
        var projectFile = Path.Combine(testDir, "ashes.json");
        var mainFile = Path.Combine(testDir, "Main.ash");

        File.Exists(projectFile).ShouldBeTrue($"ashes.json not found in {testDir}");
        File.Exists(mainFile).ShouldBeTrue($"Main.ash not found in {testDir}");

        var (expected, expectedCompileError) = ReadAnnotations(mainFile);
        var project = ProjectSupport.LoadProject(projectFile);

        if (expectedCompileError is not null)
        {
            var ex = Should.Throw<Exception>(() =>
            {
                var plan = ProjectSupport.BuildCompilationPlan(project);
                var combinedSource = ProjectSupport.BuildCompilationSource(plan);

                var diag = new Diagnostics();
                var ast = new Parser(combinedSource, diag).ParseProgram();
                diag.ThrowIfAny();

                _ = new Lowering(diag, plan.ImportedStdModules).Lower(ast);
                diag.ThrowIfAny();
            });

            ex.Message.ShouldContain(expectedCompileError);
            return;
        }

        expected.ShouldNotBeNull($"Project fixture in {testDir} must declare // expect: or // expect-compile-error:");

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
        var ast = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules).Lower(ast);
        diag.ThrowIfAny();

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-project-fixtures");
        Directory.CreateDirectory(tmpDir);

        string exePath;
        if (OperatingSystem.IsWindows())
        {
            var exeBytes = new Ashes.Backend.Backends.WindowsX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"project_{Guid.NewGuid():N}.exe");
            TestProcessHelper.WriteExecutable(exePath, exeBytes);
        }
        else
        {
            var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);
            exePath = Path.Combine(tmpDir, $"project_{Guid.NewGuid():N}");
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
        }

        try
        {
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
        finally
        {
            if (File.Exists(exePath))
            {
                try
                {
                    File.Delete(exePath);
                }
                catch
                {
                }
            }
        }
    }
}
