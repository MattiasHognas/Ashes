using Ashes.Semantics;

namespace Ashes.Backend.Backends;

public interface IBackend
{
    string TargetId { get; }
    byte[] Compile(IrProgram program);
}
