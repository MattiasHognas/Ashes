using System.Buffers.Binary;
using System.Diagnostics;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class FfiStringNativeTests
{
    [Test]
    public async Task Ffi_strings_copy_validate_null_and_dispose_exactly_once()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), "ashes-ffi-string", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string libraryPath = Path.Combine(tempDirectory, "libashes_ffi_string.so");
            await BuildFixtureAsync(libraryPath).ConfigureAwait(false);
            string output = await CompileAndRunAsync(BuildSource(libraryPath), tempDirectory).ConfigureAwait(false);

            output.ShouldBe("owned ✓\n1\n\n2\na\n3\nerror:Native UTF-8 string is invalid.\n4\nborrowed text\nnone\n4\n0|none\n4\n1|verify failed\n5\n");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Ffi_string_llvm_shapes_lower_for_every_target_abi()
    {
        IrProgram linux = LowerProgram(BuildSource("libllvm_ffi_string_fixture.so"));
        ReadElfMachine(new LinuxX64LlvmBackend().Compile(linux)).ShouldBe((ushort)62);
        ReadElfMachine(new LinuxArm64LlvmBackend().Compile(linux)).ShouldBe((ushort)183);

        IrProgram windows = LowerProgram(BuildSource("llvm_ffi_string_fixture.dll"));
        ReadPeMachine(new WindowsX64LlvmBackend().Compile(windows)).ShouldBe((ushort)0x8664);
        ReadPeMachine(new WindowsArm64LlvmBackend().Compile(windows)).ShouldBe((ushort)0xAA64);
    }

    private static string BuildSource(string library) => $$"""
        external type Handle
        external LLVMDisposeMessage(*u8) -> void = "LLVMDisposeMessage@{{library}}"
        external disposeCount() -> Int = "ashes_ffi_string_dispose_count@{{library}}"
        external makeHandle(Int) -> Handle = "ashes_ffi_string_handle@{{library}}"
        external LLVMGetHostCPUName() -> FfiStr(owned LLVMDisposeMessage) = "LLVMGetHostCPUName@{{library}}"
        external LLVMGetHostCPUFeatures() -> FfiStr(owned LLVMDisposeMessage) = "LLVMGetHostCPUFeatures@{{library}}"
        external LLVMCopyStringRepOfTargetData(Handle) -> FfiStr(owned LLVMDisposeMessage) = "LLVMCopyStringRepOfTargetData@{{library}}"
        external LLVMPrintModuleToString(Handle) -> FfiStr(owned LLVMDisposeMessage) = "LLVMPrintModuleToString@{{library}}"
        external LLVMGetTargetName(Handle) -> FfiStr(nullable borrowed) = "LLVMGetTargetName@{{library}}"
        external LLVMVerifyModule(Handle, u32, out FfiStr(owned LLVMDisposeMessage)) -> Bool = "LLVMVerifyModule@{{library}}"

        let show result =
            match result with
                | Error(error) -> "error:" + error
                | Ok(value) -> value
        let showMaybe result =
            match result with
                | Error(error) -> "error:" + error
                | Ok(None) -> "none"
                | Ok(Some(value)) -> value
        let showVerify result =
            match result with
                | (status, message) ->
                    let statusText = if status then "1" else "0" in
                    statusText + "|" + showMaybe(message)
        let nativePtr = makeHandle(1)
        let nullPtr = makeHandle(0)
        let printedOwned = Ashes.IO.print(show(LLVMGetHostCPUName()))
        let count1 = Ashes.IO.print(disposeCount())
        let printedEmpty = Ashes.IO.print(show(LLVMGetHostCPUFeatures()))
        let count2 = Ashes.IO.print(disposeCount())
        let printedBoundary = Ashes.IO.print(show(LLVMCopyStringRepOfTargetData(nativePtr)))
        let count3 = Ashes.IO.print(disposeCount())
        let printedInvalid = Ashes.IO.print(show(LLVMPrintModuleToString(nativePtr)))
        let count4 = Ashes.IO.print(disposeCount())
        let printedBorrowed = Ashes.IO.print(showMaybe(LLVMGetTargetName(nativePtr)))
        let printedNull = Ashes.IO.print(showMaybe(LLVMGetTargetName(nullPtr)))
        let borrowedCount = Ashes.IO.print(disposeCount())
        let printedVerifyNull = Ashes.IO.print(showVerify(LLVMVerifyModule(nativePtr, 0u32)))
        let verifyNullCount = Ashes.IO.print(disposeCount())
        let printedVerify = Ashes.IO.print(showVerify(LLVMVerifyModule(nativePtr, 1u32)))
        Ashes.IO.print(disposeCount())
        """;

    private static IrProgram LowerProgram(string source)
    {
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
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffi_string.c");
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
        string executablePath = Path.Combine(tempDirectory, "ffi-string-test");
        TestProcessHelper.WriteExecutable(executablePath, new LinuxX64LlvmBackend().Compile(LowerProgram(source)));
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
