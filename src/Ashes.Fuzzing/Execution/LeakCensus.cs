using System.Globalization;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Execution;

/// <summary>
/// One leak-census case: its name, and the program as a function of how many rounds of work it does.
/// </summary>
internal sealed record LeakCensusCase(string Name, Func<int, string> SourceForRounds);

/// <summary>
/// The outcome of one case: the reference-counted cells still live when the program ended after one
/// round of work and after two, or why there is no count.
/// </summary>
internal sealed record LeakCensusResult(string Name, long? LiveAfterOne, long? LiveAfterTwo, string? Failure)
{
    internal bool Leaks => LiveAfterOne is long one && LiveAfterTwo is long two && two != one;
}

/// <summary>
/// Runs every case once, deterministically, and reports each one whose second round of work leaves
/// cells live that the first did not: every enabled combination template of the profile, forced once
/// per seed, and every <c>ROUNDS</c> template of a directory. A program built with
/// <c>ASHES_RC_LEAKCHECK=1</c> reports its live reference-counted cells at its end; a value the program
/// keeps to its end counts as live in both runs, so the difference is exactly what one round leaked.
/// </summary>
internal static class LeakCensus
{
    private const string LiveCellsPrefix = "ashes-rc-live ";

    internal static async Task<int> RunAsync(
        FuzzConfiguration configuration,
        FuzzProfile profile,
        ProgramGenerator generator,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        List<LeakCensusCase> cases = [.. CombinationCases(configuration, profile, generator), .. TemplateCases(configuration.TemplateDirectory)];
        string reportDirectory = Path.Combine(repositoryRoot, configuration.ArtifactRoot, "leak-census");
        Directory.CreateDirectory(reportDirectory);
        CompilerExecution compiler = new();
        List<LeakCensusResult> results = new(cases.Count);
        foreach (LeakCensusCase testCase in cases)
        {
            LeakCensusResult result = await MeasureAsync(testCase, configuration, compiler, repositoryRoot, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            Console.WriteLine(Describe(result));
            if (result.Leaks || result.Failure is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(reportDirectory, SafeFileName(testCase.Name) + ".ash"), testCase.SourceForRounds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        await File.WriteAllLinesAsync(Path.Combine(reportDirectory, "report.tsv"), results.Select(ReportLine), cancellationToken).ConfigureAwait(false);
        int leaking = results.Count(result => result.Leaks);
        int failed = results.Count(result => result.Failure is not null);
        Console.WriteLine($"leak census: {results.Count} case(s), {leaking} leaking, {failed} without a count; report in {reportDirectory}");
        return leaking == 0 && failed == 0 ? 0 : 1;
    }

    // Case i of a seed forces the profile's i-th combination template, so one pass over the
    // templates' count visits every template exactly once.
    private static IEnumerable<LeakCensusCase> CombinationCases(FuzzConfiguration configuration, FuzzProfile profile, ProgramGenerator generator)
    {
        int templateCount = profile.EnabledCombinations.Count;
        string[] templateIds = profile.EnabledCombinations.Order(StringComparer.Ordinal).ToArray();
        for (int seedIndex = 0; seedIndex < configuration.SeedCount; seedIndex++)
        {
            ulong seed = configuration.Seed + (ulong)seedIndex;
            for (int index = 0; index < templateCount; index++)
            {
                yield return GenerateCase(generator, profile, configuration.MaximumNodes, seed, index, templateIds[index]);
            }
        }
    }

    private static LeakCensusCase GenerateCase(ProgramGenerator generator, FuzzProfile profile, int maximumNodes, ulong seed, int index, string templateId)
    {
        try
        {
            GeneratedFuzzCase generated = generator.Generate(seed, index, profile, maximumNodes);
            return new LeakCensusCase(
                $"{templateId}@{seed}:{generated.Type}",
                rounds => ObservableValueRenderer.RenderMemoryWorkload(generated, rounds));
        }
        catch (InvalidOperationException exception)
        {
            return new LeakCensusCase($"{templateId}@{seed}", _ => throw new InvalidOperationException(exception.Message, exception));
        }
    }

    // A template names its repeat count ROUNDS, as the reproducers under scripts/selfhost-dev do.
    private static IEnumerable<LeakCensusCase> TemplateCases(string? directory)
    {
        if (directory is null)
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.ash")
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                string template = File.ReadAllText(path);
                return new LeakCensusCase(
                    "template:" + Path.GetFileNameWithoutExtension(path),
                    rounds => template.Replace("ROUNDS", rounds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            });
    }

    private static async Task<LeakCensusResult> MeasureAsync(
        LeakCensusCase testCase,
        FuzzConfiguration configuration,
        CompilerExecution compiler,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        long?[] live = new long?[2];
        for (int rounds = 1; rounds <= 2; rounds++)
        {
            string source;
            try
            {
                source = testCase.SourceForRounds(rounds);
            }
            catch (InvalidOperationException exception)
            {
                return new LeakCensusResult(testCase.Name, null, null, "generation: " + exception.Message);
            }

            (ProcessResult compile, ProcessResult? run) = await compiler.CompileAndRunAsync(
                source, repositoryRoot, configuration.Target, "-O2", configuration.CompilerTimeout, configuration.ProgramTimeout,
                configuration.MaximumOutputBytes, cancellationToken, leakCheck: true).ConfigureAwait(false);
            if (run is null)
            {
                return new LeakCensusResult(testCase.Name, null, null, $"compile exit {compile.ExitCode}: {FirstLine(compile.StandardError + compile.StandardOutput)}");
            }
            if (run.ExitCode != 0 || run.TimedOut)
            {
                return new LeakCensusResult(testCase.Name, null, null, run.TimedOut ? "program timed out" : $"program exit {run.ExitCode}");
            }

            live[rounds - 1] = ParseLiveCells(run.StandardError);
            if (live[rounds - 1] is null)
            {
                return new LeakCensusResult(testCase.Name, null, null, "program reported no live-cell count");
            }
        }

        return new LeakCensusResult(testCase.Name, live[0], live[1], null);
    }

    internal static long? ParseLiveCells(string standardError)
    {
        string? line = standardError.Split('\n').LastOrDefault(candidate => candidate.StartsWith(LiveCellsPrefix, StringComparison.Ordinal));
        return line is not null && long.TryParse(line.AsSpan(LiveCellsPrefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long live)
            ? live
            : null;
    }

    private static string Describe(LeakCensusResult result)
        => result.Failure is not null
            ? $"SKIP  {result.Name}: {result.Failure}"
            : result.Leaks
                ? $"LEAK  {result.Name}: {result.LiveAfterTwo - result.LiveAfterOne} cell(s) per round (live {result.LiveAfterOne} -> {result.LiveAfterTwo})"
                : $"ok    {result.Name}";

    private static string ReportLine(LeakCensusResult result)
        => string.Join('\t', result.Name, result.LiveAfterOne?.ToString(CultureInfo.InvariantCulture) ?? "-",
            result.LiveAfterTwo?.ToString(CultureInfo.InvariantCulture) ?? "-", result.Leaks ? "leak" : result.Failure is null ? "ok" : "skip", result.Failure ?? "");

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    private static string SafeFileName(string name)
        => string.Concat(name.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
}
