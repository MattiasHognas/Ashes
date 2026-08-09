using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LogicalNotTests
{
    [Test]
    public void Logical_not_lowers_to_a_boolean_comparison()
    {
        (IrProgram ir, Diagnostics diagnostics) = LowerProgram("!true");

        diagnostics.Errors.ShouldBeEmpty();
        ir.EntryFunction.Instructions.ShouldContain(instruction => instruction is IrInst.CmpIntEq);
    }

    [Test]
    public void Logical_not_rejects_non_boolean_operands()
    {
        (_, Diagnostics diagnostics) = LowerProgram("!1");

        diagnostics.Errors.ShouldContain(
            error => error.Contains("'!' requires Bool, got Int.", StringComparison.Ordinal));
        diagnostics.StructuredErrors.ShouldContain(error => string.Equals(error.Code, "ASH002", StringComparison.Ordinal));
    }

    private static (IrProgram Ir, Diagnostics Diagnostics) LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        return (ir, diagnostics);
    }
}
