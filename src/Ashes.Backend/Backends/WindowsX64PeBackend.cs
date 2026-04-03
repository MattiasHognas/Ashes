using Ashes.Semantics;

namespace Ashes.Backend.Backends;

/// <summary>
/// Windows x64 backend producing a PE32+ console executable.
/// Composes a code generator and a PE writer.
/// </summary>
public sealed class WindowsX64PeBackend : IBackend
{
    public string TargetId => TargetIds.WindowsX64;

    public byte[] Compile(IrProgram program, BackendCompileOptions? options = null)
    {
        var codegen = new WindowsX64CodegenIced();
        var writer = new Pe64Writer(codegen);
        return writer.CompileToPe(program);
    }
}
