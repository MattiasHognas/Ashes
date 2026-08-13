using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class AmbientAuthorityCapabilityTests
{
    [Test]
    [Arguments("Ashes.IO.print(1)", "ConsoleIO")]
    [Arguments("Ashes.IO.panic(\"stop\")", "ConsoleIO")]
    [Arguments("Ashes.IO.write(\"x\")", "ConsoleIO")]
    [Arguments("Ashes.IO.writeBytes(Ashes.Byte.empty(Unit))", "ConsoleIO")]
    [Arguments("Ashes.IO.writeLine(\"x\")", "ConsoleIO")]
    [Arguments("Ashes.IO.writeBuffered(\"x\")", "ConsoleIO")]
    [Arguments("Ashes.IO.writeBufferedLine(\"x\")", "ConsoleIO")]
    [Arguments("Ashes.IO.flush(Unit)", "ConsoleIO")]
    [Arguments("Ashes.IO.readLine(Unit)", "ConsoleIO")]
    [Arguments("Ashes.IO.readExact(1)", "ConsoleIO")]
    [Arguments("Ashes.IO.Console.enableRawInput(Unit)", "ConsoleIO")]
    [Arguments("Ashes.IO.Console.restoreInput(Unit)", "ConsoleIO")]
    [Arguments("Ashes.IO.Console.pollInput(0)", "ConsoleIO")]
    [Arguments("Ashes.IO.File.readText(\"x\")", "FileRead")]
    [Arguments("Ashes.IO.File.readAllBytes(\"x\")", "FileRead")]
    [Arguments("Ashes.IO.File.mmap(\"x\")", "FileRead")]
    [Arguments("Ashes.IO.File.exists(\"x\")", "FileRead")]
    [Arguments("Ashes.IO.File.open(\"x\")", "FileRead")]
    [Arguments("Ashes.IO.File.writeText(\"x\")(\"y\")", "FileWrite")]
    [Arguments("Ashes.IO.File.writeBytes(\"x\")(Ashes.Byte.empty(Unit))", "FileWrite")]
    [Arguments("Ashes.IO.File.replace(\"x\")(\"y\")", "FileWrite")]
    [Arguments("Ashes.IO.File.makeExecutable(\"x\")", "FileWrite")]
    [Arguments("Ashes.IO.Directory.entries(\"x\")", "FileRead")]
    [Arguments("Ashes.IO.Directory.createAll(\"x\")", "FileWrite")]
    [Arguments("Ashes.IO.Directory.removeTree(\"x\")", "FileWrite")]
    [Arguments("Ashes.IO.Process.spawn(\"tool\")([])", "ProcessSpawn")]
    [Arguments("Ashes.IO.Console.monotonicMillis(Unit)", "TimeRead")]
    [Arguments("Ashes.IO.Environment.currentDirectory(Unit)", "EnvironmentRead")]
    [Arguments("Ashes.IO.Environment.executableDirectory(Unit)", "EnvironmentRead")]
    [Arguments("Ashes.IO.Environment.temporaryDirectory(Unit)", "EnvironmentRead")]
    [Arguments("Ashes.IO.Environment.cacheDirectory(Unit)", "EnvironmentRead")]
    [Arguments("Ashes.IO.Environment.get(\"PATH\")", "EnvironmentRead")]
    public void Ambient_acquisition_builtin_exposes_expected_public_capability(
        string expression,
        string expected)
    {
        (Lowering lowering, Diagnostics diagnostics) = Lower($$"""
            let acquire : Unit -> Unit needs {{{expected}}} =
                given unit -> let _ = {{expression}} in Unit
            0
            """);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.PublicApiCapabilities().ShouldBe([expected]);
    }

    [Test]
    public void Closed_pure_row_rejects_higher_order_hidden_ambient_authority()
    {
        (_, Diagnostics diagnostics) = Lower("""
            let invoke : (Unit -> Int needs {TimeRead}) -> Int needs {TimeRead} =
                given f -> f(Unit)
            let pure : Unit -> Int needs {} =
                given unit -> invoke(given ignored -> Ashes.IO.Console.monotonicMillis(Unit))
            0
            """);

        diagnostics.StructuredErrors.ShouldContain(error =>
            error.Code == "ASH018"
            && error.Message.Contains("TimeRead", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("Ashes.IO.print(1)", "ConsoleIO", "")]
    [Arguments("Ashes.IO.File.readText(\"x\")", "FileRead", "")]
    [Arguments("Ashes.IO.File.writeText(\"x\")(\"y\")", "FileWrite", "")]
    [Arguments("Ashes.IO.Process.spawn(\"tool\")([])", "ProcessSpawn", "")]
    [Arguments("Ashes.IO.Console.monotonicMillis(Unit)", "TimeRead", "")]
    [Arguments("Ashes.IO.Environment.currentDirectory(Unit)", "EnvironmentRead", "")]
    [Arguments("entropy(1)", "Entropy", "external entropy(Int) -> Int needs {Entropy}")]
    [Arguments("unknown(1)", "UnsafeFfi", "external unknown(Int) -> Int")]
    [Arguments("Ashes.Ffi.copyBytes(pointer())(0u64)", "UnsafeFfi", "external pointer() -> *u8 needs {}")]
    [Arguments("Ashes.Net.Tcp.connect(\"localhost\")(80)", "NetConnect", "")]
    [Arguments("Ashes.Net.Tcp.Server.listen(8080)", "NetListen", "")]
    public void Closed_pure_row_rejects_each_ambient_authority_category(
        string expression,
        string expected,
        string declaration)
    {
        (_, Diagnostics diagnostics) = Lower($$"""
            {{declaration}}
            let bad : Unit -> Unit needs {} =
                given unit -> let _ = {{expression}} in Unit
            0
            """);

        diagnostics.StructuredErrors.ShouldContain(error =>
            error.Code == "ASH018"
            && error.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Test]
    public void Trait_method_cannot_hide_ambient_authority_from_a_closed_caller()
    {
        (_, Diagnostics diagnostics) = Lower("""
            trait Tick(a) =
                | tick : a -> Int needs {TimeRead}
            implement Tick(Unit) =
                | tick = given unit -> Ashes.IO.Console.monotonicMillis(Unit)
            let bad : Unit -> Int needs {} =
                given unit -> Tick.tick(unit)
            0
            """);

        diagnostics.StructuredErrors.ShouldContain(error =>
            error.Code == "ASH018"
            && error.Message.Contains("TimeRead", StringComparison.Ordinal));
    }

    [Test]
    public void Recursive_and_async_functions_preserve_ambient_rows()
    {
        (Lowering lowering, Diagnostics diagnostics) = Lower("""
            let recursive timed : Int -> Int needs {TimeRead} =
                given count ->
                    if count <= 0 then Ashes.IO.Console.monotonicMillis(Unit)
                    else timed(count - 1)
            let later : Unit -> Task(Str, Int) needs {TimeRead} =
                given unit -> async(Ashes.IO.Console.monotonicMillis(Unit))
            0
            """);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.PublicApiCapabilities().ShouldBe(["TimeRead"]);
    }

    [Test]
    public void Project_stitching_preserves_imported_ambient_rows()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ashes-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "ashes.json"),
                """{"name":"authority","entry":"Main.ash","sourceRoots":["."]}""");
            File.WriteAllText(Path.Combine(root, "Clock.ash"), """
                let now : Unit -> Int needs {TimeRead} =
                    given unit -> Ashes.IO.Console.monotonicMillis(Unit)
                """);
            File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Clock
                let bad : Unit -> Int needs {} = given unit -> Clock.now(unit)
                0
                """);

            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(
                ProjectSupport.LoadProject(Path.Combine(root, "ashes.json")));
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
            Lowering lowering = new(diagnostics, plan.ImportedStdModules, plan.MergedAliases, layout.ConstructorModules);
            lowering.SetSourceContext(layout);
            _ = lowering.Lower(program);

            diagnostics.StructuredErrors.ShouldContain(error =>
                error.Code == "ASH018"
                && error.Message.Contains("TimeRead", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Possession_only_resource_operations_remain_capability_free()
    {
        (Lowering lowering, Diagnostics diagnostics) = Lower("""
            let readFile : FileHandle -> Maybe(Str) needs {} =
                given file -> Ashes.IO.File.readLine(file)
            let wait : Process -> Int needs {} =
                given process -> Ashes.IO.Process.waitForExit(process)
            let receive : Socket -> Int -> Task(Str, Str) needs {} =
                given socket -> given count -> Ashes.Net.Tcp.receive(socket)(count)
            0
            """);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.PublicApiCapabilities().ShouldBeEmpty();
    }

    [Test]
    public void External_defaults_to_unsafe_ffi_and_explicit_row_reclassifies_it()
    {
        (Lowering lowering, Diagnostics diagnostics) = Lower("""
            external unknown(Int) -> Int
            external readNative(Int) -> Int needs {FileRead}
            external pureNative(Int) -> Int needs {}
            let unsafeCall x = unknown(x)
            let readCall x = readNative(x)
            let pureCall x = pureNative(x)
            0
            """);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.PublicApiCapabilities().ShouldBe(["FileRead", "UnsafeFfi"]);
    }

    [Test]
    public void Declared_resource_destructor_defaults_to_possession_only_empty_row()
    {
        (Lowering lowering, Diagnostics diagnostics) = Lower("""
            external type NativeHandle resource destructor destroy
            external destroy(consume NativeHandle) -> void
            0
            """);

        diagnostics.StructuredErrors.ShouldBeEmpty();
        lowering.GetDecisionSnapshot().ExternalAuthority
            .Single(external => string.Equals(external.Function, "destroy", StringComparison.Ordinal))
            .Capabilities.ShouldBeEmpty();
    }

    [Test]
    public void External_row_rejects_user_capabilities_and_open_tails()
    {
        (_, Diagnostics diagnostics) = Lower("""
            capability Audit = | record : Int -> Unit
            external badUser(Int) -> Int needs {Audit}
            external badOpen(Int) -> Int needs {FileRead | e}
            0
            """);

        diagnostics.StructuredErrors.Count(error => string.Equals(error.Code, "ASH019", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Test]
    public void Every_runtime_capability_name_is_reserved()
    {
        string[] names =
        [
            "ConsoleIO", "FileRead", "FileWrite", "ProcessSpawn", "TimeRead", "EnvironmentRead", "Entropy",
            "UnsafeFfi", "NetListen", "NetConnect", "Stop",
        ];

        foreach (string name in names)
        {
            (_, Diagnostics diagnostics) = Lower($"capability {name} = | use : Unit -> Unit\n0");
            diagnostics.Errors.ShouldContain(error =>
                error.Contains("reserved for a built-in runtime capability", StringComparison.Ordinal));
        }
    }

    private static (Lowering Lowering, Diagnostics Diagnostics) Lower(string source)
    {
        var diagnostics = new Diagnostics();
        Ashes.Frontend.Program syntax = new Parser(source, diagnostics).ParseProgram();
        var lowering = new Lowering(diagnostics);
        lowering.Lower(syntax);
        return (lowering, diagnostics);
    }
}
