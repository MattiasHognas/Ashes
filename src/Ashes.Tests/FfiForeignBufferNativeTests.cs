using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ashes.Backend.Backends;
using Ashes.Backend.Llvm;
using Ashes.Backend.Llvm.Interop;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class FfiForeignBufferNativeTests
{
    [Test]
    public async Task Foreign_buffers_copy_binary_data_and_release_the_owner_once()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), "ashes-ffi-foreign-buffer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string libraryPath = Path.Combine(tempDirectory, "libashes_ffi_foreign_buffer.so");
            await BuildFixtureAsync(libraryPath).ConfigureAwait(false);
            string output = await CompileAndRunAsync(BuildSource(libraryPath), tempDirectory).ConfigureAwait(false);

            output.ShouldBe("0\n1\n5\n0\n2\n3\n4\n2\n1048576\n1\n255\n3\nerror:Foreign byte pointer was null for a nonzero length.\n4\nerror:Foreign byte length exceeds 1073741824 bytes.\n5\n5\n6\n");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void Foreign_buffer_copy_lowers_for_every_target_abi()
    {
        IrProgram linux = LowerProgram(BuildStructuralSource("libllvm_ffi_foreign_buffer_fixture.so"));
        ReadElfMachine(new LinuxX64LlvmBackend().Compile(linux)).ShouldBe((ushort)62);
        ReadElfMachine(new LinuxArm64LlvmBackend().Compile(linux)).ShouldBe((ushort)183);

        IrProgram windows = LowerProgram(BuildStructuralSource("llvm_ffi_foreign_buffer_fixture.dll"));
        ReadPeMachine(new WindowsX64LlvmBackend().Compile(windows)).ShouldBe((ushort)0x8664);
        ReadPeMachine(new WindowsArm64LlvmBackend().Compile(windows)).ShouldBe((ushort)0xAA64);
    }

    [Test]
    [Arguments(TargetIds.LinuxX64, "x86_64-unknown-linux-gnu", "x86-64", false)]
    [Arguments(TargetIds.LinuxArm64, "aarch64-unknown-linux-gnu", "generic", true)]
    [Arguments(TargetIds.WindowsX64, "x86_64-pc-windows-msvc", "x86-64", false)]
    [Arguments(TargetIds.WindowsArm64, "aarch64-pc-windows-msvc", "generic", true)]
    public async Task Private_llvm_facade_emits_the_same_object_as_the_current_adapter(
        string targetId,
        string triple,
        string cpu,
        bool arm64)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string systemLlvm = Path.Combine(Path.DirectorySeparatorChar.ToString(), "usr", "lib", "libLLVM.so");
        string llvmLibrary = File.Exists(systemLlvm)
            ? systemLlvm
            : Path.Combine(GetRepositoryRoot(), "runtimes", "linux-x64", "libLLVM.so");
        File.Exists(llvmLibrary).ShouldBeTrue($"Missing test LLVM runtime: {llvmLibrary}");
        byte[] expected = EmitTinyObjectWithCurrentAdapter(targetId);

        string tempDirectory = Path.Combine(Path.GetTempPath(), "ashes-ffi-llvm-facade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            byte[] actual = await CompileAndRunBytesAsync(
                BuildLlvmFacadeSource(llvmLibrary, targetId, triple, cpu, arm64),
                tempDirectory).ConfigureAwait(false);
            actual.ShouldBe(expected);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string BuildSource(string library) => $$"""
        external type MemoryBuffer resource destructor LLVMDisposeMemoryBuffer
        external createMemoryBuffer(Int) -> MemoryBuffer = "ashes_ffi_memory_buffer_create@{{library}}"
        external createMemoryBufferOut(Int, out MemoryBuffer) -> u32 = "ashes_ffi_memory_buffer_create_out@{{library}}"
        external LLVMGetBufferStart(borrow MemoryBuffer) -> *u8 = "LLVMGetBufferStart@{{library}}"
        external LLVMGetBufferSize(borrow MemoryBuffer) -> u64 = "LLVMGetBufferSize@{{library}}"
        external LLVMDisposeMemoryBuffer(consume MemoryBuffer) -> void = "LLVMDisposeMemoryBuffer@{{library}}"
        external disposeCount() -> Int = "ashes_ffi_memory_buffer_dispose_count@{{library}}"
        let copyKind kind =
            let buffer = createMemoryBuffer(kind) in
            let pointer = LLVMGetBufferStart(buffer) in
            let length = LLVMGetBufferSize(buffer) in
            Ashes.Ffi.copyBytes(pointer)(length)
        let copyOverflow kind =
            let buffer = createMemoryBuffer(kind) in
            let pointer = LLVMGetBufferStart(buffer) in
            Ashes.Ffi.copyBytes(pointer)(18446744073709551615u64)
        let copyNullNonzero kind =
            let buffer = createMemoryBuffer(kind) in
            let pointer = LLVMGetBufferStart(buffer) in
            Ashes.Ffi.copyBytes(pointer)(1u64)
        let outBuffer result =
            match result with
                | (_, Some(buffer)) -> buffer
                | (_, None) -> Ashes.IO.panic("missing out buffer")
        let copyOutKind kind =
            let buffer = outBuffer(createMemoryBufferOut(kind)) in
            let pointer = LLVMGetBufferStart(buffer) in
            let length = LLVMGetBufferSize(buffer) in
            Ashes.Ffi.copyBytes(pointer)(length)
        let unwrap result =
            match result with
                | Error(message) -> Ashes.IO.panic(message)
                | Ok(bytes) -> bytes
        let show result =
            match result with
                | Error(message) -> "error:" + message
                | Ok(_) -> "ok"
        let empty = unwrap(copyKind(0))
        let printEmptyLength = Ashes.IO.print(Ashes.Byte.length(empty))
        let printCount1 = Ashes.IO.print(disposeCount())
        let binary = unwrap(copyKind(1))
        let printBinaryLength = Ashes.IO.print(Ashes.Byte.length(binary))
        let printFirst = Ashes.IO.print(Ashes.Number.UInt.toInt(Ashes.Byte.get(binary)(0)))
        let printThird = Ashes.IO.print(Ashes.Number.UInt.toInt(Ashes.Byte.get(binary)(2)))
        let printFourth = Ashes.IO.print(Ashes.Number.UInt.toInt(Ashes.Byte.get(binary)(3)))
        let printLast = Ashes.IO.print(Ashes.Number.UInt.toInt(Ashes.Byte.get(binary)(4)))
        let printCount2 = Ashes.IO.print(disposeCount())
        let large = unwrap(copyKind(2))
        let printLargeLength = Ashes.IO.print(Ashes.Byte.length(large))
        let printLargeMiddle = Ashes.IO.print(Ashes.Number.UInt.toInt(Ashes.Byte.get(large)(65537)))
        let printLargeLast = Ashes.IO.print(Ashes.Number.UInt.toInt(Ashes.Byte.get(large)(1048575)))
        let printCount3 = Ashes.IO.print(disposeCount())
        let printNullNonzero = Ashes.IO.print(show(copyNullNonzero(0)))
        let printCount4 = Ashes.IO.print(disposeCount())
        let printOverflow = Ashes.IO.print(show(copyOverflow(1)))
        let printCount5 = Ashes.IO.print(disposeCount())
        let outBytes = unwrap(copyOutKind(1))
        let printOutLength = Ashes.IO.print(Ashes.Byte.length(outBytes))
        Ashes.IO.print(disposeCount())
        """;

    private static string BuildStructuralSource(string library) => $$"""
        external type MemoryBuffer resource destructor LLVMDisposeMemoryBuffer
        external createMemoryBuffer(Int) -> MemoryBuffer = "ashes_ffi_memory_buffer_create@{{library}}"
        external LLVMGetBufferStart(borrow MemoryBuffer) -> *u8 = "LLVMGetBufferStart@{{library}}"
        external LLVMGetBufferSize(borrow MemoryBuffer) -> u64 = "LLVMGetBufferSize@{{library}}"
        external LLVMDisposeMemoryBuffer(consume MemoryBuffer) -> void = "LLVMDisposeMemoryBuffer@{{library}}"

        let buffer = createMemoryBuffer(1)
        let pointer = LLVMGetBufferStart(buffer)
        let length = LLVMGetBufferSize(buffer)
        Ashes.Ffi.copyBytes(pointer)(length)
        """;

    private static string BuildLlvmFacadeSource(
        string library,
        string targetId,
        string triple,
        string cpu,
        bool arm64)
    {
        string initialize = arm64
            ? "let a = LLVMInitializeAArch64TargetInfo() in let b = LLVMInitializeAArch64Target() in let c = LLVMInitializeAArch64TargetMC() in LLVMInitializeAArch64AsmPrinter()"
            : "let a = LLVMInitializeX86TargetInfo() in let b = LLVMInitializeX86Target() in let c = LLVMInitializeX86TargetMC() in LLVMInitializeX86AsmPrinter()";
        return BuildLlvmFacadeTypeAndTargetDeclarations(library)
            + BuildLlvmFacadeModuleDeclarations(library)
            + $$"""

        let targetFrom result =
            match result with
                | (_, Some(target), _) -> target
                | (_, None, _) -> Ashes.IO.panic("LLVM target lookup failed")
        let textFrom result =
            match result with
                | Ok(text) -> text
                | Error(message) -> Ashes.IO.panic(message)
        let bytesFrom result =
            match result with
                | Ok(bytes) -> bytes
                | Error(message) -> Ashes.IO.panic(message)
        let emittedBuffer result =
            match result with
                | (_, _, Some(buffer)) -> buffer
                | (_, _, None) -> Ashes.IO.panic("LLVM object emission failed")
        let initialize unit =
            {{initialize}}
        let emitTinyObject unit =
            let initialized = initialize(Unit) in
            let target = targetFrom(LLVMGetTargetFromTriple("{{triple}}")) in
            let machine = LLVMCreateTargetMachine(target, "{{triple}}", "{{cpu}}", "", 0u32, 1u32, 0u32) in
            let dataLayout = LLVMCreateTargetDataLayout(machine) in
            let layoutText = textFrom(LLVMCopyStringRepOfTargetData(dataLayout)) in
            let context = LLVMContextCreate() in
            let module = LLVMModuleCreateWithNameInContext("ashes.{{targetId}}.module", context) in
            let setTarget = LLVMSetTarget(module, "{{triple}}") in
            let setLayout = LLVMSetDataLayout(module, layoutText) in
            let builder = LLVMCreateBuilderInContext(context) in
            let int64 = LLVMInt64TypeInContext(context) in
            let functionType = LLVMFunctionType(int64, [], 0u32, false) in
            let functionValue = LLVMAddFunction(module, "answer", functionType) in
            let entry = LLVMAppendBasicBlockInContext(context, functionValue, "entry") in
            let positioned = LLVMPositionBuilderAtEnd(builder, entry) in
            let answer = LLVMConstInt(int64, 42u64, false) in
            let returned = LLVMBuildRet(builder, answer) in
            let objectBuffer = emittedBuffer(LLVMTargetMachineEmitToMemoryBuffer(machine, module, 1u32)) in
            let pointer = LLVMGetBufferStart(objectBuffer) in
            let length = LLVMGetBufferSize(objectBuffer) in
            bytesFrom(Ashes.Ffi.copyBytes(pointer)(length))

        Ashes.IO.writeBytes(emitTinyObject(Unit))
        """;
    }

    private static string BuildLlvmFacadeTypeAndTargetDeclarations(string library) => $$"""
        external type Context resource destructor LLVMContextDispose
        external type Module resource destructor LLVMDisposeModule
        external type Builder resource destructor LLVMDisposeBuilder
        external type TargetMachine resource destructor LLVMDisposeTargetMachine
        external type TargetData resource destructor LLVMDisposeTargetData
        external type MemoryBuffer resource destructor LLVMDisposeMemoryBuffer
        external type Target
        external type TypeRef
        external type ValueRef
        external type BasicBlock

        external LLVMInitializeX86TargetInfo() -> void = "LLVMInitializeX86TargetInfo@{{library}}"
        external LLVMInitializeX86Target() -> void = "LLVMInitializeX86Target@{{library}}"
        external LLVMInitializeX86TargetMC() -> void = "LLVMInitializeX86TargetMC@{{library}}"
        external LLVMInitializeX86AsmPrinter() -> void = "LLVMInitializeX86AsmPrinter@{{library}}"
        external LLVMInitializeAArch64TargetInfo() -> void = "LLVMInitializeAArch64TargetInfo@{{library}}"
        external LLVMInitializeAArch64Target() -> void = "LLVMInitializeAArch64Target@{{library}}"
        external LLVMInitializeAArch64TargetMC() -> void = "LLVMInitializeAArch64TargetMC@{{library}}"
        external LLVMInitializeAArch64AsmPrinter() -> void = "LLVMInitializeAArch64AsmPrinter@{{library}}"
        external LLVMGetTargetFromTriple(Str, out Target, out FfiStr(owned LLVMDisposeMessage)) -> u32 = "LLVMGetTargetFromTriple@{{library}}"
        external LLVMDisposeMessage(*u8) -> void = "LLVMDisposeMessage@{{library}}"
        external LLVMCreateTargetMachine(Target, Str, Str, Str, u32, u32, u32) -> TargetMachine = "LLVMCreateTargetMachine@{{library}}"
        external LLVMDisposeTargetMachine(consume TargetMachine) -> void = "LLVMDisposeTargetMachine@{{library}}"
        external LLVMCreateTargetDataLayout(borrow TargetMachine) -> TargetData = "LLVMCreateTargetDataLayout@{{library}}"
        external LLVMCopyStringRepOfTargetData(borrow TargetData) -> FfiStr(owned LLVMDisposeMessage) = "LLVMCopyStringRepOfTargetData@{{library}}"
        external LLVMDisposeTargetData(consume TargetData) -> void = "LLVMDisposeTargetData@{{library}}"
        """;

    private static string BuildLlvmFacadeModuleDeclarations(string library) => $$"""
        external LLVMContextCreate() -> Context = "LLVMContextCreate@{{library}}"
        external LLVMContextDispose(consume Context) -> void = "LLVMContextDispose@{{library}}"
        external LLVMModuleCreateWithNameInContext(Str, borrow Context) -> Module = "LLVMModuleCreateWithNameInContext@{{library}}"
        external LLVMSetTarget(borrow Module, Str) -> void = "LLVMSetTarget@{{library}}"
        external LLVMSetDataLayout(borrow Module, Str) -> void = "LLVMSetDataLayout@{{library}}"
        external LLVMDisposeModule(consume Module) -> void = "LLVMDisposeModule@{{library}}"
        external LLVMCreateBuilderInContext(borrow Context) -> Builder = "LLVMCreateBuilderInContext@{{library}}"
        external LLVMDisposeBuilder(consume Builder) -> void = "LLVMDisposeBuilder@{{library}}"
        external LLVMInt64TypeInContext(borrow Context) -> TypeRef = "LLVMInt64TypeInContext@{{library}}"
        external LLVMFunctionType(TypeRef, FfiBuffer(TypeRef), u32, Bool) -> TypeRef = "LLVMFunctionType@{{library}}"
        external LLVMAddFunction(borrow Module, Str, TypeRef) -> ValueRef = "LLVMAddFunction@{{library}}"
        external LLVMAppendBasicBlockInContext(borrow Context, ValueRef, Str) -> BasicBlock = "LLVMAppendBasicBlockInContext@{{library}}"
        external LLVMPositionBuilderAtEnd(borrow Builder, BasicBlock) -> void = "LLVMPositionBuilderAtEnd@{{library}}"
        external LLVMConstInt(TypeRef, u64, Bool) -> ValueRef = "LLVMConstInt@{{library}}"
        external LLVMBuildRet(borrow Builder, ValueRef) -> ValueRef = "LLVMBuildRet@{{library}}"
        external LLVMTargetMachineEmitToMemoryBuffer(borrow TargetMachine, borrow Module, u32, out FfiStr(owned LLVMDisposeMessage), out MemoryBuffer) -> u32 = "LLVMTargetMachineEmitToMemoryBuffer@{{library}}"
        external LLVMGetBufferStart(borrow MemoryBuffer) -> *u8 = "LLVMGetBufferStart@{{library}}"
        external LLVMGetBufferSize(borrow MemoryBuffer) -> u64 = "LLVMGetBufferSize@{{library}}"
        external LLVMDisposeMemoryBuffer(consume MemoryBuffer) -> void = "LLVMDisposeMemoryBuffer@{{library}}"
        """;

    private static byte[] EmitTinyObjectWithCurrentAdapter(string targetId)
    {
        using LlvmTargetContext target = LlvmTargetSetup.Create(targetId, BackendOptimizationLevel.O0);
        LlvmTypeHandle int64 = LlvmApi.Int64TypeInContext(target.Context);
        LlvmTypeHandle functionType = LlvmApi.FunctionType(int64, []);
        LlvmValueHandle function = LlvmApi.AddFunction(target.Module, "answer", functionType);
        LlvmBasicBlockHandle entry = LlvmApi.AppendBasicBlockInContext(target.Context, function, "entry");
        LlvmApi.PositionBuilderAtEnd(target.Builder, entry);
        LlvmApi.BuildRet(target.Builder, LlvmApi.ConstInt(int64, 42, 0));

        int error = LlvmApi.TargetMachineEmitToMemoryBuffer(
            target.TargetMachine,
            target.Module,
            LlvmCodeGenFileType.Object,
            out nint errorMessage,
            out nint memoryBuffer);
        if (error != 0)
        {
            string message = Marshal.PtrToStringAnsi(errorMessage) ?? "unknown error";
            LlvmApi.DisposeMessage(errorMessage);
            throw new InvalidOperationException(message);
        }

        try
        {
            int size = checked((int)LlvmApi.GetBufferSize(memoryBuffer));
            byte[] bytes = new byte[size];
            Marshal.Copy(LlvmApi.GetBufferStart(memoryBuffer), bytes, 0, size);
            return bytes;
        }
        finally
        {
            LlvmApi.DisposeMemoryBuffer(memoryBuffer);
        }
    }

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
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ffi_foreign_buffer.c");
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
        string executablePath = Path.Combine(tempDirectory, "ffi-foreign-buffer-test");
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

    private static async Task<byte[]> CompileAndRunBytesAsync(string source, string tempDirectory)
    {
        string executablePath = Path.Combine(tempDirectory, "ffi-llvm-facade-test");
        TestProcessHelper.WriteExecutable(executablePath, new LinuxX64LlvmBackend().Compile(LowerProgram(source)));
        ProcessStartInfo startInfo = new(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = await TestProcessHelper.StartProcessAsync(startInfo).ConfigureAwait(false);
        using MemoryStream stdout = new();
        Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await Task.WhenAll(copyOutput, process.WaitForExitAsync()).ConfigureAwait(false);
        process.ExitCode.ShouldBe(0, stderr);
        return stdout.ToArray();
    }

    private static string GetRepositoryRoot([CallerFilePath] string callerFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", ".."));
}
