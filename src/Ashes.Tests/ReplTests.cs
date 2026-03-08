using System.Diagnostics;
using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class ReplTests
{
    [Test]
    public async Task Repl_should_persist_bindings_across_inputs()
    {
        var result = await RunReplAsync(
            "let add = fun (x) -> fun (y) -> x + y in add",
            "add(1)(2)",
            ":quit");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("add : Int -> Int -> Int");
        result.Output.ShouldContain("3");
        result.Output.ShouldContain(": Int");
    }

    [Test]
    public async Task Repl_should_persist_recursive_bindings()
    {
        var result = await RunReplAsync(
            "let rec loop = fun (n) -> if n <= 0 then 0 else loop(n - 1) in loop",
            "loop(2)",
            ":quit");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("loop : Int -> Int");
        result.Output.ShouldContain("0");
    }

    [Test]
    public async Task Repl_should_support_multiline_match_input()
    {
        var result = await RunReplAsync(
            "match [1, 2, 3] with",
            "| _ -> 1",
            ":quit");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("1");
        result.Output.ShouldContain(": Int");
    }

    [Test]
    public async Task Repl_should_support_multiline_nested_let_input()
    {
        var result = await RunReplAsync(
            "let x =",
            "    let y = 2 in y",
            "in x + 1",
            ":quit");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("3");
        result.Output.ShouldContain(": Int");
    }

    [Test]
    public async Task Repl_should_recover_after_compile_error()
    {
        var result = await RunReplAsync(
            "1 + true",
            "1 + 2",
            ":quit");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("<repl>");
        result.Output.ShouldContain("got Int and Bool");
        result.Output.ShouldContain("3");
        result.Output.ShouldContain(": Int");
    }

    [Test]
    public async Task Repl_should_echo_function_types_without_printing_values()
    {
        var result = await RunReplAsync(
            "fun (x) -> x",
            ":quit");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain(": a -> a");
    }

    private static async Task<ReplResult> RunReplAsync(params string[] lines)
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync("repl");
        startInfo.RedirectStandardInput = true;

        using var process = Process.Start(startInfo)!;
        foreach (var line in lines)
        {
            await process.StandardInput.WriteLineAsync(line);
        }

        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ReplResult(process.ExitCode, stdout, stderr, stdout + stderr);
    }
    private sealed record ReplResult(int ExitCode, string Stdout, string Stderr, string Output);
}
