using Ashes.Backend.Backends;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Links programs split into several partition objects through the ELF relocatable merge on both
/// Linux targets: the partitions call each other's functions by name, share partition 0's runtime
/// globals and string literals (the arm64 arena cursors among them live in TLS), and carry their
/// own constant pools, so every one of those must survive the merge for the programs to run.
/// </summary>
public sealed class ElfRelocatableObjectsTests
{
    private const string SplitProgram = """
        import Ashes.IO
        import Ashes.Text
        import Ashes.Collection.List
        let recursive range = given (from: Int, to: Int) -> if from > to then [] else from :: range(from + 1, to)
        let label = given (n: Int) ->
            match n % 3 with
                | 0 -> "fizz"
                | 1 -> "one"
                | _ -> Ashes.Text.fromInt(n)
        let parts = Ashes.Text.split("GET / HTTP/1.1")(" ")
        let total = Ashes.Collection.List.foldLeft(given (acc: Int, n: Int) -> acc + n)(0)(range(1, 10))
        in match parts with
            | method :: path :: version :: _rest ->
                Ashes.IO.print(method + "|" + path + "|" + version + "|" + Ashes.Text.fromInt(total) + "|" + Ashes.Text.join(", ")(Ashes.Collection.List.map(label)(range(1, 6))))
            | _other -> Ashes.IO.print("bad")
        """;

    private const string ExpectedOutput = "GET|/|HTTP/1.1|55|one, 2, fizz, one, 5, fizz\n";

    [Test]
    public async Task Elf_merge_should_link_a_linux_x64_program_split_into_several_objects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = IrOptimizer.Optimize(LinuxBackendCoverageTests.LowerProgramWithImports(SplitProgram));

        LinuxBackendCoverageTests.ExecutionResult result = await LinuxBackendCoverageTests.CompileRunWithLinuxLlvmAsync(
            program,
            compileOptions: BackendCompileOptions.Default with { ObjectPartitions = 3 }).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe(ExpectedOutput);
    }

    [Test]
    public async Task Elf_merge_should_link_a_linux_arm64_program_split_into_several_objects()
    {
        if (!LinuxArm64BackendCoverageTests.CanExecuteLinuxArm64())
        {
            return;
        }

        IrProgram program = IrOptimizer.Optimize(LinuxBackendCoverageTests.LowerProgramWithImports(SplitProgram));

        LinuxArm64BackendCoverageTests.ExecutionResult result = await LinuxArm64BackendCoverageTests.CompileRunWithLinuxArm64LlvmAsync(
            program,
            compileOptions: BackendCompileOptions.Default with { ObjectPartitions = 3 }).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe(ExpectedOutput);
    }

    [Test]
    public void Elf_merge_should_link_a_linux_arm64_image_from_merged_objects_without_an_emulator()
    {
        IrProgram program = IrOptimizer.Optimize(LinuxBackendCoverageTests.LowerProgramWithImports(SplitProgram));

        byte[] image = new LinuxArm64LlvmBackend().Compile(program, BackendCompileOptions.Default with { ObjectPartitions = 3 });

        image[0].ShouldBe((byte)0x7F);
        image[1].ShouldBe((byte)'E');
        image[2].ShouldBe((byte)'L');
        image[3].ShouldBe((byte)'F');
    }
}
