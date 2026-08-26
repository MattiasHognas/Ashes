using System.Diagnostics;
using Ashes.Backend;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// The DWARF line table of a <c>--debug</c> build, read back with <c>llvm-dwarfdump</c>: which
/// source lines a debugger can actually stop on. Skipped when the tool is not on the path.
/// </summary>
public sealed class DebugLineTableTests
{
    private const string Program = """
        type Kind =
            | Alpha
            | Beta
            | Gamma

        let classify (text: Str) =
            match text with
                | "abc" -> Alpha
                | "def" -> Beta
                | _ -> Gamma

        let describe (kind: Kind) =
            match kind with
                | Alpha -> 1
                | Beta -> 2
                | Gamma -> 3

        Ashes.IO.print(describe(classify("abc")) + describe(classify("xyz")))
        """;

    [Test]
    public async Task A_match_arm_whose_body_is_one_allocation_keeps_its_line()
    {
        // A reference-counted constructor allocation hoists scratch slots into the entry block, and
        // repositioning the builder there adopts the entry allocas' empty debug location; without
        // restoring the arm's location afterwards the rest of the allocation carries no line and
        // the arm never appears in the line table, so a breakpoint on it slides to the next line.
        if (!OperatingSystem.IsLinux() || FindOnPath("llvm-dwarfdump") is not { } dwarfdump)
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"ashes-line-table-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string sourcePath = Path.Combine(root, "arms.ash");
            string outputPath = Path.Combine(root, "arms");
            await File.WriteAllTextAsync(sourcePath, Program + "\n").ConfigureAwait(false);
            ProcessStartInfo compile = await CliTestHost.CreateStartInfoAsync(
                "compile", "--debug", "-O0", sourcePath, "-o", outputPath).ConfigureAwait(false);
            (int exitCode, string _, string stderr) = await RunAsync(compile).ConfigureAwait(false);
            exitCode.ShouldBe(0, stderr);

            HashSet<int> lines = await ReadLineTableLinesAsync(dwarfdump, outputPath).ConfigureAwait(false);
            foreach (string arm in new[] { "| \"abc\" -> Alpha", "| \"def\" -> Beta", "| _ -> Gamma", "| Alpha -> 1", "| Gamma -> 3" })
            {
                lines.ShouldContain(LineOf(arm), $"the line table must have a row for `{arm}`");
            }

            lines.ShouldContain(LineOf("match text with"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int LineOf(string text)
    {
        int index = Program.IndexOf(text, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, text);
        return Program[..index].Count(c => c == '\n') + 1;
    }

    // Every row of the line program whose file entry is the compiled source, by line number.
    private static async Task<HashSet<int>> ReadLineTableLinesAsync(string dwarfdump, string binaryPath)
    {
        ProcessStartInfo startInfo = new(dwarfdump)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--debug-line");
        startInfo.ArgumentList.Add(binaryPath);
        (int exitCode, string stdout, string stderr) = await RunAsync(startInfo).ConfigureAwait(false);
        exitCode.ShouldBe(0, stderr);

        var lines = new HashSet<int>();
        foreach (string row in stdout.Split('\n'))
        {
            // Row format: `0x<address> <line> <column> <file> <isa> <discriminator> <op-index> <flags>`.
            if (!row.StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 4 && int.TryParse(fields[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int line) && string.Equals(fields[3], "0", StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static string? FindOnPath(string command)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(ProcessStartInfo startInfo)
    {
        using Process process = Process.Start(startInfo)!;
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
