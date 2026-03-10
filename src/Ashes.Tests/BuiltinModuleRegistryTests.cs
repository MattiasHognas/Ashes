using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class BuiltinModuleRegistryTests
{
    [Test]
    public void Ashes_net_tcp_module_is_registered()
    {
        BuiltinRegistry.TryGetModule("Ashes.Net.Tcp", out var module).ShouldBeTrue();
        module.Name.ShouldBe("Ashes.Net.Tcp");
        module.Members.ContainsKey("connect").ShouldBeTrue();
        module.Members.ContainsKey("send").ShouldBeTrue();
        module.Members.ContainsKey("receive").ShouldBeTrue();
        module.Members.ContainsKey("close").ShouldBeTrue();
    }

    [Test]
    public void Ashes_net_tcp_is_known_standard_library_module()
    {
        ProjectSupport.IsStdModule("Ashes.Net.Tcp").ShouldBeTrue();
        ProjectSupport.KnownStandardLibraryModules.ShouldContain("Ashes.Net.Tcp");
    }

    [Test]
    public void Ashes_net_tcp_builtins_typecheck_through_result_flow()
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
        var lowering = new Lowering(diag);

        lowering.Lower(program);

        diag.Errors.ShouldBeEmpty();
    }
}
