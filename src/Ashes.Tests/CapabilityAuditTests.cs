using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// <see cref="Lowering.PublicApiCapabilities"/> — the compiler surface the registry reuses for its
/// capability audit.
/// </summary>
public sealed class CapabilityAuditTests
{
    [Test]
    public void Exported_function_needing_a_capability_reports_it()
    {
        var caps = CapabilitiesOf(
            """
            capability Log =
                | write : Str -> Unit

            let emit : Str -> Unit needs {Log} =
                given (m) -> Log.write(m)
            """);

        caps.ShouldContain("Log");
    }

    [Test]
    public void A_pure_package_reports_no_capabilities()
    {
        var caps = CapabilitiesOf(
            """
            let inc = given (x) -> x + 1
            let twice = given (x) -> inc(inc(x))
            """);

        caps.ShouldBeEmpty();
    }

    private static IReadOnlyList<string> CapabilitiesOf(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        var lowering = new Lowering(diag);
        lowering.Lower(program);
        return lowering.PublicApiCapabilities();
    }
}
