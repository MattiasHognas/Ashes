using System.Buffers.Binary;
using System.Diagnostics;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class FfiOutNativeTests
{
    [Test]
    public async Task Ffi_out_marshals_success_failure_null_and_multiple_outputs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), "ashes-ffi-out", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string libraryPath = Path.Combine(tempDirectory, "libashes_ffi_out.so");
            await BuildFixtureAsync(libraryPath).ConfigureAwait(false);
            string source = BuildSource(libraryPath);

            string output = await CompileAndRunAsync(source, tempDirectory).ConfigureAwait(false);

            output.ShouldBe("7\n101\nnone\n8\n-1\nsome\n0\nnone\n1\nsome\n9\nsome\n303\n10\n404\nsome\n");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Ffi_out_calls_lower_for_every_target_abi()
    {
        IrProgram linux = LowerAllFourCalls("libllvm_ffi_out_fixture.so");
        ReadElfMachine(new LinuxX64LlvmBackend().Compile(linux)).ShouldBe((ushort)62);
        ReadElfMachine(new LinuxArm64LlvmBackend().Compile(linux)).ShouldBe((ushort)183);

        IrProgram windows = LowerAllFourCalls("llvm_ffi_out_fixture.dll");
        ReadPeMachine(new WindowsX64LlvmBackend().Compile(windows)).ShouldBe((ushort)0x8664);
        ReadPeMachine(new WindowsArm64LlvmBackend().Compile(windows)).ShouldBe((ushort)0xAA64);
    }

    private static IrProgram LowerAllFourCalls(string library)
    {
        Diagnostics diagnostics = new();
        Frontend.Program ast = new Parser(BuildSource(library), diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static string BuildSource(string library) => $$"""
        external type Handle
        external handleAt(Int) -> Handle = "ashes_ffi_out_handle_at@{{library}}"
        external readHandle(Handle) -> Int = "ashes_ffi_out_read@{{library}}"
        external LLVMGetTargetFromTriple(Str, out Handle, out *u8) -> u32 = "LLVMGetTargetFromTriple@{{library}}"
        external LLVMVerifyModule(Handle, u32, out *u8) -> u32 = "LLVMVerifyModule@{{library}}"
        external LLVMTargetMachineEmitToMemoryBuffer(Handle, Handle, u32, Str, out *u8, out Handle) -> u32 = "LLVMTargetMachineEmitToMemoryBuffer@{{library}}"
        external LLVMParseIRInContext(Handle, Handle, out Handle, out *u8) -> u32 = "LLVMParseIRInContext@{{library}}"

        let printHandle maybe =
            match maybe with
                | None -> Ashes.IO.print(-1)
                | Some(native) -> Ashes.IO.print(readHandle(native))
        let printPointer maybe =
            match maybe with
                | None -> Ashes.IO.print("none")
                | Some(_) -> Ashes.IO.print("some")
        let first2 result =
            match result with
                | (first, _) -> first
        let second2 result =
            match result with
                | (_, second) -> second
        let first3 result =
            match result with
                | (first, _, _) -> first
        let second3 result =
            match result with
                | (_, second, _) -> second
        let third3 result =
            match result with
                | (_, _, third) -> third
        let seed = handleAt(1)
        let targetResult = LLVMGetTargetFromTriple("success")
        let printedTargetStatus = Ashes.IO.print(first3(targetResult))
        let printedTarget = printHandle(second3(targetResult))
        let printedTargetMessage = printPointer(third3(targetResult))
        let failureResult = LLVMGetTargetFromTriple("failure")
        let printedFailureStatus = Ashes.IO.print(first3(failureResult))
        let printedMissingTarget = printHandle(second3(failureResult))
        let printedFailureMessage = printPointer(third3(failureResult))
        let verifyResult = LLVMVerifyModule(seed, 0u32)
        let printedVerifyStatus = Ashes.IO.print(first2(verifyResult))
        let printedNoVerifyMessage = printPointer(second2(verifyResult))
        let failedVerifyResult = LLVMVerifyModule(seed, 1u32)
        let printedFailedVerifyStatus = Ashes.IO.print(first2(failedVerifyResult))
        let printedVerifyMessage = printPointer(second2(failedVerifyResult))
        let emitResult = LLVMTargetMachineEmitToMemoryBuffer(seed, seed, 0u32, "out.o")
        let printedEmitStatus = Ashes.IO.print(first3(emitResult))
        let printedEmitMessage = printPointer(second3(emitResult))
        let printedBuffer = printHandle(third3(emitResult))
        let parseResult = LLVMParseIRInContext(seed, seed)
        let printedParseStatus = Ashes.IO.print(first3(parseResult))
        let printedModule = printHandle(second3(parseResult))
        printPointer(third3(parseResult))
        """;

    private static ushort ReadElfMachine(byte[] image)
    {
        image.AsSpan(0, 4).ToArray().ShouldBe(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
        return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(18, 2));
    }

    private static ushort ReadPeMachine(byte[] image)
    {
        image.AsSpan(0, 2).ToArray().ShouldBe(new byte[] { (byte)'M', (byte)'Z' });
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
        return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(peOffset + 4, 2));
    }

    private static async Task BuildFixtureAsync(string libraryPath)
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffi_out.c");
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
        IrProgram ir = LowerAllFourCallsFromSource(source);
        string executablePath = Path.Combine(tempDirectory, "ffi-out-test");
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

    private static IrProgram LowerAllFourCallsFromSource(string source)
    {
        Diagnostics diagnostics = new();
        Frontend.Program ast = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
