using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Regression coverage for TryEmitScopeCopyOut's live-posts guard. Declaring any capability makes
// every arena scope exit conditional on no post-resume continuation being pending, so the copy-out
// runs on one path and the original arena pointer survives on the other. The guard routes both
// through a local and hands the caller a reloaded temp, which is the temp that has to carry the
// runtime-managed ownership fact: marking only the copy destination left the reloaded temp
// unowned, so nothing released the copy and a loop grew its resident set without bound. The
// skipped path is unaffected because an arena value's header holds the immortal sentinel, against
// which a drop is a no-op.
public sealed class CapabilityGuardedCopyOutDropTests
{
    private const string Loop = """
        let recursive loop (remaining: Int) (checksum: Int) =
            if remaining == 0
            then checksum
            else
                let observed = "\"" + (let b = Box("owned") in match b with | Box(item) -> item) + "\""
                in loop(remaining - 1)(checksum + Ashes.Text.byteLength(observed))

        Ashes.IO.print(Ashes.Text.fromInt(loop(8)(0)))
        """;

    [Test]
    public void Guarded_arena_copy_out_releases_the_copy()
    {
        IrProgram program = LowerProgram($$"""
            type Box(a) =
                | Box(a)

            capability C =
                | transform : Int -> Int

            {{Loop}}
            """);

        FunctionBinding(program, "observed").Instructions
            .Count(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true })
            .ShouldBeGreaterThanOrEqualTo(1);
    }

    // The same loop with no capability declared takes the unguarded path, which already released
    // the copy. Pins that the guarded and unguarded shapes agree rather than only that the guarded
    // one emits something.
    [Test]
    public void Unguarded_arena_copy_out_releases_the_copy()
    {
        IrProgram program = LowerProgram($$"""
            type Box(a) =
                | Box(a)

            {{Loop}}
            """);

        FunctionBinding(program, "observed").Instructions
            .Count(inst => inst is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true })
            .ShouldBeGreaterThanOrEqualTo(1);
    }

    private static IrFunction FunctionBinding(IrProgram program, string localName)
    {
        return program.Functions
            .Concat([program.EntryFunction])
            .Single(function => function.LocalNames?.Values.Contains(localName, StringComparer.Ordinal) == true);
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
