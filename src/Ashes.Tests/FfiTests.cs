using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class FfiTests
{
    [Test]
    public void External_function_call_lowers_to_external_call_with_null_terminated_string_argument()
    {
        var (program, diagnostics) = LowerProgram("""
            external strlen(Str) -> Int
            strlen("abc")
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalFunctions.Count.ShouldBe(1);
        program.ExternalFunctions[0].Name.ShouldBe("strlen");
        program.ExternalFunctions[0].SymbolName.ShouldBe("strlen");
        program.ExternalFunctions[0].ParameterTypes.ShouldBe([new FfiType.Str()]);
        program.ExternalFunctions[0].ReturnType.ShouldBe(new FfiType.Int());

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExternal>().Single();
        externalCall.SymbolName.ShouldBe("strlen");
        externalCall.ArgTemps.Count.ShouldBe(1);

        program.EntryFunction.Instructions.OfType<IrInst.ToCString>().Single().Target.ShouldBe(externalCall.ArgTemps[0]);
    }

    [Test]
    public void External_opaque_types_are_native_words_and_participate_in_call_typing()
    {
        var (program, diagnostics) = LowerProgram("""
            external type NativeHandle
            external makeHandle(Int) -> NativeHandle
            external consumeHandle(NativeHandle) -> Int
            consumeHandle(makeHandle(42))
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalOpaqueTypes.ShouldContain("NativeHandle");
        program.ExternalFunctions.Select(f => f.Name).ShouldBe(["makeHandle", "consumeHandle"]);
        program.EntryFunction.Instructions.OfType<IrInst.CallExternal>().Select(c => c.SymbolName).ShouldBe(["makeHandle", "consumeHandle"]);
    }

    [Test]
    public void External_opaque_types_can_be_declared_after_functions()
    {
        var (program, diagnostics) = LowerProgram("""
            external consumeHandle(NativeHandle) -> Int
            external type NativeHandle
            0
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalOpaqueTypes.ShouldContain("NativeHandle");
        program.ExternalFunctions.Count.ShouldBe(1);
        program.ExternalFunctions[0].ParameterTypes.ShouldBe([new FfiType.Opaque("NativeHandle")]);
        program.ExternalFunctions[0].ReturnType.ShouldBe(new FfiType.Int());
    }

    [Test]
    public void External_unsigned_integer_types_lower_to_unsigned_ffi_types_and_ashes_ints()
    {
        var (program, diagnostics) = LowerProgram("""
            external pack(u8, u16, u32, u64) -> u32
            pack(1, 2, 3, 4)
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalFunctions.Count.ShouldBe(1);
        program.ExternalFunctions[0].ParameterTypes.ShouldBe([
            new FfiType.UInt(8),
            new FfiType.UInt(16),
            new FfiType.UInt(32),
            new FfiType.UInt(64)
        ]);
        program.ExternalFunctions[0].ReturnType.ShouldBe(new FfiType.UInt(32));

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExternal>().Single();
        externalCall.ParameterTypes.ShouldBe(program.ExternalFunctions[0].ParameterTypes);
        externalCall.ReturnType.ShouldBe(new FfiType.UInt(32));
    }

    [Test]
    public void External_void_return_type_lowers_to_void_call_and_unit_result()
    {
        var (program, diagnostics) = LowerProgram("""
            external log(Str, u32) -> void
            log("answer", 42)
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalFunctions.Count.ShouldBe(1);
        program.ExternalFunctions[0].ParameterTypes.ShouldBe([new FfiType.Str(), new FfiType.UInt(32)]);
        program.ExternalFunctions[0].ReturnType.ShouldBe(new FfiType.Void());

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExternal>().Single();
        externalCall.ReturnType.ShouldBe(new FfiType.Void());
        program.EntryFunction.Instructions.OfType<IrInst.ToCString>().Single().Target.ShouldBe(externalCall.ArgTemps[0]);
    }

    [Test]
    public void External_pointer_types_lower_to_pointer_ffi_types()
    {
        var (program, diagnostics) = LowerProgram("""
            external type NativeHandle
            external makeHandle(Int) -> *NativeHandle
            external identity(*NativeHandle) -> *NativeHandle
            identity(makeHandle(42))
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalFunctions.Select(f => f.Name).ShouldBe(["makeHandle", "identity"]);
        program.ExternalFunctions[1].ParameterTypes.ShouldBe([new FfiType.Ptr(new FfiType.Opaque("NativeHandle"))]);
        program.ExternalFunctions[1].ReturnType.ShouldBe(new FfiType.Ptr(new FfiType.Opaque("NativeHandle")));
        program.EntryFunction.Instructions.OfType<IrInst.CallExternal>().Select(c => c.SymbolName).ShouldBe(["makeHandle", "identity"]);
    }

    [Test]
    public void External_nested_pointer_types_support_buffer_out_parameters()
    {
        var (program, diagnostics) = LowerProgram("""
            external fill(**u8) -> Bool
            0
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalFunctions.Count.ShouldBe(1);
        program.ExternalFunctions[0].ParameterTypes.ShouldBe([new FfiType.Ptr(new FfiType.Ptr(new FfiType.UInt(8)))]);
        program.ExternalFunctions[0].ReturnType.ShouldBe(new FfiType.Bool());
    }

    [Test]
    public void Void_is_rejected_as_an_external_parameter_type()
    {
        var (program, diagnostics) = LowerProgram("""
            external bad(void) -> Int
            0
            """);

        program.ExternalFunctions.ShouldBeEmpty();
        diagnostics.Errors.ShouldContain(error => error.Contains("Type 'void' is only supported as an external return type.", StringComparison.Ordinal));
    }

    [Test]
    public void External_functions_report_diagnostics_for_non_ffi_types()
    {
        var (program, diagnostics) = LowerProgram("""
            type MyAdt = | Mk
            external foo(MyAdt) -> Int
            external bar(Unknown) -> Int
            0
            """);

        program.ExternalFunctions.ShouldBeEmpty();
        diagnostics.Errors.ShouldContain(error => error.Contains("Type 'MyAdt' is not supported in external declarations.", StringComparison.Ordinal));
        diagnostics.Errors.ShouldContain(error => error.Contains("Type 'Unknown' is not supported in external declarations.", StringComparison.Ordinal));
    }

    [Test]
    public void External_call_reports_type_mismatch_for_wrong_argument_type()
    {
        var (_, diagnostics) = LowerProgram("""
            external strlen(Str) -> Int
            strlen(42)
            """);

        diagnostics.Errors.ShouldContain(error => error.Contains("Type mismatch: Str vs Int", StringComparison.Ordinal));
    }

    [Test]
    public void Unsupported_external_type_syntax_reports_the_external_declaration_span()
    {
        var diagnostics = new Diagnostics();
        var externalDecl = new ExternalDecl.Function("foo", [new UnsupportedParsedType()], new ParsedType.Named("Int"));
        var program = new Program([], [externalDecl], new Expr.IntLit(0));
        AstSpans.Set(externalDecl, TextSpan.FromBounds(5, 24));

        _ = new Lowering(diagnostics).Lower(program);

        diagnostics.StructuredErrors.ShouldContain(error =>
            error.Message == "Unsupported external type syntax." &&
            error.Span == TextSpan.FromBounds(5, 24));
    }

    [Test]
    public void External_function_used_as_value_synthesizes_closure_thunk()
    {
        var (program, diagnostics) = LowerProgram("""
            external strlen(Str) -> Int
            let f = strlen in f("abc")
            """);

        diagnostics.Errors.ShouldBeEmpty();

        // The synthesised thunk should appear among the non-entry functions.
        var thunkFuncs = program.Functions.Where(f => f.Label.StartsWith("external_strlen_thunk", StringComparison.Ordinal)).ToList();
        thunkFuncs.Count.ShouldBe(1);

        // The thunk (innermost = only layer for a 1-param external) must contain a CallExternal.
        var thunk = thunkFuncs[0];
        thunk.Instructions.OfType<IrInst.CallExternal>().Count().ShouldBe(1);

        // Entry must produce a MakeClosure whose FuncLabel points at the thunk.
        var makeClosure = program.EntryFunction.Instructions.OfType<IrInst.MakeClosure>()
            .FirstOrDefault(mc => string.Equals(mc.FuncLabel, thunk.Label, StringComparison.Ordinal));
        makeClosure.ShouldNotBeNull();
    }

    [Test]
    public void Two_param_external_as_value_synthesizes_two_thunk_layers()
    {
        var (program, diagnostics) = LowerProgram("""
            external add(Int, Int) -> Int
            let f = add in f(1)(2)
            """);

        diagnostics.Errors.ShouldBeEmpty();

        var thunkFuncs = program.Functions
            .Where(f => f.Label.StartsWith("external_add_thunk", StringComparison.Ordinal))
            .OrderBy(f => f.Label, StringComparer.Ordinal)
            .ToList();
        thunkFuncs.Count.ShouldBe(2);

        // Layer 0 (outer) packs arg into env and returns a MakeClosure — no CallExternal.
        var outerLayer = thunkFuncs.First(f => f.Label.Contains("_0_", StringComparison.Ordinal));
        outerLayer.Instructions.OfType<IrInst.CallExternal>().ShouldBeEmpty();
        outerLayer.Instructions.OfType<IrInst.MakeClosure>().Count().ShouldBe(1);

        // Layer 1 (inner) loads from env and issues CallExternal.
        var innerLayer = thunkFuncs.First(f => f.Label.Contains("_1_", StringComparison.Ordinal));
        innerLayer.Instructions.OfType<IrInst.CallExternal>().Count().ShouldBe(1);
    }

    [Test]
    public void External_function_as_value_and_direct_call_coexist_without_conflict()
    {
        // Using strlen both as a first-class value and as a direct call in the same program
        // must not produce errors.
        var (_, diagnostics) = LowerProgram("""
            external strlen(Str) -> Int
            let f = strlen in let direct = strlen("hello") in f("world")
            """);

        diagnostics.Errors.ShouldBeEmpty();
    }

    [Test]
    public void External_function_passed_to_higher_order_function_works()
    {
        var (_, diagnostics) = LowerProgram("""
            external neg(Int) -> Int
            let apply = given (f) -> given (x) -> f(x) in apply(neg)(3)
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
