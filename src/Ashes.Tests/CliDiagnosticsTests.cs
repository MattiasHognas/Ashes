using System.Diagnostics;
using System.Text.RegularExpressions;
using Ashes.Frontend;
using Ashes.Lsp;
using Shouldly;

namespace Ashes.Tests;

public sealed class CliDiagnosticsTests
{
    [Test]
    public async Task Compile_should_render_parse_errors_consistently()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "parse_error.ash");
            await File.WriteAllTextAsync(filePath, "let x =\n");

            var result = await RunCliAsync("compile", filePath);

            result.ExitCode.ShouldBe(1);
            result.Output.ShouldContain(Path.GetFileName(filePath));
            result.Output.ShouldContain(":1:8");
            result.Output.ShouldContain(DiagnosticCodes.ParseError);
            result.Output.ShouldContain("Expected");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Compile_should_render_type_errors_consistently()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "type_error.ash");
            await File.WriteAllTextAsync(filePath, "Ashes.IO.print(1 + true)\n");

            var result = await RunCliAsync("compile", filePath);

            result.ExitCode.ShouldBe(1);
            result.Output.ShouldContain(Path.GetFileName(filePath));
            result.Output.ShouldContain(":1:16");
            result.Output.ShouldContain(DiagnosticCodes.TypeMismatch);
            result.Output.ShouldContain("'+' requires Int+Int, Float+Float, or Str+Str");
            result.Output.ShouldContain("got Int and");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Compile_should_render_project_import_errors_consistently()
    {
        var tempDir = CreateTempDir();
        try
        {
            var projectPath = Path.Combine(tempDir, "ashes.json");
            var mainPath = Path.Combine(tempDir, "Main.ash");

            await File.WriteAllTextAsync(projectPath, "{\n  \"entry\": \"Main.ash\"\n}\n");
            await File.WriteAllTextAsync(mainPath, "import Missing\nAshes.IO.print(1)\n");

            var result = await RunCliAsync("compile", "--project", projectPath);

            result.ExitCode.ShouldBe(1);
            result.Output.ShouldContain("compile error:");
            result.Output.ShouldContain("Main.ash");
            result.Output.ShouldContain("Could not resolve module 'Missing'");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Run_should_propagate_non_zero_exit_code_without_cli_runtime_wrapper()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "panic.ash");
            await File.WriteAllTextAsync(filePath, "Ashes.IO.panic(\"boom\")\n");

            var result = await RunCliAsync("run", filePath);

            result.ExitCode.ShouldBe(1);
            result.Stdout.ShouldContain("boom");
            result.Output.ShouldNotContain("runtime error: process exited with code");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Run_should_pass_stdin_through_to_compiled_program()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "stdin.ash");
            await File.WriteAllTextAsync(filePath, "match Ashes.IO.readLine() with | None -> Ashes.IO.print(\"none\") | Some(text) -> Ashes.IO.print(text)\n");

            var result = await RunCliAsync(["run", filePath], stdin: "hello\n");

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldContain("hello");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Run_should_stream_stdout_without_pollution()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "stdout.ash");
            await File.WriteAllTextAsync(filePath, "Ashes.IO.writeLine(\"hello\")\n");

            var result = await RunCliAsync("run", filePath);

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldBe("hello\n");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Run_should_keep_stdout_clean_when_program_fails()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "panic_clean.ash");
            await File.WriteAllTextAsync(filePath, "Ashes.IO.panic(\"boom\")\n");

            var result = await RunCliAsync("run", filePath);

            result.ExitCode.ShouldBe(1);
            result.Stdout.ShouldBe("boom\n");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Run_should_forward_program_arguments_without_polluting_output()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "args.ash");
            await File.WriteAllTextAsync(filePath, "match Ashes.IO.args with | x :: y :: [] -> Ashes.IO.writeLine(x + \":\" + y) | _ -> Ashes.IO.writeLine(\"bad\")\n");

            var result = await RunCliAsync("run", filePath, "--", "first", "second");

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldBe("first:second\n");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Run_should_not_write_cli_banners_to_stdout()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "quiet.ash");
            await File.WriteAllTextAsync(filePath, "Ashes.IO.writeLine(\"ok\")\n");

            var result = await RunCliAsync("run", filePath);

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldBe("ok\n");
            result.Output.ShouldNotContain("Wrote ");
            result.Output.ShouldNotContain("Target:");
            result.Output.ShouldNotContain("Time:");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Test_should_render_output_mismatches_consistently()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "mismatch.ash");
            await File.WriteAllTextAsync(filePath, "// expect: 2\nAshes.IO.print(1)\n");

            var result = await RunCliAsync("test", filePath);

            result.ExitCode.ShouldBe(1);
            result.Output.ShouldContain("FAIL");
            result.Output.ShouldContain("Expected exit:");
            result.Output.ShouldContain("Actual exit:");
            result.Output.ShouldContain("Expected:");
            result.Output.ShouldContain("Actual:");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Test_should_support_expected_runtime_failures_with_exit_annotations()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "runtime_expected.ash");
            await File.WriteAllTextAsync(filePath, "// exit: 1\n// expect: boom\nAshes.IO.panic(\"boom\")\n");

            var result = await RunCliAsync("test", filePath);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("PASS");
            result.Output.ShouldContain("1 passed");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Compile_should_match_lsp_diagnostic_code_message_and_span()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "unknown_identifier.ash");
            var source = "Ashes.IO.print(value)\n";
            await File.WriteAllTextAsync(filePath, source);

            var result = await RunCliAsync("compile", filePath);
            var lspDiagnostic = DocumentService.Analyze(source, filePath).ShouldHaveSingleItem();
            var location = GetLocation(source, lspDiagnostic.Start);
            var expectedUnderline = Math.Max(lspDiagnostic.End - lspDiagnostic.Start, 1);
            var diagnosticCode = lspDiagnostic.Code;
            var normalizedOutput = NormalizeWhitespace(result.Output);
            var renderedSnippet = ParseRenderedSnippet(result.Output);

            result.ExitCode.ShouldBe(1);
            renderedSnippet.Line.ShouldBe(location.Line);
            renderedSnippet.Column.ShouldBe(location.Column);
            renderedSnippet.UnderlineLength.ShouldBe(expectedUnderline);
            renderedSnippet.SourceLine.ShouldBe("Ashes.IO.print(value)");
            diagnosticCode.ShouldNotBeNull();
            normalizedOutput.ShouldContain(diagnosticCode);
            normalizedOutput.ShouldContain(lspDiagnostic.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static (int Line, int Column) GetLocation(string source, int position)
    {
        var line = 1;
        var column = 1;

        for (var i = 0; i < position && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
                continue;
            }

            if (source[i] != '\r')
            {
                column++;
            }
        }

        return (line, column);
    }

    private static string NormalizeWhitespace(string text)
    {
        return Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static RenderedSnippet ParseRenderedSnippet(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (int i = 0; i < lines.Length - 1; i++)
        {
            var sourceMatch = Regex.Match(lines[i], @"^\s*(\d+)\s\|\s(.*)$", RegexOptions.CultureInvariant);
            if (!sourceMatch.Success)
            {
                continue;
            }

            var underlineMatch = Regex.Match(lines[i + 1], @"^\s*\|\s(\s*)(\^+)$", RegexOptions.CultureInvariant);
            if (!underlineMatch.Success)
            {
                continue;
            }

            return new RenderedSnippet(
                int.Parse(sourceMatch.Groups[1].Value),
                sourceMatch.Groups[2].Value,
                underlineMatch.Groups[1].Value.Length + 1,
                underlineMatch.Groups[2].Value.Length);
        }

        throw new ShouldAssertException("Expected rendered diagnostic source snippet was not found.");
    }

    private sealed record RenderedSnippet(int Line, string SourceLine, int Column, int UnderlineLength);

    private static async Task<CliCommandResult> RunCliAsync(string[] args, string? stdin = null)
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync(args);
        startInfo.RedirectStandardInput = stdin is not null;

        using var process = Process.Start(startInfo)!;
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new CliCommandResult(process.ExitCode, stdout, stderr, stdout + stderr);
    }

    private static Task<CliCommandResult> RunCliAsync(params string[] args)
    {
        return RunCliAsync(args, stdin: null);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashes-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record CliCommandResult(int ExitCode, string Stdout, string Stderr, string Output);
}
