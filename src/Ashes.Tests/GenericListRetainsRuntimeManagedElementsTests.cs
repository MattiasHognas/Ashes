using System.Diagnostics;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// A generic list-building function (Ashes.Collection.List.reverse is the standing example, and its
// self-recursive "go" helper is mirrored here as reverseGo/reverseList so this in-process harness
// does not depend on stdlib module stitching) is compiled once, generically: its own cons cell has
// no static layout for a type-variable head, so it can never mark that cell runtime-managed the way
// a monomorphic accumulator does. GetCallCopyOutKind used to fall straight to CopyOutKind.None for
// a call result of List(T) where T is anything but a bare Str or a list of arena-resettable values
// (a record/ADT, a tuple, Bytes, BigInt) — silently skipping ALL normalization at the call boundary
// instead of falling back to the recursive deep-copy machinery TCO parameter entry normalization
// already used for this exact element shape (EmitRuntimeManagedTcoListDeepCopy). Once the source
// list is released, the returned list's element pointers could read back from memory the release
// had already reclaimed.
public sealed class GenericListRetainsRuntimeManagedElementsTests
{
    // A minimal generic reverse over a list of records: reverseGo's own body never marks its `head
    // :: acc` cons cell runtime-managed (no static layout for the type-variable element), so the
    // call site is solely responsible for normalizing the returned list.
    private const string MinimalGenericReverseOfRecordsSource = """
        type Item =
            | text: Str
            | position: Int

        let recursive reverseGo acc rest =
            match rest with
                | [] -> acc
                | head :: tail -> reverseGo(head :: acc)(tail)

        let reverseList xs = reverseGo([])(xs)

        let recursive build count acc =
            if count == 0
            then acc
            else build(count - 1)(Item(text = "item " + Ashes.Text.fromInt(count), position = count) :: acc)

        let items = build(3)([])

        let ignored = reverseList(items)

        Ashes.IO.print("ok")
        """;

    // Before the fix, GetCallCopyOutKind returned CopyOutKind.None for reverseList's List(Item)
    // result (Item is a named record, not Str and not a list of arena-resettable values), so the
    // call site emitted no normalization at all: the returned list's element pointers stayed live
    // only as long as the caller's own copy of `items` was never released. The fix routes this
    // element shape through EmitRuntimeManagedTcoListDeepCopy — the same recursive per-element deep
    // copy already used to normalize a runtime-managed TCO parameter on entry — so the call result
    // is retained exactly like a monomorphic accumulator's. That routine emits a "rc_normalize_list"
    // walk (count/cache/build over fresh RuntimeManaged cons cells); other functions (build's own
    // accumulator, reverseGo's own TCO loop entry) already emit this same walk for unrelated,
    // pre-existing reasons, so this test isolates the program's entry function specifically — the
    // walk this fix adds appears there, at the "let ignored = reverseList(items)" call site, in
    // place of the silent no-op the None branch previously fell through to.
    [Test]
    public void Generic_reverse_of_records_call_result_is_deep_copy_normalized()
    {
        Diagnostics diagnostics = new();
        var program = new Parser(MinimalGenericReverseOfRecordsSource, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("genericReverse.ash", MinimalGenericReverseOfRecordsSource);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();

        IReadOnlyList<string> entryIr = IrTextFormatter.Format(
            IrOptimizer.Optimize(ir), IrDumpStage.Final, filter: null);
        string dump = string.Join('\n', entryIr);

        int entryFunctionStart = dump.IndexOf("function _start_main", StringComparison.Ordinal);
        entryFunctionStart.ShouldBeGreaterThanOrEqualTo(
            0, $"expected a ProgramEntry function named _start_main; dump:\n{dump}");
        string entryFunctionIr = dump[entryFunctionStart..];

        entryFunctionIr.ShouldContain(
            "rc_normalize_list",
            Case.Insensitive,
            $"reverseList's call result should deep-copy normalize inside the program entry; dump:\n{dump}");
    }

    // A non-inlined generic reverseGo/reverseList moving Item records (a Str field plus an Int
    // field) out of a consumed list into the cells it builds, exercised end to end with an
    // unrelated 5000-iteration allocation churn between the reverse call and the read — the same
    // recipe GenericParameterHeapValueUafTests uses, needed to actually observe stale reads rather
    // than just a leak. A payload read back from freed/reused memory would print garbled text
    // instead of the expected "item N" lines.
    private const string GenericReverseChurnSource = """
        type Item =
            | text: Str
            | position: Int

        let recursive reverseGo acc rest =
            match rest with
                | [] -> acc
                | head :: tail -> reverseGo(head :: acc)(tail)

        let reverseList xs = reverseGo([])(xs)

        let recursive build count acc =
            if count == 0
            then acc
            else build(count - 1)(Item(text = "item " + Ashes.Text.fromInt(count), position = count) :: acc)

        let recursive show items =
            match items with
                | [] -> Unit
                | Item { text = text, position = position } :: rest ->
                    let _ = Ashes.IO.print(Ashes.Text.fromInt(position) + " -> " + text)
                    in show(rest)

        let recursive length items count =
            match items with
                | [] -> count
                | _ :: rest -> length(rest)(count + 1)

        let recursive churn count acc =
            if count == 0
            then acc
            else churn(count - 1)(("churn " + Ashes.Text.fromInt(count)) :: acc)

        let items = build(3)([])

        let reversed = reverseList(items)

        let noise = churn(5000)([])

        let _ = Ashes.IO.print("noise " + Ashes.Text.fromInt(length(noise)(0)))

        show(reversed)
        """;

    [Test]
    public async Task Generic_reverse_over_records_survives_unrelated_allocation_churn()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string stdout = await CompileRunCaptureProgramAsync(GenericReverseChurnSource).ConfigureAwait(false);

        // build(3, []) recurses count 3 -> 2 -> 1 -> 0 and conses at each step, so the last cons
        // (position 1) ends up at the head: items = [pos 1, pos 2, pos 3]. reverseList (mirroring
        // Ashes.Collection.List.reverse's own generic "go" shape) reverses that to [pos 3, pos 2,
        // pos 1] — a corrupted payload read back from freed/reused memory after the churn loop
        // would print garbled text here instead of "item 3"/"item 2"/"item 1".
        stdout.ShouldBe("""
            noise 5000
            3 -> item 3
            2 -> item 2
            1 -> item 1

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
        var exePath = Path.Combine(tmpDir, $"generic_reverse_{Guid.NewGuid():N}");
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
