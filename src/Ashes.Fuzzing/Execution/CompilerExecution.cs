namespace Ashes.Fuzzing.Execution;

internal sealed class CompilerExecution
{
    internal async Task<(ProcessResult Compile, ProcessResult? Run)> CompileAndRunAsync(
        string source,
        string repositoryRoot,
        string target,
        string optimization,
        TimeSpan compilerTimeout,
        TimeSpan programTimeout,
        int maximumOutputBytes,
        CancellationToken cancellationToken,
        bool disableReuse = false,
        bool disableTraitSpecialization = false,
        bool measurePeakRss = false,
        bool leakCheck = false,
        string? stage1Compiler = null)
    {
        string temporaryRoot = Directory.CreateTempSubdirectory("ashes-fuzz-").FullName;
        try
        {
            string sourcePath = Path.Combine(temporaryRoot, "case.ash");
            string extension = target.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "";
            string executablePath = Path.Combine(temporaryRoot, "case" + extension);
            await File.WriteAllTextAsync(sourcePath, source, cancellationToken).ConfigureAwait(false);
            string effectiveTarget = string.Equals(target, "host", StringComparison.Ordinal) ? HostTarget() : target;
            (string fileName, IReadOnlyList<string> arguments) = stage1Compiler is null
                ? Stage0Command(repositoryRoot, optimization, effectiveTarget, sourcePath, executablePath, disableReuse, disableTraitSpecialization)
                : Stage1Command(stage1Compiler, sourcePath, executablePath, disableReuse);
            // A leak check builds the program with the runtime's live-cell count (ASHES_RC_LEAKCHECK).
            Dictionary<string, string> environment = new(StringComparer.Ordinal);
            if (leakCheck)
            {
                environment["ASHES_RC_LEAKCHECK"] = "1";
            }
            ProcessResult compile = await ProcessTimeout.RunAsync(fileName, arguments, repositoryRoot, compilerTimeout, maximumOutputBytes, cancellationToken, environment: environment).ConfigureAwait(false);
            if (compile.ExitCode != 0 || compile.TimedOut || !File.Exists(executablePath) || !CanExecute(effectiveTarget))
            {
                return (compile, null);
            }
            ProcessResult run = measurePeakRss
                ? await ProcessTimeout.RunWithNativePeakRssAsync(executablePath, [], temporaryRoot, programTimeout, maximumOutputBytes, cancellationToken).ConfigureAwait(false)
                : await ProcessTimeout.RunAsync(executablePath, [], temporaryRoot, programTimeout, maximumOutputBytes, cancellationToken).ConfigureAwait(false);
            return (compile, run);
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static (string FileName, IReadOnlyList<string> Arguments) Stage0Command(
        string repositoryRoot,
        string optimization,
        string target,
        string sourcePath,
        string executablePath,
        bool disableReuse,
        bool disableTraitSpecialization)
    {
        string cliDll = Path.Combine(repositoryRoot, "src", "Ashes.Cli", "bin", "Release", "net10.0", "ashes.dll");
        IReadOnlyList<string> prefix = File.Exists(cliDll)
            ? [cliDll, "compile"]
            : ["run", "--project", Path.Combine(repositoryRoot, "src", "Ashes.Cli", "Ashes.Cli.csproj"), "--configuration", "Release", "--", "compile"];
        return ("dotnet", CompileArguments(prefix, optimization, target, sourcePath, executablePath, disableReuse, disableTraitSpecialization));
    }

    // The self-hosted compiler recurses deeply, so it runs under a 1 GB stack; it compiles for the
    // host only and takes no optimization or target flag. It finds the shipped standard library
    // under the working directory, the repository root.
    private static (string FileName, IReadOnlyList<string> Arguments) Stage1Command(
        string stage1Compiler,
        string sourcePath,
        string executablePath,
        bool disableReuse)
    {
        List<string> arguments = ["-c", "ulimit -s 1048576; exec \"$0\" \"$@\"", stage1Compiler, "compile", sourcePath, "-o", executablePath];
        if (disableReuse)
        {
            arguments.Add("--debug-disable-reuse");
        }
        return ("bash", arguments);
    }

    private static IReadOnlyList<string> CompileArguments(
        IReadOnlyList<string> prefix,
        string optimization,
        string target,
        string sourcePath,
        string executablePath,
        bool disableReuse,
        bool disableTraitSpecialization)
    {
        List<string> arguments = [.. prefix, optimization, "--target", target];
        if (disableReuse)
        {
            arguments.Add("--debug-disable-reuse");
        }
        if (disableTraitSpecialization)
        {
            arguments.Add("--debug-disable-trait-specialization");
        }
        arguments.Add(sourcePath);
        arguments.Add("-o");
        arguments.Add(executablePath);
        return arguments;
    }

    private static string HostTarget() => OperatingSystem.IsWindows()
        ? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64"
        : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    private static bool CanExecute(string target) => string.Equals(target, HostTarget(), StringComparison.Ordinal);

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
