using Ashes.Backend.Llvm;
using Ashes.Semantics;

namespace Ashes.Backend.Backends;

/// <summary>The <see cref="IBackend"/> that emits 64-bit ARM Windows PE executables via LLVM.</summary>
public sealed class WindowsArm64LlvmBackend : IBackend
{
    /// <inheritdoc/>
    public string TargetId => TargetIds.WindowsArm64;

    /// <inheritdoc/>
    public byte[] Compile(IrProgram program, BackendCompileOptions? options = null)
    {
        return LlvmCodegen.Compile(program, TargetId, options ?? BackendCompileOptions.Default);
    }
}
