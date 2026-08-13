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
    public void Nullary_external_function_call_lowers_with_no_arguments()
    {
        var (program, diagnostics) = LowerProgram("""
            external getpid() -> Int = "getpid@libc.so.6"
            getpid()
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalFunctions.Count.ShouldBe(1);

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExternal>().Single();
        externalCall.SymbolName.ShouldBe("getpid");
        externalCall.LibraryName.ShouldBe("libc.so.6");
        externalCall.ArgTemps.ShouldBeEmpty();
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
    public void External_unsigned_integer_types_lower_to_unsigned_ffi_types_and_ashes_uints()
    {
        var (program, diagnostics) = LowerProgram("""
            external pack(u8, u16, u32, u64) -> u32
            pack(1u8, 2u16, 3u32, 4u64)
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
    public void External_f32_parameters_accept_ashes_float_values_and_lower_to_float32_ffi()
    {
        var (program, diagnostics) = LowerProgram("""
            external point(f32, f32, f32) -> void
            point(1.0, 2.0, 3.0)
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.ExternalFunctions.Count.ShouldBe(1);
        program.ExternalFunctions[0].ParameterTypes.ShouldBe([
            new FfiType.Float32(),
            new FfiType.Float32(),
            new FfiType.Float32()
        ]);

        var externalCall = program.EntryFunction.Instructions.OfType<IrInst.CallExternal>().Single();
        externalCall.ParameterTypes.ShouldBe(program.ExternalFunctions[0].ParameterTypes);
    }

    [Test]
    public void External_void_return_type_lowers_to_void_call_and_unit_result()
    {
        var (program, diagnostics) = LowerProgram("""
            external log(Str, u32) -> void
            log("answer", 42u32)
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
    public void External_ffi_buffer_accepts_a_list_of_copyable_opaque_handles()
    {
        var (program, diagnostics) = LowerProgram("""
            external type NativeHandle
            external makeHandle(Int) -> NativeHandle
            external inspect(FfiBuffer(NativeHandle), u64) -> Int
            inspect([makeHandle(1), makeHandle(2)], 2u64)
            """);

        diagnostics.Errors.ShouldBeEmpty();
        IrExternalFunction inspect = program.ExternalFunctions.Single(function =>
            string.Equals(function.Name, "inspect", StringComparison.Ordinal));
        inspect.ParameterTypes.ShouldBe([
            new FfiType.Buffer(new FfiType.Opaque("NativeHandle")),
            new FfiType.UInt(64)
        ]);
        program.EntryFunction.Instructions.OfType<IrInst.CallExternal>()
            .Single(call => string.Equals(call.SymbolName, "inspect", StringComparison.Ordinal))
            .ParameterTypes.ShouldBe(inspect.ParameterTypes);
    }

    [Test]
    public void External_ffi_buffer_rejects_non_opaque_resource_nested_and_return_shapes()
    {
        var (program, diagnostics) = LowerProgram("""
            external type Handle
            external type Resource resource destructor closeResource
            external closeResource(consume Resource) -> void
            external primitive(FfiBuffer(Int)) -> Int
            external resource(FfiBuffer(Resource)) -> Int
            external nested(*FfiBuffer(Handle)) -> Int
            external returned() -> FfiBuffer(Handle)
            0
            """);

        program.ExternalFunctions.Select(function => function.Name).ShouldBe(["closeResource"]);
        diagnostics.Errors.ShouldContain(error => error.Contains(
            "FfiBuffer(T) requires a copyable opaque external type T.",
            StringComparison.Ordinal));
        diagnostics.Errors.ShouldContain(error => error.Contains(
            "FfiBuffer(Resource) cannot contain affine external resources.",
            StringComparison.Ordinal));
        diagnostics.Errors.Count(error => error.Contains(
            "FfiBuffer(T) is supported only as a direct external parameter.",
            StringComparison.Ordinal)).ShouldBe(2);
        diagnostics.StructuredErrors.Where(error => error.Message.StartsWith("FfiBuffer", StringComparison.Ordinal))
            .ShouldAllBe(error => error.Code == DiagnosticCodes.InvalidFfiBuffer);
    }

    [Test]
    public void External_ffi_buffer_function_cannot_be_used_as_a_first_class_value()
    {
        var (_, diagnostics) = LowerProgram("""
            external type Handle
            external inspect(FfiBuffer(Handle)) -> Int
            let f = inspect in 0
            """);

        diagnostics.Errors.ShouldContain(error => error.Contains(
            "External function 'inspect' has a call-scoped FFI buffer parameter and must be called directly.",
            StringComparison.Ordinal));
        diagnostics.StructuredErrors.ShouldContain(error => error.Code == DiagnosticCodes.InvalidFfiBuffer);
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

        foreach (IrFunction thunk in thunkFuncs)
        {
            IrFunctionOrigin origin = thunk.Origin
                ?? throw new InvalidOperationException("Missing external thunk origin.");
            origin.Kind.ShouldBe(IrFunctionOriginKind.ExternalThunk);
            CompilerFunctionOwner owner = origin.CompilerOwner
                ?? throw new InvalidOperationException("Missing external thunk owner.");
            owner.Kind.ShouldBe(CompilerFunctionOwnerKind.External);
            owner.Name.ShouldBe("add");
            origin.StableDiscriminator.ShouldBe(
                ReferenceEquals(thunk, outerLayer) ? "layer:0" : "layer:1");
        }

        outerLayer.Origin!.ParentGeneratedLabel.ShouldBe("_start_main");
        innerLayer.Origin!.ParentGeneratedLabel.ShouldBe(outerLayer.Label);
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

    [Test]
    public void Resource_aware_external_function_must_be_called_directly()
    {
        var (_, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle
            external closeHandle(consume Handle) -> void
            let close = closeHandle in 0
            """);

        diagnostics.StructuredErrors.ShouldContain(
            error => error.Code == DiagnosticCodes.InvalidExternalOwnershipMarker
                && error.Message.Contains("must be called directly", StringComparison.Ordinal));
    }

    [Test]
    public void Resource_returning_external_function_must_be_called_directly()
    {
        var (_, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle
            external closeHandle(consume Handle) -> void
            let open = openHandle in 0
            """);

        diagnostics.StructuredErrors.ShouldContain(
            error => error.Code == DiagnosticCodes.InvalidExternalOwnershipMarker
                && error.Message.Contains("must be called directly", StringComparison.Ordinal));
    }

    [Test]
    public void Declared_resource_emits_its_external_destructor_at_scope_exit()
    {
        var (program, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle
            external closeHandle(consume Handle) -> void
            let resource = openHandle() in 0
            """);

        diagnostics.Errors.ShouldBeEmpty();
        IrInst.CleanupResource cleanup = program.EntryFunction.Instructions
            .OfType<IrInst.CleanupResource>()
            .Single();
        cleanup.TypeName.ShouldBe("Handle");
        cleanup.Destructor.ShouldNotBeNull();
        cleanup.Destructor.Name.ShouldBe("closeHandle");
        cleanup.Destructor.DestructorForResource.ShouldBe("Handle");
    }

    [Test]
    public void Borrowed_resource_call_preserves_ownership_until_explicit_close()
    {
        var (program, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle
            external inspectHandle(borrow Handle) -> Int
            external closeHandle(consume Handle) -> void
            let resource = openHandle() in
                let _ = inspectHandle(resource) in
                    closeHandle(resource)
            """);

        diagnostics.Errors.ShouldBeEmpty();
        program.EntryFunction.Instructions.OfType<IrInst.CallExternal>()
            .Select(call => call.SymbolName)
            .ShouldBe(["openHandle", "inspectHandle", "closeHandle"]);
        program.EntryFunction.Instructions.OfType<IrInst.CleanupResource>().ShouldBeEmpty();
    }

    [Test]
    public void Consuming_resource_call_rejects_later_use()
    {
        var (_, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle
            external takeHandle(consume Handle) -> void
            external inspectHandle(borrow Handle) -> Int
            external closeHandle(consume Handle) -> void
            let resource = openHandle() in
                let _ = takeHandle(resource) in
                    inspectHandle(resource)
            """);

        diagnostics.StructuredErrors.ShouldContain(error => error.Code == DiagnosticCodes.UseAfterMove);
    }

    [Test]
    public void Closing_declared_resource_twice_reports_double_close()
    {
        var (_, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle
            external closeHandle(consume Handle) -> void
            let resource = openHandle() in
                let _ = closeHandle(resource) in
                    closeHandle(resource)
            """);

        diagnostics.StructuredErrors.ShouldContain(error => error.Code == DiagnosticCodes.DoubleDrop);
    }

    [Test]
    public void Direct_resource_parameter_requires_ownership_marker()
    {
        var (_, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external inspectHandle(Handle) -> Int
            external closeHandle(consume Handle) -> void
            0
            """);

        diagnostics.StructuredErrors.ShouldContain(
            error => error.Code == DiagnosticCodes.InvalidExternalOwnershipMarker);
    }

    [Test]
    public void Resource_requires_a_matching_consume_destructor()
    {
        var (_, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external closeHandle(borrow Handle) -> void
            0
            """);

        diagnostics.StructuredErrors.Count(
            error => string.Equals(error.Code, DiagnosticCodes.InvalidExternalResourceDestructor, StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public void Resource_bearing_aggregate_recursively_uses_declared_destructor()
    {
        var (program, diagnostics) = LowerProgram("""
            external type Handle resource destructor closeHandle
            external openHandle() -> Handle
            external closeHandle(consume Handle) -> void
            type Wrapped = | Wrapped(Handle)
            let wrapped = Wrapped(openHandle()) in 0
            """);

        diagnostics.Errors.ShouldBeEmpty();
        IrInst.CleanupResource cleanup = program.EntryFunction.Instructions
            .OfType<IrInst.CleanupResource>()
            .Single();
        cleanup.Destructor.ShouldNotBeNull();
        cleanup.Destructor.Name.ShouldBe("closeHandle");
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
