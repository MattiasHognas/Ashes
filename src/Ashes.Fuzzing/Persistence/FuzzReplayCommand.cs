using System.Globalization;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Persistence;

internal static class FuzzReplayCommand
{
    internal static IReadOnlyList<string> Arguments(GeneratedFuzzCase testCase, FuzzConfiguration configuration) =>
        Arguments(testCase.MasterSeed, testCase.CaseIndex, testCase.Profile, testCase.Budget.RemainingNodes, configuration);

    internal static IReadOnlyList<string> Arguments(
        ulong masterSeed,
        int caseIndex,
        string profile,
        int maximumNodes,
        FuzzConfiguration configuration) =>
    [
        "replay",
        "--profile", profile,
        "--seed", masterSeed.ToString(CultureInfo.InvariantCulture),
        "--case", caseIndex.ToString(CultureInfo.InvariantCulture),
        "--max-nodes", maximumNodes.ToString(CultureInfo.InvariantCulture),
        "--target", configuration.Target,
        "--compiler-timeout", ((int)configuration.CompilerTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        "--program-timeout", ((int)configuration.ProgramTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        "--max-output-bytes", configuration.MaximumOutputBytes.ToString(CultureInfo.InvariantCulture),
        "--max-artifact-bytes", configuration.MaximumArtifactBytes.ToString(CultureInfo.InvariantCulture),
    ];

    internal static string Format(GeneratedFuzzCase testCase, FuzzConfiguration configuration)
        => Format(testCase.MasterSeed, testCase.CaseIndex, testCase.Profile, testCase.Budget.RemainingNodes, configuration);

    internal static string Format(
        ulong masterSeed,
        int caseIndex,
        string profile,
        int maximumNodes,
        FuzzConfiguration configuration)
    {
        string arguments = string.Join(" ", Arguments(masterSeed, caseIndex, profile, maximumNodes, configuration).Select(Quote));
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
