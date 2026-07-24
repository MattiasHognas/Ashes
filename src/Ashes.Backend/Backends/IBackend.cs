using Ashes.Semantics;

namespace Ashes.Backend.Backends;

/// <summary>
/// A native code generator for one target platform. Each implementation binds a specific target RID
/// (see <see cref="TargetIds"/>) to the LLVM pipeline, turning a lowered <see cref="IrProgram"/> into
/// a standalone executable image. Resolve one through <see cref="BackendFactory.Create(string)"/>.
/// </summary>
public interface IBackend
{
    /// <summary>The target RID this backend emits for, e.g. <c>linux-x64</c> or <c>win-arm64</c>.</summary>
    string TargetId { get; }

    /// <summary>
    /// Compiles <paramref name="program"/> to a native executable image (ELF on Linux, PE on Windows)
    /// for <see cref="TargetId"/>, returning the linked bytes. When <paramref name="options"/> is
    /// null, <see cref="BackendCompileOptions.Default"/> applies.
    /// </summary>
    byte[] Compile(IrProgram program, BackendCompileOptions? options = null);
}
