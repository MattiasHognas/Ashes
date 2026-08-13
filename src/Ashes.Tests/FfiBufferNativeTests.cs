using System.Diagnostics;
using System.Buffers.Binary;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class FfiBufferNativeTests
{
    [Test]
    public async Task Ffi_buffer_marshals_opaque_handle_lists_and_empty_lists()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ashes-ffi-buffer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string libraryPath = Path.Combine(tempDirectory, "libashes_ffi_buffer.so");
            await BuildFixtureAsync(libraryPath).ConfigureAwait(false);
            string source = $$"""
                external type Handle
                external makeHandle(Int) -> Handle = "ashes_ffi_buffer_make@{{libraryPath}}"
                external readHandle(Handle) -> Int = "ashes_ffi_buffer_read@{{libraryPath}}"
                external LLVMFunctionType(Handle, FfiBuffer(Handle), u32, Bool) -> Handle = "LLVMFunctionType@{{libraryPath}}"
                external LLVMBuildCall2(Handle, Handle, Handle, FfiBuffer(Handle), u32, Str) -> Handle = "LLVMBuildCall2@{{libraryPath}}"
                external LLVMBuildGEP2(Handle, Handle, Handle, FfiBuffer(Handle), u32, Str) -> Handle = "LLVMBuildGEP2@{{libraryPath}}"
                external LLVMConstArray2(Handle, FfiBuffer(Handle), u64) -> Handle = "LLVMConstArray2@{{libraryPath}}"
                external LLVMConstStructInContext(Handle, FfiBuffer(Handle), u32, Bool) -> Handle = "LLVMConstStructInContext@{{libraryPath}}"
                external LLVMStructTypeInContext(Handle, FfiBuffer(Handle), u32, Bool) -> Handle = "LLVMStructTypeInContext@{{libraryPath}}"

                let first = makeHandle(10)
                let second = makeHandle(20)
                let values = [first, second]
                let printedFunction = Ashes.IO.print(readHandle(LLVMFunctionType(first, values, 2u32, false)))
                let printedCall = Ashes.IO.print(readHandle(LLVMBuildCall2(first, first, first, values, 2u32, "")))
                let printedGep = Ashes.IO.print(readHandle(LLVMBuildGEP2(first, first, first, values, 2u32, "")))
                let printedArray = Ashes.IO.print(readHandle(LLVMConstArray2(first, values, 2u64)))
                let printedConstStruct = Ashes.IO.print(readHandle(LLVMConstStructInContext(first, values, 2u32, false)))
                let printedStruct = Ashes.IO.print(readHandle(LLVMStructTypeInContext(first, values, 2u32, false)))
                Ashes.IO.print(readHandle(LLVMFunctionType(first, [], 0u32, false)))
                """;

            string output = await CompileAndRunAsync(source, tempDirectory).ConfigureAwait(false);

            output.ShouldBe("1230\n2230\n3230\n4230\n5230\n6230\n1000\n");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Ffi_buffer_calls_lower_for_every_target_abi()
    {
        IrProgram linux = LowerAllSixCalls("libllvm_ffi_buffer_fixture.so");
        ReadElfMachine(new LinuxX64LlvmBackend().Compile(linux)).ShouldBe((ushort)62);
        ReadElfMachine(new LinuxArm64LlvmBackend().Compile(linux)).ShouldBe((ushort)183);

        IrProgram windows = LowerAllSixCalls("llvm_ffi_buffer_fixture.dll");
        ReadPeMachine(new WindowsX64LlvmBackend().Compile(windows)).ShouldBe((ushort)0x8664);
        ReadPeMachine(new WindowsArm64LlvmBackend().Compile(windows)).ShouldBe((ushort)0xAA64);
    }

    private static IrProgram LowerAllSixCalls(string library)
    {
        string source = $$"""
            external type Handle
            external makeHandle(Int) -> Handle = "makeHandle@{{library}}"
            external LLVMFunctionType(Handle, FfiBuffer(Handle), u32, Bool) -> Handle = "LLVMFunctionType@{{library}}"
            external LLVMBuildCall2(Handle, Handle, Handle, FfiBuffer(Handle), u32, Str) -> Handle = "LLVMBuildCall2@{{library}}"
            external LLVMBuildGEP2(Handle, Handle, Handle, FfiBuffer(Handle), u32, Str) -> Handle = "LLVMBuildGEP2@{{library}}"
            external LLVMConstArray2(Handle, FfiBuffer(Handle), u64) -> Handle = "LLVMConstArray2@{{library}}"
            external LLVMConstStructInContext(Handle, FfiBuffer(Handle), u32, Bool) -> Handle = "LLVMConstStructInContext@{{library}}"
            external LLVMStructTypeInContext(Handle, FfiBuffer(Handle), u32, Bool) -> Handle = "LLVMStructTypeInContext@{{library}}"

            let seedHandle = makeHandle(1)
            let handles = [seedHandle, seedHandle]
            let functionType = LLVMFunctionType(seedHandle, handles, 2u32, false)
            let call = LLVMBuildCall2(seedHandle, seedHandle, seedHandle, handles, 2u32, "")
            let gep = LLVMBuildGEP2(seedHandle, seedHandle, seedHandle, handles, 2u32, "")
            let array = LLVMConstArray2(seedHandle, handles, 2u64)
            let constStruct = LLVMConstStructInContext(seedHandle, handles, 2u32, false)
            let structType = LLVMStructTypeInContext(seedHandle, handles, 2u32, false)
            LLVMFunctionType(seedHandle, [], 0u32, false)
            """;
        Diagnostics diagnostics = new();
        Frontend.Program ast = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }

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
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffi_buffer.c");
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

        string executablePath = Path.Combine(tempDirectory, "ffi-buffer-test");
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
