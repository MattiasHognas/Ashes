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
        bool disableTraitSpecialization = false)
    {
        string temporaryRoot = Directory.CreateTempSubdirectory("ashes-fuzz-").FullName;
        try
        {
            string sourcePath = Path.Combine(temporaryRoot, "case.ash");
            string extension = target.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "";
            string executablePath = Path.Combine(temporaryRoot, "case" + extension);
            await File.WriteAllTextAsync(sourcePath, source, cancellationToken).ConfigureAwait(false);
            string effectiveTarget = string.Equals(target, "host", StringComparison.Ordinal) ? HostTarget() : target;
            string cliDll = Path.Combine(repositoryRoot, "src", "Ashes.Cli", "bin", "Release", "net10.0", "ashes.dll");
            IReadOnlyList<string> arguments = File.Exists(cliDll)
                ? CompileArguments([cliDll, "compile"], optimization, effectiveTarget, sourcePath, executablePath, disableReuse, disableTraitSpecialization)
                : CompileArguments(["run", "--project", Path.Combine(repositoryRoot, "src", "Ashes.Cli", "Ashes.Cli.csproj"), "--configuration", "Release", "--", "compile"], optimization, effectiveTarget, sourcePath, executablePath, disableReuse, disableTraitSpecialization);
            ProcessResult compile = await ProcessTimeout.RunAsync("dotnet", arguments, repositoryRoot, compilerTimeout, maximumOutputBytes, cancellationToken).ConfigureAwait(false);
            if (compile.ExitCode != 0 || compile.TimedOut || !File.Exists(executablePath) || !CanExecute(effectiveTarget))
            {
                return (compile, null);
            }
            ProcessResult run = await ProcessTimeout.RunAsync(executablePath, [], temporaryRoot, programTimeout, maximumOutputBytes, cancellationToken).ConfigureAwait(false);
            return (compile, run);
        }
        finally
        {
            TryDeleteTemporaryDirectory(temporaryRoot);
        }
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
