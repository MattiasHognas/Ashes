using Ashes.Backend.Llvm;
using Ashes.Semantics;

namespace Ashes.Backend.Backends;

/// <summary>The <see cref="IBackend"/> that emits 64-bit ARM Linux ELF executables via LLVM.</summary>
public sealed class LinuxArm64LlvmBackend : IBackend
{
    /// <inheritdoc/>
    public string TargetId => TargetIds.LinuxArm64;

    /// <inheritdoc/>
    public byte[] Compile(IrProgram program, BackendCompileOptions? options = null)
    {
        return LlvmCodegen.Compile(program, TargetId, options ?? BackendCompileOptions.Default);
    }
}
