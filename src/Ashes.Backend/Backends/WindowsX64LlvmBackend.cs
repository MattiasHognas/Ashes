using Ashes.Backend.Llvm;
using Ashes.Semantics;

namespace Ashes.Backend.Backends;

public sealed class WindowsX64LlvmBackend : IBackend
{
    public string TargetId => TargetIds.WindowsX64;

    public byte[] Compile(IrProgram program, BackendCompileOptions? options = null)
    {
        return LlvmCodegen.Compile(program, TargetId, options ?? BackendCompileOptions.Default);
    }
}
