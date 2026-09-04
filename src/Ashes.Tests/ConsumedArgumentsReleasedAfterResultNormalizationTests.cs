using System.Diagnostics;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// A call whose result the caller must normalize out of the call's arena window (a generic
// list-building callee's own cons cells reference whatever the caller passed in, and the caller
// deep-copies the returned list into fresh runtime-managed cells) may consume runtime-managed
// arguments — here the deep-copied results of two earlier generic `map` calls. Those arguments
// must be released only after the result is normalized: released first, the deep copy reads the
// records the release just freed through the callee's arena cells. A normalized result owns copies
// of every part it kept, so the release is then a full one rather than a child-preserving spine
// drop, which would leak one reference per kept part.
public sealed class ConsumedArgumentsReleasedAfterResultNormalizationTests
{
    // mapList/appendList mirror Ashes.Collection.List.map/append's generic shapes so this
    // in-process harness does not depend on stdlib module stitching.
    private const string GenericAppendOfGenericMapResultsSource = """
        type Item =
            | name: Str
            | direct: Bool

        let mapList f =
            (let recursive mapGo xs =
                match xs with
                    | [] -> []
                    | head :: tail -> f(head) :: mapGo(tail)
            in mapGo)

        let appendList left right =
            (let recursive go rest =
                match rest with
                    | [] -> right
                    | head :: tail -> head :: go(tail)
            in go(left))

        let toEntry (direct: Bool) (name: Str) = Item(name = name, direct = direct)

        let recursive show items =
            match items with
                | [] -> Unit
                | Item { name = name, direct = direct } :: rest ->
                    let _ = Ashes.IO.print(name + (if direct then " yes" else " no"))
                    in show(rest)

        let recursive length items count =
            match items with
                | [] -> count
                | _ :: rest -> length(rest)(count + 1)

        let recursive churn count acc =
            if count == 0
            then acc
            else churn(count - 1)(("churn " + Ashes.Text.fromInt(count)) :: acc)

        let entries = appendList(mapList(toEntry(true))(["Mid"]))(mapList(toEntry(false))(["Testing"]))

        let noise = churn(5000)([])

        let _ = Ashes.IO.print("noise " + Ashes.Text.fromInt(length(noise)(0)))

        show(entries)
        """;

    // In the program entry, the appended result's deep-copy walk ("rc_normalize_list", the third
    // such walk after the two map results' own) must precede the release of the consumed map
    // results ("rcdrop_list"): before the fix the two releases came first, freeing the records the
    // walk then read through appendList's arena cells.
    [Test]
    public void Consumed_generic_map_results_are_released_after_the_appended_result_is_normalized()
    {
        Diagnostics diagnostics = new();
        var program = new Parser(GenericAppendOfGenericMapResultsSource, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("genericAppend.ash", GenericAppendOfGenericMapResultsSource);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();

        IReadOnlyList<string> lines = IrTextFormatter.Format(
            IrOptimizer.Optimize(ir), IrDumpStage.Final, filter: null);
        string dump = string.Join('\n', lines);

        int entryFunctionStart = dump.IndexOf("function _start_main", StringComparison.Ordinal);
        entryFunctionStart.ShouldBeGreaterThanOrEqualTo(
            0, $"expected a ProgramEntry function named _start_main; dump:\n{dump}");
        string entryFunctionIr = dump[entryFunctionStart..];

        int firstRelease = entryFunctionIr.IndexOf("rcdrop_list", StringComparison.Ordinal);
        firstRelease.ShouldBeGreaterThanOrEqualTo(
            0, $"the consumed map results should be released in the program entry; dump:\n{dump}");

        // The third deep-copy walk normalizes appendList's result; the first two normalize the map
        // results before they are passed in.
        int walk = -1;
        for (int occurrence = 0; occurrence < 3; occurrence++)
        {
            walk = entryFunctionIr.IndexOf("rc_normalize_list_", walk + 1, StringComparison.Ordinal);
            walk.ShouldBeGreaterThanOrEqualTo(
                0, $"expected three deep-copy walks in the program entry; dump:\n{dump}");
        }

        walk.ShouldBeLessThan(
            firstRelease,
            $"the appended result must be deep-copied before its consumed arguments are released; dump:\n{dump}");
    }

    [Test]
    public async Task Generic_append_of_generic_map_results_survives_unrelated_allocation_churn()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string stdout = await CompileRunCaptureProgramAsync(GenericAppendOfGenericMapResultsSource).ConfigureAwait(false);

        stdout.ShouldBe("""
            noise 5000
            Mid yes
            Testing no

            """.ReplaceLineEndings("\n"));
    }

    private static async Task<string> CompileRunCaptureProgramAsync(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(program);
        diag.ThrowIfAny();

        var elfBytes = new LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);
        var exePath = Path.Combine(tmpDir, $"generic_append_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
