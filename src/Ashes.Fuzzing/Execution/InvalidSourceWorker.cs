using Ashes.Frontend;

namespace Ashes.Fuzzing.Execution;

internal static class InvalidSourceWorker
{
    internal const string Command = "__invalid-source-worker";
    internal const int MaximumDiagnostics = 1024;

    internal static int Run(string sourcePath)
    {
        try
        {
            string source = File.ReadAllText(sourcePath);
            Diagnostics diagnostics = new();
            _ = new Parser(source, diagnostics).ParseProgram();
            if (diagnostics.Errors.Count > MaximumDiagnostics)
            {
                Console.Error.WriteLine($"Parser emitted {diagnostics.Errors.Count} diagnostics; maximum is {MaximumDiagnostics}.");
                return 3;
            }

            Console.WriteLine(diagnostics.Errors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }
}
