using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ZeroCostTypeTests
{
    [Test]
    public void Alias_expands_without_registering_a_nominal_symbol()
    {
        (Lowering lowering, IrProgram _, Diagnostics diagnostics) = Lower(
            "type alias Identity(a) = a\n" +
            "let identity (value: Identity(Int)) = value\n" +
            "identity(42)");

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.TypeSymbols.ContainsKey("Identity").ShouldBeFalse();
    }

    [Test]
    public void Zero_cost_construction_and_pattern_emit_no_wrapper_operations()
    {
        (Lowering lowering, IrProgram program, Diagnostics diagnostics) = Lower(
            "type UserId = UserId(Int)\n" +
            "let unwrap id = match id with | UserId(value) -> value\n" +
            "unwrap(UserId(42))");

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.TypeSymbols["UserId"].IsZeroCost.ShouldBeTrue();
        IrFunction unwrap = program.Functions.Single(function => string.Equals(
            function.Origin?.Source?.SourceName,
            "unwrap",
            StringComparison.Ordinal));
        unwrap.Instructions.Any(instruction => instruction is
            IrInst.AllocAdt or IrInst.GetAdtTag or IrInst.GetAdtField or IrInst.SetAdtField)
            .ShouldBeFalse();
    }

    [Test]
    public void Same_payload_zero_cost_types_remain_nominally_distinct()
    {
        (_, _, Diagnostics diagnostics) = Lower(
            "type UserId = UserId(Int)\n" +
            "type OrderId = OrderId(Int)\n" +
            "let useUser (value: UserId) = value\n" +
            "useUser(OrderId(42))");

        diagnostics.StructuredErrors.ShouldContain(error =>
            error.Code == DiagnosticCodes.TypeMismatch
            && error.Message.Contains("UserId vs OrderId", StringComparison.Ordinal));
    }

    private static (Lowering Lowering, IrProgram Program, Diagnostics Diagnostics) Lower(string source)
    {
        var diagnostics = new Diagnostics();
        Ashes.Frontend.Program syntax = new Parser(source, diagnostics).ParseProgram();
        var lowering = new Lowering(diagnostics);
        IrProgram program = lowering.Lower(syntax);
        return (lowering, program, diagnostics);
    }
}
