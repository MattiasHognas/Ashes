using System.Diagnostics;
using Shouldly;

namespace Ashes.Tests;

public sealed class PackageManagementCliTests
{
    // ──────────────── ashes init ────────────────

    [Test]
    public async Task Init_should_create_ashes_json_and_main_ash()
    {
        var tempDir = CreateTempDir();
        try
        {
            var result = await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Created");
            result.Output.ShouldContain("ashes.json");

            var projectPath = Path.Combine(tempDir, "ashes.json");
            File.Exists(projectPath).ShouldBeTrue("ashes.json should be created");

            var json = await File.ReadAllTextAsync(projectPath).ConfigureAwait(false);
            json.ShouldContain("\"entry\"");
            json.ShouldContain("src/Main.ash");
            json.ShouldContain("\"sourceRoots\"");

            var mainPath = Path.Combine(tempDir, "src", "Main.ash");
            File.Exists(mainPath).ShouldBeTrue("src/Main.ash should be created");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Init_should_fail_if_ashes_json_already_exists()
    {
        var tempDir = CreateTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ashes.json"), "{}").ConfigureAwait(false);

            var result = await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("ashes.json already exists");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Init_should_use_directory_name_as_project_name()
    {
        var tempDir = CreateTempDir();
        try
        {
            var result = await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);

            var projectPath = Path.Combine(tempDir, "ashes.json");
            var json = await File.ReadAllTextAsync(projectPath).ConfigureAwait(false);
            var dirName = new DirectoryInfo(tempDir).Name;
            json.ShouldContain($"\"{dirName}\"");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Init_should_not_overwrite_existing_main_ash()
    {
        var tempDir = CreateTempDir();
        try
        {
            var srcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(srcDir);
            await File.WriteAllTextAsync(Path.Combine(srcDir, "Main.ash"), "Ashes.IO.print(42)\n").ConfigureAwait(false);

            var result = await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);

            var mainContent = await File.ReadAllTextAsync(Path.Combine(srcDir, "Main.ash")).ConfigureAwait(false);
            mainContent.ShouldContain("42");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ──────────────── ashes add ────────────────

    [Test]
    public async Task Add_should_add_package_to_dependencies()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["add", "json-parser"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Added");
            result.Output.ShouldContain("json-parser");

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldContain("\"dependencies\"");
            json.ShouldContain("\"json-parser\"");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Add_should_fail_without_package_name()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["add"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("Missing package name");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Add_should_fail_without_ashes_json()
    {
        var tempDir = CreateTempDir();
        try
        {
            var result = await RunCliAsync(["add", "some-pkg"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("No ashes.json found");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Add_should_preserve_existing_dependencies()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "pkg-a"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "pkg-b"], workingDirectory: tempDir).ConfigureAwait(false);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldContain("\"pkg-a\"");
            json.ShouldContain("\"pkg-b\"");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Add_should_preserve_existing_project_fields()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "pkg-a"], workingDirectory: tempDir).ConfigureAwait(false);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldContain("\"entry\"");
            json.ShouldContain("\"sourceRoots\"");
            json.ShouldContain("src/Main.ash");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ──────────────── ashes remove ────────────────

    [Test]
    public async Task Remove_should_remove_package_from_dependencies()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "json-parser"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["remove", "json-parser"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Removed");
            result.Output.ShouldContain("json-parser");

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldNotContain("json-parser");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Remove_should_fail_without_package_name()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["remove"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("Missing package name");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Remove_should_fail_without_ashes_json()
    {
        var tempDir = CreateTempDir();
        try
        {
            var result = await RunCliAsync(["remove", "some-pkg"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("No ashes.json found");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Remove_should_fail_if_package_not_in_dependencies()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["remove", "nonexistent-pkg"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("not a dependency");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Remove_should_omit_dependencies_field_when_empty()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "only-pkg"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["remove", "only-pkg"], workingDirectory: tempDir).ConfigureAwait(false);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldNotContain("dependencies");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Remove_should_preserve_other_packages()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "pkg-a"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "pkg-b"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["remove", "pkg-a"], workingDirectory: tempDir).ConfigureAwait(false);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldNotContain("pkg-a");
            json.ShouldContain("pkg-b");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ──────────────── ashes restore ────────────────

    [Test]
    public async Task Restore_reports_no_dependencies_when_empty()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["restore"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("No dependencies");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Restore_lists_a_path_dependency()
    {
        var tempDir = CreateTempDir();
        try
        {
            var depDir = Path.Combine(tempDir, "dep");
            Directory.CreateDirectory(Path.Combine(depDir, "src"));
            await File.WriteAllTextAsync(Path.Combine(depDir, "ashes.json"),
                "{ \"name\": \"greet\", \"entry\": \"src/Greet.ash\", \"sourceRoots\": [\"src\"] }").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(depDir, "src", "Greet.ash"), "let hello = given (n) -> n\n").ConfigureAwait(false);

            var appDir = Path.Combine(tempDir, "app");
            Directory.CreateDirectory(appDir);
            await RunCliAsync(["init"], workingDirectory: appDir).ConfigureAwait(false);
            await RunCliAsync(["add", "greet", "--path", "../dep"], workingDirectory: appDir).ConfigureAwait(false);

            var result = await RunCliAsync(["restore"], workingDirectory: appDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Restored");
            result.Output.ShouldContain("greet");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Restore_fails_without_ashes_json()
    {
        var tempDir = CreateTempDir();
        try
        {
            var result = await RunCliAsync(["restore"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("No ashes.json found");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Add_dev_writes_dev_dependencies()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);
            await RunCliAsync(["add", "test-helper", "--dev"], workingDirectory: tempDir).ConfigureAwait(false);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldContain("devDependencies");
            json.ShouldContain("test-helper");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Install_is_retired()
    {
        var result = await RunCliAsync(["install"]).ConfigureAwait(false);

        result.ExitCode.ShouldBe(1);
        result.Stderr.ShouldContain("retired");
    }

    // ──────────────── --help and unexpected args ────────────────

    [Test]
    public async Task Init_help_should_show_usage()
    {
        var result = await RunCliAsync(["init", "--help"]).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Commands");
    }

    [Test]
    public async Task Init_unexpected_arg_should_fail()
    {
        var result = await RunCliAsync(["init", "--unknown"]).ConfigureAwait(false);

        result.ExitCode.ShouldBe(2);
        result.Stderr.ShouldContain("Unknown argument");
    }

    [Test]
    public async Task Add_help_should_show_usage()
    {
        var result = await RunCliAsync(["add", "--help"]).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Commands");
    }

    [Test]
    public async Task Remove_help_should_show_usage()
    {
        var result = await RunCliAsync(["remove", "--help"]).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Commands");
    }

    [Test]
    public async Task Add_should_not_treat_help_as_package_name()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["add", "-h"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Commands");

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json")).ConfigureAwait(false);
            json.ShouldNotContain("-h");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Remove_should_not_treat_help_as_package_name()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir).ConfigureAwait(false);

            var result = await RunCliAsync(["remove", "-h"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Commands");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Add_should_fail_on_invalid_json()
    {
        var tempDir = CreateTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ashes.json"), "not valid json!!!").ConfigureAwait(false);

            var result = await RunCliAsync(["add", "some-pkg"], workingDirectory: tempDir).ConfigureAwait(false);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("Invalid ashes.json");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ──────────────── helpers ────────────────

    private static async Task<CliCommandResult> RunCliAsync(string[] args, string? workingDirectory = null)
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync(args).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CliCommandResult(process.ExitCode, stdout, stderr, stdout + stderr);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashes-pkg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record CliCommandResult(int ExitCode, string Stdout, string Stderr, string Output);
}
