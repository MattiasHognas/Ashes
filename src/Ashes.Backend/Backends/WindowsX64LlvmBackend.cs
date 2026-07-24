using Ashes.Backend.Llvm;
using Ashes.Semantics;

namespace Ashes.Backend.Backends;

/// <summary>The <see cref="IBackend"/> that emits 64-bit x86 Windows PE executables via LLVM.</summary>
public sealed class WindowsX64LlvmBackend : IBackend
{
    /// <inheritdoc/>
    public string TargetId => TargetIds.WindowsX64;

    /// <inheritdoc/>
    public byte[] Compile(IrProgram program, BackendCompileOptions? options = null)
    {
        return LlvmCodegen.Compile(program, TargetId, options ?? BackendCompileOptions.Default);
    }
}
