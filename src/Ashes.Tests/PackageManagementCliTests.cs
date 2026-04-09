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
            var result = await RunCliAsync(["init"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Created");
            result.Output.ShouldContain("ashes.json");

            var projectPath = Path.Combine(tempDir, "ashes.json");
            File.Exists(projectPath).ShouldBeTrue("ashes.json should be created");

            var json = await File.ReadAllTextAsync(projectPath);
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
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ashes.json"), "{}");

            var result = await RunCliAsync(["init"], workingDirectory: tempDir);

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
            var result = await RunCliAsync(["init"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);

            var projectPath = Path.Combine(tempDir, "ashes.json");
            var json = await File.ReadAllTextAsync(projectPath);
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
            await File.WriteAllTextAsync(Path.Combine(srcDir, "Main.ash"), "Ashes.IO.print(42)\n");

            var result = await RunCliAsync(["init"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);

            var mainContent = await File.ReadAllTextAsync(Path.Combine(srcDir, "Main.ash"));
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
            await RunCliAsync(["init"], workingDirectory: tempDir);

            var result = await RunCliAsync(["add", "json-parser"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Added");
            result.Output.ShouldContain("json-parser");

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json"));
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
            await RunCliAsync(["init"], workingDirectory: tempDir);

            var result = await RunCliAsync(["add"], workingDirectory: tempDir);

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
            var result = await RunCliAsync(["add", "some-pkg"], workingDirectory: tempDir);

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
            await RunCliAsync(["init"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-a"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-b"], workingDirectory: tempDir);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json"));
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
            await RunCliAsync(["init"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-a"], workingDirectory: tempDir);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json"));
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
            await RunCliAsync(["init"], workingDirectory: tempDir);
            await RunCliAsync(["add", "json-parser"], workingDirectory: tempDir);

            var result = await RunCliAsync(["remove", "json-parser"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Removed");
            result.Output.ShouldContain("json-parser");

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json"));
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
            await RunCliAsync(["init"], workingDirectory: tempDir);

            var result = await RunCliAsync(["remove"], workingDirectory: tempDir);

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
            var result = await RunCliAsync(["remove", "some-pkg"], workingDirectory: tempDir);

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
            await RunCliAsync(["init"], workingDirectory: tempDir);

            var result = await RunCliAsync(["remove", "nonexistent-pkg"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("not in dependencies");
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
            await RunCliAsync(["init"], workingDirectory: tempDir);
            await RunCliAsync(["add", "only-pkg"], workingDirectory: tempDir);
            await RunCliAsync(["remove", "only-pkg"], workingDirectory: tempDir);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json"));
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
            await RunCliAsync(["init"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-a"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-b"], workingDirectory: tempDir);
            await RunCliAsync(["remove", "pkg-a"], workingDirectory: tempDir);

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json"));
            json.ShouldNotContain("pkg-a");
            json.ShouldContain("pkg-b");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ──────────────── ashes install ────────────────

    [Test]
    public async Task Install_should_list_dependencies()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir);
            await RunCliAsync(["add", "json-parser"], workingDirectory: tempDir);

            var result = await RunCliAsync(["install"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Dependencies");
            result.Output.ShouldContain("json-parser");
            result.Output.ShouldContain("registry not yet available");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Install_should_report_no_dependencies_when_empty()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir);

            var result = await RunCliAsync(["install"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("No dependencies");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Install_should_fail_without_ashes_json()
    {
        var tempDir = CreateTempDir();
        try
        {
            var result = await RunCliAsync(["install"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(1);
            result.Stderr.ShouldContain("No ashes.json found");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Install_should_list_multiple_dependencies()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-a"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-b"], workingDirectory: tempDir);
            await RunCliAsync(["add", "pkg-c"], workingDirectory: tempDir);

            var result = await RunCliAsync(["install"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("3");
            result.Output.ShouldContain("pkg-a");
            result.Output.ShouldContain("pkg-b");
            result.Output.ShouldContain("pkg-c");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ──────────────── --help and unexpected args ────────────────

    [Test]
    public async Task Init_help_should_show_usage()
    {
        var result = await RunCliAsync(["init", "--help"]);

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Commands");
    }

    [Test]
    public async Task Init_unexpected_arg_should_fail()
    {
        var result = await RunCliAsync(["init", "--unknown"]);

        result.ExitCode.ShouldBe(2);
        result.Stderr.ShouldContain("Unknown argument");
    }

    [Test]
    public async Task Add_help_should_show_usage()
    {
        var result = await RunCliAsync(["add", "--help"]);

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Commands");
    }

    [Test]
    public async Task Remove_help_should_show_usage()
    {
        var result = await RunCliAsync(["remove", "--help"]);

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Commands");
    }

    [Test]
    public async Task Install_help_should_show_usage()
    {
        var result = await RunCliAsync(["install", "--help"]);

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Commands");
    }

    [Test]
    public async Task Install_unexpected_arg_should_fail()
    {
        var result = await RunCliAsync(["install", "--unknown"]);

        result.ExitCode.ShouldBe(2);
        result.Stderr.ShouldContain("Unknown argument");
    }

    [Test]
    public async Task Add_should_not_treat_help_as_package_name()
    {
        var tempDir = CreateTempDir();
        try
        {
            await RunCliAsync(["init"], workingDirectory: tempDir);

            var result = await RunCliAsync(["add", "-h"], workingDirectory: tempDir);

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldContain("Commands");

            var json = await File.ReadAllTextAsync(Path.Combine(tempDir, "ashes.json"));
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
            await RunCliAsync(["init"], workingDirectory: tempDir);

            var result = await RunCliAsync(["remove", "-h"], workingDirectory: tempDir);

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
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ashes.json"), "not valid json!!!");

            var result = await RunCliAsync(["add", "some-pkg"], workingDirectory: tempDir);

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
        var startInfo = await CliTestHost.CreateStartInfoAsync(args);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
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
