using System.Diagnostics;
using Shouldly;

namespace Ashes.Cli.Tests;

public sealed class CompileCommandTests
{
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
