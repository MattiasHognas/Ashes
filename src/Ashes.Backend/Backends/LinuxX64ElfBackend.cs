using Ashes.Semantics;

namespace Ashes.Backend.Backends;

public sealed class LinuxX64ElfBackend : IBackend
{
    public string TargetId => TargetIds.LinuxX64;
    public byte[] Compile(IrProgram program, BackendCompileOptions? options = null)
    {
        return new LinuxX64LlvmBackend().Compile(program, options);
    }
}
