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
    public void Extern_unsigned_integer_types_lower_to_unsigned_ffi_types_and_ashes_ints()
    {
        var (program, diagnostics) = LowerProgram("""
            extern pack(u8, u16, u32, u64) -> u32
            pack(1, 2, 3, 4)
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternFunctions.Count.ShouldBe(1);
        program.ExternFunctions[0].ParameterTypes.ShouldBe([
            new FfiType.UInt(8),
            new FfiType.UInt(16),
            new FfiType.UInt(32),
            new FfiType.UInt(64)
        ]);
        program.ExternFunctions[0].ReturnType.ShouldBe(new FfiType.UInt(32));

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExtern>().Single();
        externalCall.ParameterTypes.ShouldBe(program.ExternFunctions[0].ParameterTypes);
        externalCall.ReturnType.ShouldBe(new FfiType.UInt(32));
    }

    [Test]
    public void Extern_void_return_type_lowers_to_void_call_and_unit_result()
    {
        var (program, diagnostics) = LowerProgram("""
            extern log(Str, u32) -> void
            log("answer", 42)
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternFunctions.Count.ShouldBe(1);
        program.ExternFunctions[0].ParameterTypes.ShouldBe([new FfiType.Str(), new FfiType.UInt(32)]);
        program.ExternFunctions[0].ReturnType.ShouldBe(new FfiType.Void());

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExtern>().Single();
        externalCall.ReturnType.ShouldBe(new FfiType.Void());
        program.EntryFunction.Instructions.OfType<IrInst.ToCString>().Single().Target.ShouldBe(externalCall.ArgTemps[0]);
    }

    [Test]
    public void Extern_pointer_types_lower_to_pointer_ffi_types()
    {
        var (program, diagnostics) = LowerProgram("""
            extern type NativeHandle
            extern makeHandle(Int) -> *NativeHandle
            extern identity(*NativeHandle) -> *NativeHandle
            identity(makeHandle(42))
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternFunctions.Select(f => f.Name).ShouldBe(["makeHandle", "identity"]);
        program.ExternFunctions[1].ParameterTypes.ShouldBe([new FfiType.Ptr(new FfiType.Opaque("NativeHandle"))]);
        program.ExternFunctions[1].ReturnType.ShouldBe(new FfiType.Ptr(new FfiType.Opaque("NativeHandle")));
        program.EntryFunction.Instructions.OfType<IrInst.CallExtern>().Select(c => c.SymbolName).ShouldBe(["makeHandle", "identity"]);
    }

    [Test]
    public void Extern_nested_pointer_types_support_buffer_out_parameters()
    {
        var (program, diagnostics) = LowerProgram("""
            extern fill(**u8) -> Bool
            0
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternFunctions.Count.ShouldBe(1);
        program.ExternFunctions[0].ParameterTypes.ShouldBe([new FfiType.Ptr(new FfiType.Ptr(new FfiType.UInt(8)))]);
        program.ExternFunctions[0].ReturnType.ShouldBe(new FfiType.Bool());
    }

    [Test]
    public void Void_is_rejected_as_an_extern_parameter_type()
    {
        var (program, diagnostics) = LowerProgram("""
            extern bad(void) -> Int
            0
            """);

        program.ExternFunctions.ShouldBeEmpty();
        diagnostics.Errors.ShouldContain(error => error.Contains("Type 'void' is only supported as an extern return type.", StringComparison.Ordinal));
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

    [Test]
    public void Extern_function_used_as_value_synthesizes_closure_thunk()
    {
        var (program, diagnostics) = LowerProgram("""
            extern strlen(Str) -> Int
            let f = strlen in f("abc")
            """);

        diagnostics.Errors.ShouldBeEmpty();

        // The synthesised thunk should appear among the non-entry functions.
        var thunkFuncs = program.Functions.Where(f => f.Label.StartsWith("extern_strlen_thunk", StringComparison.Ordinal)).ToList();
        thunkFuncs.Count.ShouldBe(1);

        // The thunk (innermost = only layer for a 1-param extern) must contain a CallExtern.
        var thunk = thunkFuncs[0];
        thunk.Instructions.OfType<IrInst.CallExtern>().Count().ShouldBe(1);

        // Entry must produce a MakeClosure whose FuncLabel points at the thunk.
        var makeClosure = program.EntryFunction.Instructions.OfType<IrInst.MakeClosure>()
            .FirstOrDefault(mc => string.Equals(mc.FuncLabel, thunk.Label, StringComparison.Ordinal));
        makeClosure.ShouldNotBeNull();
    }

    [Test]
    public void Two_param_extern_as_value_synthesizes_two_thunk_layers()
    {
        var (program, diagnostics) = LowerProgram("""
            extern add(Int, Int) -> Int
            let f = add in f(1)(2)
            """);

        diagnostics.Errors.ShouldBeEmpty();

        var thunkFuncs = program.Functions
            .Where(f => f.Label.StartsWith("extern_add_thunk", StringComparison.Ordinal))
            .OrderBy(f => f.Label, StringComparer.Ordinal)
            .ToList();
        thunkFuncs.Count.ShouldBe(2);

        // Layer 0 (outer) packs arg into env and returns a MakeClosure — no CallExtern.
        var outerLayer = thunkFuncs.First(f => f.Label.Contains("_0_", StringComparison.Ordinal));
        outerLayer.Instructions.OfType<IrInst.CallExtern>().ShouldBeEmpty();
        outerLayer.Instructions.OfType<IrInst.MakeClosure>().Count().ShouldBe(1);

        // Layer 1 (inner) loads from env and issues CallExtern.
        var innerLayer = thunkFuncs.First(f => f.Label.Contains("_1_", StringComparison.Ordinal));
        innerLayer.Instructions.OfType<IrInst.CallExtern>().Count().ShouldBe(1);
    }

    [Test]
    public void Extern_function_as_value_and_direct_call_coexist_without_conflict()
    {
        // Using strlen both as a first-class value and as a direct call in the same program
        // must not produce errors.
        var (_, diagnostics) = LowerProgram("""
            extern strlen(Str) -> Int
            let f = strlen in let direct = strlen("hello") in f("world")
            """);

        diagnostics.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Extern_function_passed_to_higher_order_function_works()
    {
        var (_, diagnostics) = LowerProgram("""
            extern neg(Int) -> Int
            let apply = fun (f) -> fun (x) -> f(x) in apply(neg)(3)
            """);

        diagnostics.Errors.ShouldBeEmpty();
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
