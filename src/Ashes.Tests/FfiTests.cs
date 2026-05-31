using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class FfiTests
{
    [Test]
    public void Extern_void_return_lowers_to_external_call_and_unit_value()
    {
        var (program, diagnostics) = LowerProgram("""
            extern type LLVMModuleRef
            extern makeModule(Int) -> LLVMModuleRef
            extern disposeModule(LLVMModuleRef) -> Void
            disposeModule(makeModule(1))
            """);

        diagnostics.Errors.ShouldBeEmpty();
        var dispose = program.ExternFunctions.Single(f => f.Name == "disposeModule");
        dispose.ReturnType.ShouldBe(new FfiType.Void());

        var voidCall = program.EntryFunction.Instructions
            .OfType<IrInst.CallExtern>()
            .Single(c => c.SymbolName == "disposeModule");
        voidCall.ReturnType.ShouldBe(new FfiType.Void());
    }

    [Test]
    public void Extern_void_parameter_is_rejected()
    {
        var (program, diagnostics) = LowerProgram("""
            extern oops(Void) -> Int
            0
            """);

        program.ExternFunctions.ShouldBeEmpty();
        diagnostics.Errors.ShouldContain(error =>
            error.Contains("Type 'Void' is only supported as an extern return type.", StringComparison.Ordinal));
    }

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
    public void Extern_opaque_types_can_be_declared_after_functions()
    {
        var (program, diagnostics) = LowerProgram("""
            extern consumeHandle(NativeHandle) -> Int
            extern type NativeHandle
            0
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternOpaqueTypes.ShouldContain("NativeHandle");
        program.ExternFunctions.Count.ShouldBe(1);
        program.ExternFunctions[0].ParameterTypes.ShouldBe([new FfiType.Opaque("NativeHandle")]);
        program.ExternFunctions[0].ReturnType.ShouldBe(new FfiType.Int());
    }

    [Test]
    public void Extern_functions_report_diagnostics_for_non_ffi_types()
    {
        var (program, diagnostics) = LowerProgram("""
            type MyAdt = | Mk
            extern foo(MyAdt) -> Int
            extern bar(Unknown) -> Int
            0
            """);

        program.ExternFunctions.ShouldBeEmpty();
        diagnostics.Errors.ShouldContain(error => error.Contains("Type 'MyAdt' is not supported in extern declarations.", StringComparison.Ordinal));
        diagnostics.Errors.ShouldContain(error => error.Contains("Type 'Unknown' is not supported in extern declarations.", StringComparison.Ordinal));
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

    [Test]
    public void Unsupported_extern_type_syntax_reports_the_extern_declaration_span()
    {
        var diagnostics = new Diagnostics();
        var externDecl = new ExternDecl.Function("foo", [new UnsupportedParsedType()], new ParsedType.Named("Int"));
        var program = new Program([], [externDecl], new Expr.IntLit(0));
        AstSpans.Set(externDecl, TextSpan.FromBounds(5, 24));

        _ = new Lowering(diagnostics).Lower(program);

        diagnostics.StructuredErrors.ShouldContain(error =>
            error.Message == "Unsupported extern type syntax." &&
            error.Span == TextSpan.FromBounds(5, 24));
    }

    private sealed record UnsupportedParsedType : ParsedType;

    private static (IrProgram Program, Diagnostics Diagnostics) LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseProgram();
        var ir = new Lowering(diagnostics).Lower(ast);
        return (ir, diagnostics);
    }
}
