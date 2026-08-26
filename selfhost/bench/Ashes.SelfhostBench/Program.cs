using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ashes.Formatter;
using Ashes.Frontend;
using Ashes.Semantics;

namespace Ashes.SelfhostBench;

/// <summary>
/// Times the .NET compiler's front phases over a corpus of <c>.ash</c> files and, when a compiled
/// stage-1 bench binary is given, the self-hosted compiler's phases over the same corpus, then
/// prints both side by side. Each phase runs several times and the fastest run is reported.
/// </summary>
internal static class Program
{
    private static readonly string[] DefaultCorpus = ["tests", "lib/Ashes", "selfhost/packages", "examples"];

    private static readonly string[] Stage1Phases = ["header", "lex", "parse", "format", "infer"];

    private sealed record BenchSource(string Path, string Source, string Body, bool ImportFree);

    private sealed record PhaseResult(string Phase, long Count, long Millis, string? Note = null);

    private sealed class Options
    {
        public List<string> Corpus { get; } = [];

        public int Iterations { get; set; } = 3;

        public string? Stage1Binary { get; set; }

        public string? FileListPath { get; set; }

        public bool Markdown { get; set; } = true;
    }

    private static int Main(string[] args)
    {
        Options? options = ParseOptions(args);
        if (options is null)
        {
            Console.Error.WriteLine("usage: ashes-selfhost-bench [--corpus <dir>]... [--iterations N] [--stage1 <stage1-bench-binary>] [--file-list <path>]");
            return 2;
        }

        List<BenchSource> sources = LoadCorpus(options.Corpus.Count == 0 ? DefaultCorpus : options.Corpus);
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("no .ash files found in the corpus");
            return 1;
        }

        List<PhaseResult> stage0 = RunStage0(sources, options.Iterations);
        List<PhaseResult> stage1 = options.Stage1Binary is null
            ? []
            : RunStage1(options.Stage1Binary, sources, options.Iterations, options.FileListPath);

        PrintReport(sources, stage0, stage1, options);
        return 0;
    }

    private static Options? ParseOptions(string[] args)
    {
        Options options = new();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus" when i + 1 < args.Length:
                    options.Corpus.Add(args[++i]);
                    break;
                case "--iterations" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int iterations) && iterations > 0:
                    options.Iterations = iterations;
                    i++;
                    break;
                case "--stage1" when i + 1 < args.Length:
                    options.Stage1Binary = args[++i];
                    break;
                case "--file-list" when i + 1 < args.Length:
                    options.FileListPath = args[++i];
                    break;
                case "--plain":
                    options.Markdown = false;
                    break;
                default:
                    return null;
            }
        }

        return options;
    }

    private static List<BenchSource> LoadCorpus(IEnumerable<string> roots)
    {
        List<string> paths = [];
        foreach (string root in roots)
        {
            if (File.Exists(root))
            {
                paths.Add(Path.GetFullPath(root));
                continue;
            }

            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"corpus root not found: {root}");
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*.ash", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(file);
                string[] segments = full.Split(Path.DirectorySeparatorChar);
                if (segments.Contains("out", StringComparer.Ordinal) || segments.Contains("dist", StringComparer.Ordinal))
                {
                    continue;
                }

                paths.Add(full);
            }
        }

        paths.Sort(StringComparer.Ordinal);
        List<BenchSource> sources = [];
        foreach (string path in paths.Distinct(StringComparer.Ordinal))
        {
            string source = File.ReadAllText(path);
            try
            {
                ParsedImportHeader header = ProjectSupport.ParseImportHeader(source, path);
                bool importFree = header.ImportNames.Count == 0 && header.ImportSelectors.Count == 0 && header.ImportAliases.Count == 0;
                sources.Add(new BenchSource(path, source, header.SourceWithoutImports, importFree));
            }
            catch (InvalidOperationException)
            {
                // A malformed import header is not part of the corpus either compiler can process.
            }
        }

        return sources;
    }

    private static List<PhaseResult> RunStage0(List<BenchSource> sources, int iterations)
    {
        List<PhaseResult> results = [];
        results.Add(Measure("header", iterations, () => CountHeaders(sources)));
        results.Add(Measure("lex", iterations, () => CountTokens(sources)));
        results.Add(Measure("parse", iterations, () => CountParsed(sources)));

        List<(Frontend.Program Program, bool ImportFree)> programs = ParseAll(sources);
        results.Add(Measure("format", iterations, () => CountFormatted(programs)));
        results.Add(Measure("lower", iterations, () => CountLowered(programs)));

        List<IrProgram> lowered = LowerAll(programs);
        results.Add(Measure("optimize", iterations, () => CountOptimized(lowered)));
        return results;
    }

    private static PhaseResult Measure(string phase, int iterations, Func<long> work)
    {
        long best = long.MaxValue;
        long count = 0;
        for (int i = 0; i < iterations; i++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            count = work();
            stopwatch.Stop();
            best = Math.Min(best, stopwatch.ElapsedMilliseconds);
        }

        return new PhaseResult(phase, count, best);
    }

    private static long CountHeaders(List<BenchSource> sources)
    {
        long count = 0;
        foreach (BenchSource source in sources)
        {
            try
            {
                _ = ProjectSupport.ParseImportHeader(source.Source, source.Path);
                count++;
            }
            catch (InvalidOperationException)
            {
            }
        }

        return count;
    }

    private static long CountTokens(List<BenchSource> sources)
    {
        long count = 0;
        foreach (BenchSource source in sources)
        {
            Lexer lexer = new(source.Body, new Diagnostics());
            while (lexer.Next().Kind != TokenKind.EOF)
            {
                count++;
            }
        }

        return count;
    }

    private static long CountParsed(List<BenchSource> sources)
    {
        long count = 0;
        foreach (BenchSource source in sources)
        {
            Diagnostics diagnostics = new();
            _ = new Parser(source.Body, diagnostics).ParseProgram();
            if (diagnostics.StructuredErrors.Count == 0)
            {
                count++;
            }
        }

        return count;
    }

    private static List<(Frontend.Program Program, bool ImportFree)> ParseAll(List<BenchSource> sources)
    {
        List<(Frontend.Program, bool)> programs = [];
        foreach (BenchSource source in sources)
        {
            Diagnostics diagnostics = new();
            Frontend.Program program = new Parser(source.Body, diagnostics).ParseProgram();
            if (diagnostics.StructuredErrors.Count == 0)
            {
                programs.Add((program, source.ImportFree));
            }
        }

        return programs;
    }

    private static long CountFormatted(List<(Frontend.Program Program, bool ImportFree)> programs)
    {
        long bytes = 0;
        foreach ((Frontend.Program program, _) in programs)
        {
            bytes += Encoding.UTF8.GetByteCount(Formatter.Formatter.Format(program));
        }

        return bytes;
    }

    private static long CountLowered(List<(Frontend.Program Program, bool ImportFree)> programs)
    {
        long count = 0;
        foreach ((Frontend.Program program, bool importFree) in programs)
        {
            if (!importFree)
            {
                continue;
            }

            if (TryLower(program) is not null)
            {
                count++;
            }
        }

        return count;
    }

    private static List<IrProgram> LowerAll(List<(Frontend.Program Program, bool ImportFree)> programs)
    {
        List<IrProgram> lowered = [];
        foreach ((Frontend.Program program, bool importFree) in programs)
        {
            if (importFree && TryLower(program) is { } ir)
            {
                lowered.Add(ir);
            }
        }

        return lowered;
    }

    private static IrProgram? TryLower(Frontend.Program program)
    {
        Diagnostics diagnostics = new();
        try
        {
            IrProgram ir = new Lowering(diagnostics).Lower(program);
            return diagnostics.StructuredErrors.Count == 0 ? ir : null;
        }
        catch (CompileDiagnosticException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static long CountOptimized(List<IrProgram> lowered)
    {
        long functions = 0;
        foreach (IrProgram ir in lowered)
        {
            functions += IrOptimizer.Optimize(ir).Functions.Count;
        }

        return functions;
    }

    private static List<PhaseResult> RunStage1(string binary, List<BenchSource> sources, int iterations, string? fileListPath)
    {
        string listPath = fileListPath ?? Path.Combine(Path.GetTempPath(), $"ashes-selfhost-bench-{Environment.ProcessId}.txt");
        List<string> paths = sources.Select(source => source.Path).ToList();
        File.WriteAllLines(listPath, paths);
        List<PhaseResult> results = [];
        foreach (string phase in Stage1Phases)
        {
            PhaseResult result = RunStage1Phase(binary, listPath, iterations, phase);
            if (result.Note is not null && result.Note.StartsWith("crashed", StringComparison.Ordinal))
            {
                result = RunStage1PhaseExcludingCrashers(binary, listPath, paths, iterations, phase);
            }

            results.Add(result);
        }

        if (fileListPath is null)
        {
            File.Delete(listPath);
        }

        return results;
    }

    /// <summary>
    /// A stage-1 crash on one corpus file would lose the whole phase, and the self-hosted compiler
    /// still has such files. Find them by running the phase on each file alone, time the phase over
    /// the rest, and report how many were excluded: the crash count is a result in its own right.
    /// </summary>
    private static PhaseResult RunStage1PhaseExcludingCrashers(string binary, string listPath, List<string> paths, int iterations, string phase)
    {
        string singlePath = listPath + ".one";
        List<string> surviving = [];
        int crashed = 0;
        foreach (string path in paths)
        {
            File.WriteAllLines(singlePath, [path]);
            PhaseResult single = RunStage1Phase(binary, singlePath, 1, phase);
            if (single.Note is not null && single.Note.StartsWith("crashed", StringComparison.Ordinal))
            {
                crashed++;
                Console.Error.WriteLine($"stage 1 {phase}: crashes on {path}");
            }
            else
            {
                surviving.Add(path);
            }
        }

        File.Delete(singlePath);
        string survivingPath = listPath + ".surviving";
        File.WriteAllLines(survivingPath, surviving);
        PhaseResult result = RunStage1Phase(binary, survivingPath, iterations, phase);
        File.Delete(survivingPath);
        return result.Note is null
            ? result with { Note = $"{crashed} file(s) crashed and were excluded" }
            : result;
    }

    private static PhaseResult RunStage1Phase(string binary, string listPath, int iterations, string phase)
    {
        ProcessStartInfo startInfo = new(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(listPath);
        startInfo.ArgumentList.Add(iterations.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(phase);
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return new PhaseResult(phase, 0, 0, "could not start");
        }

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string detail = error.Trim().Length > 0 ? error.Trim() : $"exit {process.ExitCode}";
            return new PhaseResult(phase, 0, 0, $"crashed ({detail})");
        }

        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length == 3
                && string.Equals(parts[0], phase, StringComparison.Ordinal)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long count)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long millis))
            {
                return new PhaseResult(phase, count, millis);
            }
        }

        return new PhaseResult(phase, 0, 0, "no result line");
    }

    private static void PrintReport(List<BenchSource> sources, List<PhaseResult> stage0, List<PhaseResult> stage1, Options options)
    {
        int importFree = sources.Count(source => source.ImportFree);
        Console.WriteLine($"corpus: {sources.Count} files ({importFree} import-free), fastest of {options.Iterations} runs, milliseconds");
        Console.WriteLine();
        string separator = options.Markdown ? " | " : "  ";
        string prefix = options.Markdown ? "| " : "";
        string suffix = options.Markdown ? " |" : "";
        Console.WriteLine($"{prefix}Phase{separator}Stage 0 (.NET) ms{separator}Stage 0 count{separator}Stage 1 (Ashes) ms{separator}Stage 1 count{separator}Stage 1 / Stage 0{separator}Notes{suffix}");
        if (options.Markdown)
        {
            Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | --- |");
        }

        foreach (string phase in new[] { "header", "lex", "parse", "format", "infer", "lower", "optimize" })
        {
            PhaseResult? s0 = stage0.FirstOrDefault(result => string.Equals(result.Phase, MapStage0Phase(phase), StringComparison.Ordinal));
            PhaseResult? s1 = stage1.FirstOrDefault(result => string.Equals(result.Phase, phase, StringComparison.Ordinal));
            if (s0 is null && s1 is null)
            {
                continue;
            }

            string label = phase switch
            {
                "infer" => "infer (stage 1) vs infer+lower (stage 0)",
                "lower" => "lower (stage 0 only; included in infer row)",
                "optimize" => "optimize IR (stage 0 only)",
                _ => phase,
            };
            if (string.Equals(phase, "lower", StringComparison.Ordinal) && s1 is null && stage1.Count > 0)
            {
                continue;
            }

            bool s1Failed = s1 is not null && s1.Note is not null && s1.Note.StartsWith("crashed", StringComparison.Ordinal);
            string s0Millis = s0 is null ? "-" : s0.Millis.ToString(CultureInfo.InvariantCulture);
            string s0Count = s0 is null ? "-" : s0.Count.ToString(CultureInfo.InvariantCulture);
            string s1Millis = s1 is null || s1Failed ? "-" : s1.Millis.ToString(CultureInfo.InvariantCulture);
            string s1Count = s1 is null || s1Failed ? "-" : s1.Count.ToString(CultureInfo.InvariantCulture);
            string ratio = s0 is null || s1 is null || s1Failed || s0.Millis == 0
                ? "-"
                : ((double)s1.Millis / s0.Millis).ToString("0.00", CultureInfo.InvariantCulture) + "x";
            string note = s1?.Note ?? "";
            Console.WriteLine($"{prefix}{label}{separator}{s0Millis}{separator}{s0Count}{separator}{s1Millis}{separator}{s1Count}{separator}{ratio}{separator}{note}{suffix}");
        }

        Console.WriteLine();
        Console.WriteLine("counts: header = files whose import header parsed; lex = tokens; parse = files without diagnostics; format = bytes rendered; infer/lower = import-free programs accepted; optimize = IR functions after optimization.");
    }

    private static string MapStage0Phase(string phase) => phase switch
    {
        "infer" => "lower",
        _ => phase,
    };
}
