using System.Diagnostics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// The <c>--explain</c> CLI surface: which commands accept it, how values parse, where output goes,
/// and the guarantee that asking for a report cannot change what was compiled.
/// </summary>
public sealed class ExplainReportCliTests
{
    private const string Program = """
        import Ashes.IO
        import Ashes.Text

        type Box =
            | Boxed(Str)

        let label text = "tag-" + text

        let recursive relabel entries acc =
            match entries with
                | [] -> acc
                | Boxed(text) :: rest -> relabel(rest)(acc + Ashes.Text.byteLength(label(text)))

        Ashes.IO.print(relabel([Boxed("a"), Boxed("b")])(0))
        """;

    private const string ExpectedStdout = "10\n";

    [Test]
    public async Task Compile_should_report_ownership_on_stderr()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("compile", path, "-o", Path.ChangeExtension(path, null) + ".out", "--explain", "ownership")
                .ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Stderr.ShouldContain("Ownership report");
            result.Stderr.ShouldContain("Parameters");
            result.Stderr.ShouldContain("ownership:");
            result.Stderr.ShouldContain("move-safe:");
            // The report is a diagnostic, so it must not appear on the stream a program would use.
            result.Stdout.ShouldNotContain("Ownership report");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Run_should_report_without_disturbing_program_output()
    {
        await WithProgramAsync(async path =>
        {
            var plain = await RunCliAsync("run", path).ConfigureAwait(false);
            var explained = await RunCliAsync("run", path, "--explain", "rc").ConfigureAwait(false);

            plain.Stdout.ShouldBe(ExpectedStdout);
            // The whole point of stderr: a reported run is byte-for-byte the same program output.
            explained.Stdout.ShouldBe(plain.Stdout);
            explained.Stderr.ShouldContain("RC report");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Test_command_should_accept_explain()
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "explain_case.ash");
            await File.WriteAllTextAsync(path, "// expect: 10\n" + Program).ConfigureAwait(false);

            var result = await RunCliAsync("test", path, "--explain", "reuse").ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Reuse report");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Every_explain_kind_should_produce_its_own_report()
    {
        await WithProgramAsync(async path =>
        {
            foreach ((string kind, string heading) in new[]
            {
                ("ownership", "Ownership report"),
                ("rc", "RC report"),
                ("reuse", "Reuse report"),
                ("traits", "Trait evidence report"),
                ("authority", "Authority report"),
                ("memory", "Memory report"),
            })
            {
                var result = await RunCliAsync("compile", path, "-o", path + "." + kind, "--explain", kind)
                    .ConfigureAwait(false);

                result.ExitCode.ShouldBe(0);
                result.Stderr.ShouldContain(heading);
            }
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Repeated_explain_options_should_combine_and_deduplicate()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync(
                "compile", path, "-o", path + ".out",
                "--explain", "ownership",
                "--explain", "rc",
                "--explain", "ownership").ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Stderr.ShouldContain("Ownership report");
            result.Stderr.ShouldContain("RC report");
            // Requested twice, printed once.
            CountOccurrences(result.Stderr, "Ownership report").ShouldBe(1);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Unknown_explain_value_should_be_a_usage_error()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("compile", path, "-o", path + ".out", "--explain", "everything")
                .ConfigureAwait(false);

            result.ExitCode.ShouldNotBe(0);
            result.Stderr.ShouldContain("Unknown explain type 'everything'.");
            foreach (string valid in new[] { "ownership", "rc", "reuse", "memory" })
            {
                result.Stderr.ShouldContain(valid);
            }
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Explain_without_a_value_should_be_a_usage_error()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("compile", path, "-o", path + ".out", "--explain").ConfigureAwait(false);

            result.ExitCode.ShouldNotBe(0);
            result.Stderr.ShouldContain("--explain requires a value.");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task A_selector_should_restrict_the_report_to_matching_functions()
    {
        await WithProgramAsync(async path =>
        {
            var all = await RunCliAsync("compile", path, "-o", path + ".a", "--explain", "ownership")
                .ConfigureAwait(false);
            var filtered = await RunCliAsync("compile", path, "-o", path + ".b", "--explain", "ownership:relabel")
                .ConfigureAwait(false);

            filtered.ExitCode.ShouldBe(0);
            filtered.Stderr.ShouldContain("relabel");
            CountOccurrences(filtered.Stderr, "Function:")
                .ShouldBeLessThan(CountOccurrences(all.Stderr, "Function:"));
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task A_selector_matching_nothing_should_say_so_and_succeed()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("compile", path, "-o", path + ".out", "--explain", "ownership:no_such_function")
                .ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Stderr.ShouldContain("(no functions matched)");
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task No_report_should_appear_without_the_option()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("compile", path, "-o", path + ".out").ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            foreach (string heading in new[] { "Ownership report", "RC report", "Reuse report", "Memory report" })
            {
                result.Output.ShouldNotContain(heading);
            }
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Explaining_should_not_change_the_compiled_image()
    {
        await WithProgramAsync(async path =>
        {
            string plainPath = path + ".plain";
            string explainedPath = path + ".explained";

            (await RunCliAsync("compile", path, "-o", plainPath).ConfigureAwait(false)).ExitCode.ShouldBe(0);
            (await RunCliAsync("compile", path, "-o", explainedPath, "--explain", "memory", "--explain", "rc")
                .ConfigureAwait(false)).ExitCode.ShouldBe(0);

            // The guarantee that makes the reports safe to leave on: they observe, they do not steer.
            (await File.ReadAllBytesAsync(explainedPath).ConfigureAwait(false))
                .ShouldBe(await File.ReadAllBytesAsync(plainPath).ConfigureAwait(false));
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Two_reports_of_one_program_should_be_identical()
    {
        await WithProgramAsync(async path =>
        {
            var first = await RunCliAsync("compile", path, "-o", path + ".1", "--explain", "memory").ConfigureAwait(false);
            var second = await RunCliAsync("compile", path, "-o", path + ".2", "--explain", "memory").ConfigureAwait(false);

            // Deterministic ordering is what makes a report diffable between builds.
            second.Stderr.ShouldBe(first.Stderr);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task Help_should_document_the_option()
    {
        var result = await RunCliAsync("--help").ConfigureAwait(false);

        result.Output.ShouldContain("--explain");
        foreach (string valid in new[] { "ownership", "rc", "reuse", "memory" })
        {
            result.Output.ShouldContain(valid);
        }
    }

    [Test]
    public async Task Commands_without_compilation_should_reject_the_option()
    {
        await WithProgramAsync(async path =>
        {
            var result = await RunCliAsync("fmt", path, "--explain", "ownership").ConfigureAwait(false);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldNotContain("Ownership report");
        }).ConfigureAwait(false);
    }

    private static async Task WithProgramAsync(Func<string, Task> body)
    {
        var tempDir = CreateTempDir();
        try
        {
            var path = Path.Combine(tempDir, "explain_program.ash");
            await File.WriteAllTextAsync(path, Program).ConfigureAwait(false);
            await body(path).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static async Task<CliCommandResult> RunCliAsync(params string[] args)
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync(args).ConfigureAwait(false);
        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CliCommandResult(process.ExitCode, stdout, stderr, stdout + stderr);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashes-explain-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record CliCommandResult(int ExitCode, string Stdout, string Stderr, string Output);
}
