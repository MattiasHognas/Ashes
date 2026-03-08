using Ashes.Semantics;

namespace Ashes.Backend.Backends;

public sealed class LinuxX64ElfBackend : IBackend
{
    public string TargetId => TargetIds.LinuxX64;
    public byte[] Compile(IrProgram program)
    {
        return new X64CodegenIced().CompileToElf(program);
    }
}
