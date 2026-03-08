using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Spectre.Console;

namespace Ashes.TestRunner;

public static class Runner
{
    public sealed record TestResult(string Path, bool Passed, string Expected, string Actual, int ExitCode, int ExpectedExitCode, bool HasExpected = true, long ElapsedMs = 0);

    public static int RunTests(IEnumerable<string> paths, string? targetId, IAnsiConsole console)
    {
        targetId ??= BackendFactory.DefaultForCurrentOS();

        var files = DiscoverAshFiles(paths.Any() ? paths : new[] { "tests" })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            console.MarkupLine("[yellow]No tests found.[/]");
            return 0;
        }

        var results = new List<TestResult>();

        foreach (var file in files)
        {
            var (expected, hasExpected, expectedExitCode, isCompileError, stdin) = TryReadExpected(file);
            if (!hasExpected)
            {
                results.Add(new TestResult(file, Passed: true, Expected: "", Actual: "", ExitCode: 0, ExpectedExitCode: 0, HasExpected: false));
                continue;
            }

            var sw = Stopwatch.StartNew();
            int exit;
            string actual;
            string stderr = "";
            try
            {
                var image = CompileFileToImage(file, targetId);
                var (runExit, stdout, runStderr) = RunImageCapture(image, targetId, stdin);
                exit = runExit;
                actual = (stdout ?? "").TrimEnd();
                stderr = runStderr ?? "";
            }
            catch (CompileDiagnosticException ex)
            {
                exit = 1;
                var isUnexpectedFailure = expectedExitCode != 1;
                actual = (isUnexpectedFailure || isCompileError)
                    ? DiagnosticTextRenderer.RenderCompilerDiagnostics(ex, source: null, displayPath: file).TrimEnd()
                    : "";
            }
            catch (InvalidOperationException ex)
            {
                exit = 1;
                var isUnexpectedFailure = expectedExitCode != 1;
                actual = (isUnexpectedFailure || isCompileError)
                    ? DiagnosticTextRenderer.RenderFailure("compile error", ex.Message ?? string.Empty, file).TrimEnd()
                    : "";
            }
            sw.Stop();

            var exp = expected.TrimEnd();
            var passed = exit == expectedExitCode && (isCompileError
                ? actual.Contains(exp, StringComparison.Ordinal)
                : actual == exp);

            // If stderr present, append for diagnostics in 'Actual' when the test fails
            if (!string.IsNullOrWhiteSpace(stderr) && !passed)
            {
                actual = actual + "\n[stderr]\n" + stderr.TrimEnd();
            }

            results.Add(new TestResult(file, passed, exp, actual, exit, expectedExitCode, ElapsedMs: sw.ElapsedMilliseconds));
        }

        RenderResults(results, console);

        return results.Any(r => !r.Passed && r.HasExpected) ? 1 : 0;
    }

    private static IEnumerable<string> DiscoverAshFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p) && p.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(p);
                continue;
            }

            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*.ash", SearchOption.AllDirectories))
                {
                    yield return Path.GetFullPath(f);
                }
            }
        }
    }

    private static (string Expected, bool HasExpected, int ExpectedExitCode, bool IsCompileError, string? Stdin) TryReadExpected(string path)
    {
        string expected = "";
        var hasExpected = false;
        var expectedExitCode = 0;
        var isCompileError = false;
        string? stdin = null;

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

            const string prefix = "// expect:";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                expected = trimmed.Substring(prefix.Length).Trim();
                hasExpected = true;
                isCompileError = false;
                continue;
            }

            const string compileErrorPrefix = "// expect-compile-error:";
            if (trimmed.StartsWith(compileErrorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                expected = trimmed.Substring(compileErrorPrefix.Length).Trim();
                hasExpected = true;
                expectedExitCode = 1;
                isCompileError = true;
                continue;
            }

            const string exitPrefix = "// exit:";
            if (trimmed.StartsWith(exitPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed.Substring(exitPrefix.Length).Trim(), out var parsedExitCode))
            {
                expectedExitCode = parsedExitCode;
                continue;
            }

            const string stdinPrefix = "// stdin:";
            if (trimmed.StartsWith(stdinPrefix, StringComparison.OrdinalIgnoreCase))
            {
                stdin = DecodeTestInput(trimmed.Substring(stdinPrefix.Length).Trim());
            }
        }

        return (expected, hasExpected, expectedExitCode, isCompileError, stdin);
    }

    private static string DecodeTestInput(string escaped)
    {
        var builder = new System.Text.StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            var ch = escaped[i];
            if (ch != '\\' || i == escaped.Length - 1)
            {
                builder.Append(ch);
                continue;
            }

            i++;
            builder.Append(escaped[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                _ => escaped[i]
            });
        }

        return builder.ToString();
    }

    private static readonly Regex ImportPattern = new(
        ProjectSupport.ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool HasImports(string filePath)
    {
        return File.ReadLines(filePath).Any(line => ImportPattern.IsMatch(line));
    }

    private static byte[] CompileFileToImage(string filePath, string targetId)
    {
        if (HasImports(filePath))
        {
            var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath))
                ?? throw new InvalidOperationException($"Cannot determine directory for: {filePath}");
            var project = new AshesProject(
                ProjectFilePath: filePath,
                ProjectDirectory: fileDir,
                EntryPath: filePath,
                EntryModuleName: Path.GetFileNameWithoutExtension(filePath),
                Name: null,
                SourceRoots: [fileDir],
                Include: [],
                OutDir: Path.GetTempPath(),
                Target: targetId
            );
            var plan = ProjectSupport.BuildCompilationPlan(project);
            var compilationSource = ProjectSupport.BuildCompilationSource(plan);
            return CompileToImage(compilationSource, targetId, plan.ImportedStdModules);
        }

        return CompileToImage(File.ReadAllText(filePath), targetId);
    }

    private static byte[] CompileToImage(string source, string targetId, IReadOnlySet<string>? importedStdModules = null)
    {
        var diag = new Diagnostics();
        var program = new Parser(StripLeadingCommentLines(source), diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules).Lower(program);
        diag.ThrowIfAny();

        var backend = BackendFactory.Create(targetId);
        return backend.Compile(ir);
    }

    private static string StripLeadingCommentLines(string source)
    {
        using var reader = new StringReader(source);
        var lines = new List<string>();
        var skipping = true;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (skipping)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                skipping = false;
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunImageCapture(byte[] image, string targetId, string? stdin = null)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes");
        Directory.CreateDirectory(tmpDir);

        var name = "ashes_test_" + Guid.NewGuid().ToString("N");
        var exePath = Path.Combine(tmpDir, targetId == TargetIds.WindowsX64 ? name + ".exe" : name);

        File.WriteAllBytes(exePath, image);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(exePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { }
        }

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    private static void RenderResults(List<TestResult> results, IAnsiConsole console)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey);
        table.AddColumn("[grey]Test[/]");
        table.AddColumn("[grey]Result[/]");
        table.AddColumn("[grey]Time[/]");

        int pass = 0, fail = 0, skip = 0;

        foreach (var r in results)
        {
            var name = Path.GetFileName(r.Path);
            if (!r.HasExpected)
            {
                skip++;
                table.AddRow(name, "[grey]SKIP (no // expect:)[/]", "[grey]—[/]");
                continue;
            }

            var time = FormatElapsed(r.ElapsedMs);
            if (r.Passed)
            {
                pass++;
                table.AddRow(name, "[green]PASS[/]", $"[grey]{time}[/]");
            }
            else
            {
                fail++;
                table.AddRow(name, "[red]FAIL[/]", $"[grey]{time}[/]");
            }
        }

        console.Write(table);
        console.WriteLine();

        var totalMs = results.Sum(r => r.ElapsedMs);
        console.MarkupLine($"[green]{pass} passed[/], [red]{fail} failed[/], [grey]{skip} skipped[/] in [bold]{FormatElapsed(totalMs)}[/]");

        if (fail > 0)
        {
            console.WriteLine();
            foreach (var r in results.Where(x => x.HasExpected && !x.Passed))
            {
                console.Write(new Rule(Path.GetFileName(r.Path)).RuleStyle("red").LeftJustified());
                console.MarkupLine($"[grey]Expected exit:[/] {r.ExpectedExitCode}");
                console.MarkupLine($"[grey]Actual exit:[/] {r.ExitCode}");
                console.MarkupLine("[grey]Expected:[/]");
                console.WriteLine(string.IsNullOrEmpty(r.Expected) ? "(empty)" : r.Expected);
                console.MarkupLine("[grey]Actual:[/]");
                console.WriteLine(string.IsNullOrEmpty(r.Actual) ? "(empty)" : r.Actual);
                console.WriteLine();
            }
        }
    }

    public static string FormatElapsed(long ms)
    {
        if (ms < 1000)
        {
            return $"{ms}ms";
        }

        var seconds = ms / 1000.0;
        return seconds < 60
            ? seconds.ToString("F2", CultureInfo.InvariantCulture) + "s"
            : (seconds / 60.0).ToString("F2", CultureInfo.InvariantCulture) + "min";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
        }

        return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }
}
