using Ashes.Frontend;

namespace Ashes.Fuzzing.Oracles;

/// <summary>
/// How a failing oracle renders the exception that failed it. A compile diagnostic is rendered from its
/// structured entries so the report carries each diagnostic's stable code: the code names the defect
/// independently of how the message happened to be worded for this case, which is what
/// <see cref="Shrinking.FuzzFailureSignature"/> compares when deciding whether a shrink still
/// reproduces the failure being reported.
/// </summary>
internal static class FuzzExceptionDescription
{
    internal static string Describe(Exception exception)
    {
        if (exception is not CompileDiagnosticException diagnostics)
        {
            return exception.ToString();
        }

        IEnumerable<string> lines = diagnostics.StructuredErrors.Select(
            entry => $"{entry.Code ?? "-"} [pos {entry.Pos}] {entry.Message}");
        return $"{diagnostics.GetType().FullName}: {string.Join('\n', lines)}";
    }
}
