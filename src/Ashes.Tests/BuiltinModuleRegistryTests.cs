using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class BuiltinModuleRegistryTests
{
    [Test]
    public void Ashes_root_module_is_reserved_but_has_no_value_members()
    {
        BuiltinRegistry.TryGetModule("Ashes", out var module).ShouldBeTrue();
        module.Name.ShouldBe("Ashes");
        module.Members.ShouldBeEmpty();
    }

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
    public void Ashes_http_module_is_registered()
    {
        BuiltinRegistry.TryGetModule("Ashes.Http", out var module).ShouldBeTrue();
        module.Name.ShouldBe("Ashes.Http");
        module.Members.ContainsKey("get").ShouldBeTrue();
        module.Members.ContainsKey("post").ShouldBeTrue();
    }

    [Test]
    public void Ashes_http_is_known_standard_library_module()
    {
        ProjectSupport.IsStdModule("Ashes.Http").ShouldBeTrue();
        ProjectSupport.KnownStandardLibraryModules.ShouldContain("Ashes.Http");
    }

    [Test]
    public void Ashes_text_module_is_registered()
    {
        BuiltinRegistry.TryGetModule("Ashes.Text", out var module).ShouldBeTrue();
        module.Name.ShouldBe("Ashes.Text");
        module.Members.ContainsKey("uncons").ShouldBeTrue();
        module.Members.ContainsKey("parseInt").ShouldBeTrue();
        module.Members.ContainsKey("parseFloat").ShouldBeTrue();
    }

    [Test]
    public void Ashes_text_is_known_standard_library_module()
    {
        ProjectSupport.IsStdModule("Ashes.Text").ShouldBeTrue();
        ProjectSupport.KnownStandardLibraryModules.ShouldContain("Ashes.Text");
    }

    [Test]
    public void Ashes_text_builtins_typecheck_through_maybe_and_result_flow()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            """
            match Ashes.Text.uncons("ab") with
                | None -> Ashes.IO.print("none")
                | Some((head, tail)) ->
                    match Ashes.Text.parseInt("123") with
                        | Error(message) -> Ashes.IO.print(message)
                        | Ok(value) -> Ashes.IO.print(value)
            """,
            diag).ParseProgram();
        var lowering = new Lowering(diag);

        lowering.Lower(program);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Ashes_http_builtins_typecheck_through_result_flow()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            """
            Ashes.IO.print(match Ashes.Async.run(async
                await Ashes.Http.get("http://example.com")) with
                | Error(_) -> "fail"
                | Ok(text) -> text)
            """,
            diag).ParseProgram();
        var lowering = new Lowering(diag);

        lowering.Lower(program);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Ashes_net_tcp_builtins_typecheck_through_result_flow()
    {
        var diag = new Diagnostics();
        var program = new Parser(
            """
            Ashes.IO.print(match Ashes.Async.run(async
                let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(80)
                in
                    let _ = await Ashes.Net.Tcp.close(sock)
                    in "ok") with
                | Error(_) -> "fail"
                | Ok(text) -> text)
            """,
            diag).ParseProgram();
        var lowering = new Lowering(diag);

        lowering.Lower(program);

        diag.Errors.ShouldBeEmpty();
    }

    [Test]
    public void Ashes_test_module_is_registered()
    {
        BuiltinRegistry.TryGetModule("Ashes.Test", out var module).ShouldBeTrue();
        module.Name.ShouldBe("Ashes.Test");
        module.ResourceName.ShouldBe("Ashes.Semantics.StdLib.Ashes.Test.ash");
    }

    [Test]
    public void Ashes_async_module_is_registered()
    {
        BuiltinRegistry.TryGetModule("Ashes.Async", out var module).ShouldBeTrue();
        module.Name.ShouldBe("Ashes.Async");
        module.Members.ContainsKey("run").ShouldBeTrue();
        module.Members.ContainsKey("fromResult").ShouldBeTrue();
    }

    [Test]
    public void Task_builtin_type_is_registered()
    {
        BuiltinRegistry.TryGetType("Task", out var taskType).ShouldBeTrue();
        taskType.Name.ShouldBe("Task");
        taskType.TypeParameters.Count.ShouldBe(2);
        taskType.TypeParameters[0].Name.ShouldBe("E");
        taskType.TypeParameters[1].Name.ShouldBe("A");
    }

    [Test]
    public void Task_is_a_reserved_type_name()
    {
        BuiltinRegistry.IsReservedTypeName("Task").ShouldBeTrue();
    }
}
