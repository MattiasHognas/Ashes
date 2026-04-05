using Ashes.Backend.Llvm;
using Ashes.Semantics;

namespace Ashes.Backend.Backends;

public sealed class LinuxX64LlvmBackend : IBackend
{
    public string TargetId => TargetIds.LinuxX64;

    public byte[] Compile(IrProgram program, BackendCompileOptions? options = null)
    {
        return LlvmCodegen.Compile(program, TargetId, options ?? BackendCompileOptions.Default);
    }
}
