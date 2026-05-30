using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class FfiTests
{
    [Test]
    public void Extern_function_call_lowers_to_external_call_with_null_terminated_string_argument()
    {
        var (program, diagnostics) = LowerProgram("""
            extern strlen(Str) -> Int
            strlen("abc")
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternFunctions.Count.ShouldBe(1);
        program.ExternFunctions[0].Name.ShouldBe("strlen");
        program.ExternFunctions[0].SymbolName.ShouldBe("strlen");
        program.ExternFunctions[0].ParameterTypes.ShouldBe([new FfiType.Str()]);
        program.ExternFunctions[0].ReturnType.ShouldBe(new FfiType.Int());

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExtern>().Single();
        externalCall.SymbolName.ShouldBe("strlen");
        externalCall.ArgTemps.Count.ShouldBe(1);

        program.EntryFunction.Instructions.OfType<IrInst.ToCString>().Single().Target.ShouldBe(externalCall.ArgTemps[0]);
    }

    [Test]
    public void Extern_opaque_types_are_native_words_and_participate_in_call_typing()
    {
        var (program, diagnostics) = LowerProgram("""
            extern type NativeHandle
            extern makeHandle(Int) -> NativeHandle
            extern consumeHandle(NativeHandle) -> Int
            consumeHandle(makeHandle(42))
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternOpaqueTypes.ShouldContain("NativeHandle");
        program.ExternFunctions.Select(f => f.Name).ShouldBe(["makeHandle", "consumeHandle"]);
        program.EntryFunction.Instructions.OfType<IrInst.CallExtern>().Select(c => c.SymbolName).ShouldBe(["makeHandle", "consumeHandle"]);
    }

    [Test]
    public void Extern_call_reports_type_mismatch_for_wrong_argument_type()
    {
        var (_, diagnostics) = LowerProgram("""
            extern strlen(Str) -> Int
            strlen(42)
            """);

        diagnostics.Errors.ShouldContain(error => error.Contains("Type mismatch: Str vs Int", StringComparison.Ordinal));
    }

    private static (IrProgram Program, Diagnostics Diagnostics) LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseProgram();
        var ir = new Lowering(diagnostics).Lower(ast);
        return (ir, diagnostics);
    }
}
