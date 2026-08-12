using System.Diagnostics;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ExternalResourceNativeTests
{
    [Test]
    public async Task Declared_resource_destructor_runs_once_per_recursive_scope()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ashes-affine-resource",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string libraryPath = Path.Combine(tempDirectory, "libashes_affine_resource.so");
            await BuildFixtureAsync(libraryPath).ConfigureAwait(false);
            string source = $$"""
                external type Counted resource destructor closeCounted
                external openCounted(Int) -> Counted = "ashes_resource_open@{{libraryPath}}"
                external readCounted(borrow Counted) -> Int = "ashes_resource_read@{{libraryPath}}"
                external closeCounted(consume Counted) -> void = "ashes_resource_close@{{libraryPath}}"

                let recursive visit n =
                    if n == 0 then 0
                    else
                        let resource = openCounted(n) in
                            let _ = Ashes.IO.print(readCounted(resource)) in
                                visit(n - 1)

                Ashes.IO.print(visit(3))
                """;

            string output = await CompileAndRunAsync(source, tempDirectory).ConfigureAwait(false);

            output.ShouldBe("3\nclosed 3\n2\nclosed 2\n1\nclosed 1\n0\n");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Repeated_declared_resource_construction_keeps_live_count_bounded()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ashes-affine-resource",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string libraryPath = Path.Combine(tempDirectory, "libashes_affine_resource.so");
            await BuildFixtureAsync(libraryPath).ConfigureAwait(false);
            string source = $$"""
                external type Counted resource destructor closeCounted
                external openCounted(Int) -> Counted = "ashes_resource_open@{{libraryPath}}"
                external closeCounted(consume Counted) -> void = "ashes_resource_close@{{libraryPath}}"
                external liveCount() -> Int = "ashes_resource_live@{{libraryPath}}"

                let recursive visit n =
                    if n == 0 then 0
                    else let resource = openCounted(-1) in visit(n - 1)

                let _ = visit(10000) in Ashes.IO.print(liveCount(Unit))
                """;

            string output = await CompileAndRunAsync(source, tempDirectory).ConfigureAwait(false);
            output.ShouldBe("0\n");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Declared_resource_control_flow_and_capture_paths_close_exactly_once()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ashes-affine-resource",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string libraryPath = Path.Combine(tempDirectory, "libashes_affine_resource.so");
            await BuildFixtureAsync(libraryPath).ConfigureAwait(false);
            string output = await CompileAndRunAsync(
                BuildControlFlowSource(libraryPath),
                tempDirectory).ConfigureAwait(false);
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (int value in new[] { 10, 20, 30, 40, 50 })
            {
                lines.Count(line => string.Equals(
                    line,
                    $"closed {value}",
                    StringComparison.Ordinal)).ShouldBe(1, output);
            }
            lines[^1].ShouldBe("0", output);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string BuildControlFlowSource(string libraryPath) => $$"""
        external type Counted resource destructor closeCounted
        external openCounted(Int) -> Counted = "ashes_resource_open@{{libraryPath}}"
        external readCounted(borrow Counted) -> Int = "ashes_resource_read@{{libraryPath}}"
        external closeCounted(consume Counted) -> void = "ashes_resource_close@{{libraryPath}}"
        external liveCount() -> Int = "ashes_resource_live@{{libraryPath}}"

        let branch flag =
            let resource = if flag then openCounted(10) else openCounted(11) in
                readCounted(resource)

        let matched = match Some(openCounted(20)) with
            | Some(resource) -> readCounted(resource)
            | None -> 0

        let fallible flag =
            if flag then Ok(openCounted(30)) else Error("not opened")

        let returned = match fallible(true) with
            | Ok(resource) -> readCounted(resource)
            | Error(_message) -> 0

        let explicitlyClosed =
            let resource = openCounted(40) in
                let value = readCounted(resource) in
                    let _ = closeCounted(resource) in value

        let makeReader unit =
            let resource = openCounted(50) in
                given ignored -> readCounted(resource)

        let readThroughClosure unit =
            let reader = makeReader(Unit) in reader(Unit)

        let _ = branch(true) in
            let _ = matched + returned + explicitlyClosed + readThroughClosure(Unit) in
                    Ashes.IO.print(liveCount(Unit))
        """;

    private static async Task BuildFixtureAsync(string libraryPath)
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "affine_resource.c");
        ProcessStartInfo startInfo = new("clang")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-shared");
        startInfo.ArgumentList.Add("-fPIC");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(libraryPath);
        using Process process = await TestProcessHelper.StartProcessAsync(startInfo).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.ExitCode.ShouldBe(0, stderr);
    }

    private static async Task<string> CompileAndRunAsync(string source, string tempDirectory)
    {
        var diagnostics = new Diagnostics();
        Frontend.Program ast = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        IReadOnlyList<IrInst.CleanupResource> cleanups = [.. ir.Functions.Prepend(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.CleanupResource>()];
        cleanups.ShouldContain(cleanup =>
            cleanup.Destructor != null
            && cleanup.Destructor.DestructorForResource == "Counted");

        string executablePath = Path.Combine(tempDirectory, "resource-test");
        TestProcessHelper.WriteExecutable(executablePath, new LinuxX64LlvmBackend().Compile(ir));
        ProcessStartInfo startInfo = new(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = await TestProcessHelper.StartProcessAsync(startInfo).ConfigureAwait(false);
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.ExitCode.ShouldBe(0, stderr);
        return stdout;
    }
}
