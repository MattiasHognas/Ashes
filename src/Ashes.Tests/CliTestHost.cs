using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ashes.Tests;

internal static class CliTestHost
{
    private static readonly Lazy<Task<string>> CliAssemblyPath = new(EnsureCliAssemblyPathAsync);

    public static async Task<ProcessStartInfo> CreateStartInfoAsync(params string[] cliArgs)
    {
        var cliAssemblyPath = await CliAssemblyPath.Value;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = GetRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(cliAssemblyPath);
        foreach (var arg in cliArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static async Task<string> EnsureCliAssemblyPathAsync()
    {
        var configuration = GetCurrentConfiguration();
        var cliAssemblyPath = Path.Combine(GetRepoRoot(), "src", "Ashes.Cli", "bin", configuration, "net10.0", "ashes.dll");
        var buildStartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = GetRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        buildStartInfo.ArgumentList.Add("build");
        buildStartInfo.ArgumentList.Add(GetCliProjectPath());
        buildStartInfo.ArgumentList.Add("--configuration");
        buildStartInfo.ArgumentList.Add(configuration);
        buildStartInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(buildStartInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to build Ashes.Cli for tests.{Environment.NewLine}{stdout}{stderr}");
        }

        return cliAssemblyPath;
    }

    private static string GetCurrentConfiguration()
    {
        var configurationDirectory = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent?.Name;
        return string.IsNullOrWhiteSpace(configurationDirectory) ? "Debug" : configurationDirectory;
    }

    private static string GetRepoRoot([CallerFilePath] string? callerFile = null)
    {
        var sourceDir = Path.GetDirectoryName(callerFile)!;
        return Path.GetFullPath(Path.Combine(sourceDir, "..", ".."));
    }

    private static string GetCliProjectPath()
    {
        return Path.Combine(GetRepoRoot(), "src", "Ashes.Cli", "Ashes.Cli.csproj");
    }
}
