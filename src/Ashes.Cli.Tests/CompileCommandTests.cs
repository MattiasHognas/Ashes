using System.Diagnostics;
using System.Text.Json;
using Ashes.Cli.Package;
using Shouldly;

namespace Ashes.Cli.Tests;

public sealed class CompileCommandTests
{
    [Test]
    public async Task Hermetic_runtime_assets_resolve_outside_repository_working_directory()
    {
        string root = TempDir();
        try
        {
            string sourcePath = Path.Combine(root, "Main.ash");
            string outputPath = Path.Combine(root, OperatingSystem.IsWindows() ? "Main.exe" : "Main");
            File.WriteAllText(sourcePath, "import Ashes.Number.Math\nAshes.IO.print(Ashes.Number.Math.floorToInt(Ashes.Number.Math.sqrt(81.0)))\n");

            (int exitCode, string output) = await RunCliAsync(
                root,
                "compile",
                sourcePath,
                "-o",
                outputPath).ConfigureAwait(false);

            exitCode.ShouldBe(0, output);
            File.Exists(outputPath).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Compile_debug_output_is_executable_on_unix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        string root = TempDir();
        try
        {
            string repoRoot = FindRepoRoot();
            string outPath = Path.Combine(root, "Main");
            ProcessStartInfo psi = new("dotnet")
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(Path.Combine(repoRoot, "src", "Ashes.Cli"));
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("compile");
            psi.ArgumentList.Add("--debug");
            psi.ArgumentList.Add("--expr");
            psi.ArgumentList.Add("Ashes.IO.print(42)");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outPath);

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet.");
            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            process.ExitCode.ShouldBe(0, stdout + stderr);
            UnixFileMode mode = File.GetUnixFileMode(outPath);
            mode.HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
            mode.HasFlag(UnixFileMode.GroupExecute).ShouldBeTrue();
            mode.HasFlag(UnixFileMode.OtherExecute).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Add_project_updates_only_the_selected_manifest()
    {
        string root = TempDir();
        try
        {
            string mainProject = Path.Combine(root, "ashes.json");
            string testProject = Path.Combine(root, "ashes-test.json");
            File.WriteAllText(mainProject, """{"entry":"Main.ash"}""");
            File.WriteAllText(testProject, """{"entry":"Test.ash"}""");

            (int exitCode, string output) = await RunCliAsync(
                root,
                "add",
                "json-parser",
                "--project",
                testProject).ConfigureAwait(false);

            exitCode.ShouldBe(0, output);
            using JsonDocument main = JsonDocument.Parse(
                File.ReadAllText(mainProject));
            using JsonDocument test = JsonDocument.Parse(
                File.ReadAllText(testProject));
            main.RootElement.TryGetProperty(
                "dependencies",
                out _).ShouldBeFalse();
            test.RootElement
                .GetProperty("dependencies")
                .GetProperty("json-parser")
                .GetString()
                .ShouldBe("*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(
        string workingDirectory,
        params string[] arguments)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(LockFile).Assembly.Location);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        string stdout = await process.StandardOutput.ReadToEndAsync()
            .ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync()
            .ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, stdout + stderr);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ashes.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Ashes repository root.");
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ashes-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
