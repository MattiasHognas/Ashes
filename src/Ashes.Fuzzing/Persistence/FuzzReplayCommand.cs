using System.Globalization;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Persistence;

internal static class FuzzReplayCommand
{
    internal static IReadOnlyList<string> Arguments(GeneratedFuzzCase testCase, FuzzConfiguration configuration) =>
    [
        "replay",
        "--profile", testCase.Profile,
        "--seed", testCase.MasterSeed.ToString(CultureInfo.InvariantCulture),
        "--case", testCase.CaseIndex.ToString(CultureInfo.InvariantCulture),
        "--max-nodes", testCase.Budget.RemainingNodes.ToString(CultureInfo.InvariantCulture),
        "--target", configuration.Target,
        "--compiler-timeout", ((int)configuration.CompilerTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        "--program-timeout", ((int)configuration.ProgramTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
    ];

    internal static string Format(GeneratedFuzzCase testCase, FuzzConfiguration configuration)
    {
        string arguments = string.Join(" ", Arguments(testCase, configuration).Select(Quote));
        return $"dotnet run --project src/Ashes.Fuzzing/Ashes.Fuzzing.csproj --configuration Release -- {arguments}";
    }

    private static string Quote(string value)
    {
        if (value.Length != 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            return value;
        }
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
