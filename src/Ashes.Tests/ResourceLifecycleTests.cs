using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ResourceLifecycleTests
{
    // --- Resource type classification ---

    [Test]
    public void Socket_is_classified_as_resource_type()
    {
        BuiltinRegistry.IsResourceTypeName("Socket").ShouldBeTrue();
    }

    [Test]
    public void Int_is_not_classified_as_resource_type()
    {
        BuiltinRegistry.IsResourceTypeName("Int").ShouldBeFalse();
    }

    [Test]
    public void String_is_not_classified_as_resource_type()
    {
        BuiltinRegistry.IsResourceTypeName("Str").ShouldBeFalse();
    }

    [Test]
    public void List_is_not_classified_as_resource_type()
    {
        BuiltinRegistry.IsResourceTypeName("List").ShouldBeFalse();
    }

    [Test]
    public void Result_is_not_classified_as_resource_type()
    {
        BuiltinRegistry.IsResourceTypeName("Result").ShouldBeFalse();
    }

    [Test]
    public void Maybe_is_not_classified_as_resource_type()
    {
        BuiltinRegistry.IsResourceTypeName("Maybe").ShouldBeFalse();
    }

    // --- Drop IR instruction ---

    [Test]
    public void Drop_ir_instruction_carries_resource_type_name()
    {
        var drop = new IrInst.Drop(0, "Socket");
        drop.SourceSlot.ShouldBe(0);
        drop.ResourceTypeName.ShouldBe("Socket");
    }

    // --- Scope drop: socket bound via pattern match gets Drop at scope exit ---

    [Test]
    public void Socket_binding_in_match_emits_drop_instruction()
    {
        var ir = LowerProgram(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(_) -> Ashes.IO.print("fail")
                | Ok(sock) -> Ashes.IO.print("ok")
            """);

        // The Ok(sock) branch should contain a Drop instruction for the socket
        var allInstructions = ir.EntryFunction.Instructions;
        HasDropInstruction(allInstructions, "Socket")
            .ShouldBeTrue("Expected a Drop instruction for the Socket resource binding.");
    }

    [Test]
    public void Socket_closed_explicitly_does_not_emit_redundant_drop()
    {
        var ir = LowerProgram(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(_) -> Ashes.IO.print("fail")
                | Ok(sock) ->
                    match Ashes.Net.Tcp.close(sock) with
                        | Ok(_) -> Ashes.IO.print("ok")
                        | Error(_) -> Ashes.IO.print("fail")
            """);

        // After explicit close, no additional Drop should be emitted
        var allInstructions = ir.EntryFunction.Instructions;
        HasDropInstruction(allInstructions, "Socket")
            .ShouldBeFalse("Should not emit a redundant Drop when socket is already explicitly closed.");
    }

    [Test]
    public void Tcp_connect_close_flow_typechecks_without_errors()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(_) -> Ashes.IO.print("fail")
                | Ok(sock) ->
                    match Ashes.Net.Tcp.close(sock) with
                        | Ok(_) -> Ashes.IO.print("ok")
                        | Error(_) -> Ashes.IO.print("fail")
            """,
            diag).ParseProgram();
        new Lowering(diag).Lower(program);

        diag.Errors.ShouldBeEmpty();
    }

    // --- Use-after-drop detection ---

    [Test]
    public void Use_after_close_reports_ash006_diagnostic()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(_) -> Ashes.IO.print("fail")
                | Ok(sock) ->
                    match Ashes.Net.Tcp.close(sock) with
                        | Ok(_) ->
                            match Ashes.Net.Tcp.send(sock)("hello") with
                                | Ok(_) -> Ashes.IO.print("ok")
                                | Error(_) -> Ashes.IO.print("fail")
                        | Error(_) -> Ashes.IO.print("fail")
            """,
            diag).ParseProgram();
        new Lowering(diag).Lower(program);

        diag.StructuredErrors.ShouldContain(
            x => x.Code == DiagnosticCodes.UseAfterDrop,
            "Expected ASH006 (use-after-drop) diagnostic for using socket after close.");
        diag.StructuredErrors.ShouldContain(
            x => x.Message.Contains("already been closed", StringComparison.Ordinal));
    }

    [Test]
    public void Use_after_close_with_receive_reports_ash006_diagnostic()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(_) -> Ashes.IO.print("fail")
                | Ok(sock) ->
                    match Ashes.Net.Tcp.close(sock) with
                        | Ok(_) ->
                            match Ashes.Net.Tcp.receive(sock)(64) with
                                | Ok(_) -> Ashes.IO.print("ok")
                                | Error(_) -> Ashes.IO.print("fail")
                        | Error(_) -> Ashes.IO.print("fail")
            """,
            diag).ParseProgram();
        new Lowering(diag).Lower(program);

        diag.StructuredErrors.ShouldContain(
            x => x.Code == DiagnosticCodes.UseAfterDrop,
            "Expected ASH006 (use-after-drop) diagnostic for receiving on closed socket.");
    }

    // --- Double-drop detection ---

    [Test]
    public void Double_close_reports_ash007_diagnostic()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(_) -> Ashes.IO.print("fail")
                | Ok(sock) ->
                    match Ashes.Net.Tcp.close(sock) with
                        | Ok(_) ->
                            match Ashes.Net.Tcp.close(sock) with
                                | Ok(_) -> Ashes.IO.print("ok")
                                | Error(_) -> Ashes.IO.print("fail")
                        | Error(_) -> Ashes.IO.print("fail")
            """,
            diag).ParseProgram();
        new Lowering(diag).Lower(program);

        diag.StructuredErrors.ShouldContain(
            x => x.Code == DiagnosticCodes.DoubleDrop,
            "Expected ASH007 (double-drop) diagnostic for closing socket twice.");
        diag.StructuredErrors.ShouldContain(
            x => x.Message.Contains("already been closed", StringComparison.Ordinal));
    }

    // --- Non-resource bindings are unaffected ---

    [Test]
    public void Non_resource_let_binding_does_not_emit_drop()
    {
        var ir = LowerProgram("let x = 42 in Ashes.IO.print(x)");

        var allInstructions = ir.EntryFunction.Instructions;
        HasAnyDropInstruction(allInstructions)
            .ShouldBeFalse("Non-resource bindings should not emit Drop instructions.");
    }

    [Test]
    public void String_let_binding_does_not_emit_drop()
    {
        var ir = LowerProgram("let s = \"hello\" in Ashes.IO.print(s)");

        var allInstructions = ir.EntryFunction.Instructions;
        HasAnyDropInstruction(allInstructions)
            .ShouldBeFalse("String bindings should not emit Drop instructions.");
    }

    [Test]
    public void List_binding_does_not_emit_drop()
    {
        var ir = LowerProgram("let xs = [1, 2, 3] in Ashes.IO.print(1)");

        var allInstructions = ir.EntryFunction.Instructions;
        HasAnyDropInstruction(allInstructions)
            .ShouldBeFalse("List bindings should not emit Drop instructions.");
    }

    [Test]
    public void Bool_binding_does_not_emit_drop()
    {
        var ir = LowerProgram("let b = true in Ashes.IO.print(1)");

        var allInstructions = ir.EntryFunction.Instructions;
        HasAnyDropInstruction(allInstructions)
            .ShouldBeFalse("Bool bindings should not emit Drop instructions.");
    }

    // --- Control-flow path drops ---

    [Test]
    public void Socket_in_match_with_multiple_branches_gets_dropped_in_all_paths()
    {
        var ir = LowerProgram(
            """
            match Ashes.Net.Tcp.connect("127.0.0.1")(80) with
                | Error(_) -> Ashes.IO.print("error")
                | Ok(sock) ->
                    if true then
                        Ashes.IO.print("a")
                    else
                        Ashes.IO.print("b")
            """);

        // The socket should be dropped at the end of the Ok branch
        var allInstructions = ir.EntryFunction.Instructions;
        HasDropInstruction(allInstructions, "Socket")
            .ShouldBeTrue("Socket should be dropped at the end of the match branch.");
    }

    // --- Diagnostic code values ---

    [Test]
    public void Diagnostic_code_ash006_has_correct_value()
    {
        DiagnosticCodes.UseAfterDrop.ShouldBe("ASH006");
    }

    [Test]
    public void Diagnostic_code_ash007_has_correct_value()
    {
        DiagnosticCodes.DoubleDrop.ShouldBe("ASH007");
    }

    // --- Helpers ---

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static bool HasDropInstruction(List<IrInst> instructions, string resourceTypeName)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.Drop drop && drop.ResourceTypeName == resourceTypeName)
                return true;
        }
        return false;
    }

    private static bool HasAnyDropInstruction(List<IrInst> instructions)
    {
        foreach (var inst in instructions)
        {
            if (inst is IrInst.Drop)
                return true;
        }
        return false;
    }
}
