using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ApplicationDiagnosticsTests
{
    [Test]
    public void Oversaturated_call_reports_expected_and_provided_argument_counts()
    {
        var diag = LowerExpression("let add = fun (x, y) -> x + y in Ashes.IO.print(add(1, 2, 3))");

        diag.Errors.ShouldContain(x => x.Contains("Call to 'add' expects 2 argument(s) but got 3.", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("print() does not support type Never yet.", StringComparison.Ordinal));
    }

    [Test]
    public void Calling_partial_value_with_too_many_arguments_reports_remaining_arity()
    {
        var diag = LowerExpression("let add = fun (x, y) -> x + y in let add1 = add(1) in Ashes.IO.print(add1(1, 2))");

        diag.Errors.ShouldContain(x => x.Contains("Call to 'add1' expects 1 argument(s) but got 2.", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("print() does not support type Never yet.", StringComparison.Ordinal));
    }

    [Test]
    public void Calling_non_function_reports_callee_name_and_type()
    {
        var diag = LowerExpression("let x = 1 in Ashes.IO.print(x(1))");

        diag.Errors.ShouldContain(x => x.Contains("Attempted to call 'x' with 1 argument(s), but its type is Int, not a function.", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("expects 0 argument(s)", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("print() does not support type Never yet.", StringComparison.Ordinal));
    }

    [Test]
    public void Call_argument_type_mismatch_reports_argument_context()
    {
        var diag = LowerExpression("let add = fun (x, y) -> x + y in Ashes.IO.print(add(1, \"x\"))");

        diag.Errors.ShouldContain(x =>
            x.Contains("Type mismatch: Int vs Str.", StringComparison.Ordinal)
            && x.Contains("Context: in argument #2 of call to 'add'.", StringComparison.Ordinal));
    }

    [Test]
    public void Unqualified_print_requires_import_of_Ashes_IO()
    {
        var diag = LowerExpression("print(1)", importAshesIO: false);

        diag.Errors.ShouldContain(x => x.Contains("Undefined variable 'print'.", StringComparison.Ordinal));
    }

    [Test]
    public void Unqualified_panic_requires_import_of_Ashes_IO()
    {
        var diag = LowerExpression("panic(\"boom\")", importAshesIO: false);

        diag.Errors.ShouldContain(x => x.Contains("Undefined variable 'panic'.", StringComparison.Ordinal));
    }

    [Test]
    public void Unqualified_args_requires_import_of_Ashes_IO()
    {
        var diag = LowerExpression("args", importAshesIO: false);

        diag.Errors.ShouldContain(x => x.Contains("Undefined variable 'args'.", StringComparison.Ordinal));
    }

    [Test]
    public void Qualified_Ashes_IO_access_does_not_open_unqualified_names()
    {
        var diag = LowerExpression("let x = Ashes.IO.args in args", importAshesIO: false);

        diag.Errors.ShouldContain(x => x.Contains("Undefined variable 'args'.", StringComparison.Ordinal));
    }

    [Test]
    public void Unqualified_writeLine_is_not_available_even_with_Ashes_IO_import()
    {
        var diag = LowerExpression("writeLine(\"hello\")", importAshesIO: true);

        diag.Errors.ShouldContain(x => x.Contains("Undefined variable 'writeLine'. Did you mean 'Ashes.IO.writeLine'?", StringComparison.Ordinal));
    }

    [Test]
    public void Unqualified_readLine_is_not_available_even_with_Ashes_IO_import()
    {
        var diag = LowerExpression("readLine()", importAshesIO: true);

        diag.Errors.ShouldContain(x => x.Contains("Undefined variable 'readLine'. Did you mean 'Ashes.IO.readLine'?", StringComparison.Ordinal));
    }

    [Test]
    public void Unknown_builtin_module_member_reports_member_diagnostic()
    {
        var diag = LowerExpression("Ashes.IO.nope(\"hello\")", importAshesIO: true);

        diag.Errors.ShouldContain(x => x.Contains("Unknown member 'nope' in module Ashes.IO.", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("Unknown module 'Ashes.IO'.", StringComparison.Ordinal));
    }

    [Test]
    public void Unknown_builtin_fs_module_member_reports_member_diagnostic()
    {
        var diag = LowerExpression("Ashes.Fs.nope(\"hello\")", importAshesIO: false);

        diag.Errors.ShouldContain(x => x.Contains("Unknown member 'nope' in module Ashes.Fs.", StringComparison.Ordinal));
        diag.Errors.ShouldNotContain(x => x.Contains("Unknown module 'Ashes.Fs'.", StringComparison.Ordinal));
    }

    [Test]
    public void Mixed_float_and_int_operands_report_clear_binary_operator_diagnostic()
    {
        var diag = LowerExpression("1 + 2.0", importAshesIO: false);

        diag.Errors.ShouldContain(x => x.Contains("'+' requires Int+Int, Float+Float, or Str+Str, got Int and Float.", StringComparison.Ordinal));
    }

    private static Diagnostics LowerExpression(string source, bool importAshesIO = true)
    {
        var diag = new Diagnostics();
        var expr = new Parser(source, diag).ParseExpression();
        var importedStdModules = importAshesIO
            ? new HashSet<string>(StringComparer.Ordinal) { "Ashes.IO" }
            : null;
        var lowering = new Lowering(diag, importedStdModules);
        lowering.Lower(expr);
        return diag;
    }
}
