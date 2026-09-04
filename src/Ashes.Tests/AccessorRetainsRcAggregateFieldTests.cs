using System.Diagnostics;
using System.Text.RegularExpressions;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// An accessor's own compiled body — let getName (p: Person) = p.name — is nothing but a borrowed
// field read: its own return value never allocates anything RC, so its per-callee
// ReturnsRuntimeManaged bit (baked once, at every call site's closure value) is always false. Every
// call site therefore ran the whole result through CopyOutArena RcNormalization — a full byte copy
// of the field — even when the caller can independently prove its own aggregate argument is
// RC-placed, in which case the field is already an independently owned RC value with its own
// header (an RC parent may only own inline data, static non-owning data, or independently owned RC
// children) and retaining that reference (RcDup) is sound and far cheaper. The fix is a call-site
// decision (IsTrivialAccessorCall/ClassifyAccessorArgumentRc/RetainAccessorCallResult in
// Lowering.cs), not a change to the callee's own ReturnsRuntimeManaged bit — every other call site
// (an arena aggregate, or one whose RC-ness cannot be proven) keeps the unchanged copy behavior.
public sealed class AccessorRetainsRcAggregateFieldTests
{
    // callMany's own p parameter is a self-recursive loop's TCO parameter over a record type
    // (Person), which the TCO parameter-entry path places genuinely RC (EmitRuntimeManagedTcoArgumentNormalization) —
    // exactly the "provably RC aggregate" case the fix targets. Its own placement is not settled
    // until after the whole loop body is lowered (TryGetRuntimeManagedCallArgument's own "eligibility
    // can resolve only after this call's surrounding tail self-call constrains the parameter types"
    // timing), so the retain here goes through the deferred marker path
    // (RetainAccessorCallResult's PendingTcoSlot branch / FinalizeAccessorResultRetains), not the
    // immediate one.
    private const string AccessorOverRcTcoParameterSource = """
        type Person =
            | name: Str
            | age: Int

        let getName (p: Person) = p.name

        let recursive build count acc =
            if count == 0
            then acc
            else build(count - 1)(Person(name = "Alice", age = count) :: acc)

        let recursive headOf xs =
            match xs with
                | h :: _ -> h
                | [] -> Person(name = "", age = 0)

        let recursive callMany i result p =
            if i == 0
            then result
            else callMany(i - 1)(getName(p))(p)

        let people = build(3)([])

        let last = callMany(200000)("")(headOf(people))

        Ashes.IO.print(last)
        """;

    [Test]
    public async Task Accessor_over_rc_tco_parameter_survives_two_hundred_thousand_calls()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string stdout = await CompileRunCaptureProgramAsync(AccessorOverRcTcoParameterSource).ConfigureAwait(false);

        stdout.ShouldBe("Alice\n");
    }

    // getName's own call site inside callMany's loop should retain (RcDup ... RuntimeManaged=true)
    // rather than copy (CopyOutArena ... Purpose=RcNormalization) its result, once the loop's own
    // parameter placement resolves p to runtime-RC. The forced-true ownership flag
    // (LoadConstInt ... Value=1, ResolveCallResultOwnershipFlag's replacement for the old
    // packed-word bit-63 read) makes the CopyOutArena branch dead code; the positive signal here is
    // the RcDup itself, upgraded from an identity marker to RuntimeManaged=true by
    // FinalizeAccessorResultRetains.
    [Test]
    public void Accessor_over_rc_tco_parameter_call_result_is_retained_not_copied()
    {
        Diagnostics diagnostics = new();
        var program = new Parser(AccessorOverRcTcoParameterSource, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("accessorRetain.ash", AccessorOverRcTcoParameterSource);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();

        IReadOnlyList<string> entryIr = IrTextFormatter.Format(
            IrOptimizer.Optimize(ir), IrDumpStage.Final, filter: null);
        string dump = string.Join('\n', entryIr);

        // A forced-true flag (LoadConstInt ... Value=1) immediately preceding the call, then the
        // call itself, then an RcDup upgraded to RuntimeManaged=true: the exact sequence
        // ResolveCallResultOwnershipFlag + RetainAccessorCallResult + FinalizeAccessorResultRetains
        // produce together, in place of the old packed-word bit-63 read this call site used before.
        Match match = Regex.Match(
            dump,
            @"LoadConstInt\s+Target=\d+ Value=1\s*(?:\([^)]*\))?\s*\n\s*LoadMemOffset[^\n]*\n\s*CallKnown[^\n]*\n\s*RcDup\s+Target=\d+ SourceTemp=\d+ RuntimeManaged=true");

        match.Success.ShouldBeTrue($"expected a forced-true flag, the call, then an upgraded RcDup; dump:\n{dump}");
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
        var exePath = Path.Combine(tmpDir, $"accessor_retain_{Guid.NewGuid():N}");
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
