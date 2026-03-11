using System.Diagnostics;
using Ashes.TestRunner;
using Ashes.Backend.Backends;
using Spectre.Console;
using Shouldly;

namespace Ashes.Tests;

public sealed class TestRunnerFixtureTests
{
    [Test]
    public void ParseTestDirectives_reads_text_and_binary_fixtures()
    {
        const string source = """
            // file: input.txt = hello
            // file: nested/dir/value.txt = spaced value
            // file-bytes: bad.bin = FF FE FD
            // stdin: a\n
            // expect: ok
            Ashes.IO.print(1)
            """;

        var directives = Runner.ParseTestDirectives(source);

        directives.HasExpected.ShouldBeTrue();
        directives.Expected.ShouldBe("ok");
        directives.Stdin.ShouldBe("a\n");
        directives.FileFixtures.Count.ShouldBe(3);
        directives.FileFixtures[0].RelativePath.ShouldBe("input.txt");
        directives.FileFixtures[1].RelativePath.ShouldBe("nested/dir/value.txt");
        directives.FileFixtures[2].RelativePath.ShouldBe("bad.bin");
        directives.FileFixtures[0].Content.ShouldBe(System.Text.Encoding.UTF8.GetBytes("hello"));
        directives.FileFixtures[2].Content.ShouldBe(new byte[] { 0xFF, 0xFE, 0xFD });
    }

    [Test]
    public void MaterializeTestFixtures_creates_nested_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes-test-runner-fixtures", Guid.NewGuid().ToString("N"));

        try
        {
            Runner.MaterializeTestFixtures(
                root,
                [
                    new Runner.TestFileFixture("input.txt", System.Text.Encoding.UTF8.GetBytes("hello")),
                    new Runner.TestFileFixture("nested/dir/value.bin", new byte[] { 0x00, 0x01, 0x02 })
                ]);

            File.ReadAllText(Path.Combine(root, "input.txt")).ShouldBe("hello");
            File.ReadAllBytes(Path.Combine(root, "nested", "dir", "value.bin")).ShouldBe(new byte[] { 0x00, 0x01, 0x02 });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void MaterializeTestFixtures_rejects_paths_outside_working_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes-test-runner-fixtures", Guid.NewGuid().ToString("N"));

        try
        {
            var ex = Should.Throw<InvalidOperationException>(() =>
                Runner.MaterializeTestFixtures(
                    root,
                    [new Runner.TestFileFixture("..\\escape.txt", System.Text.Encoding.UTF8.GetBytes("bad"))]));

            ex.Message.ShouldContain("escapes the test working directory");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void RunTests_times_out_tcp_fixture_when_program_never_connects()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes-test-runner-fixtures", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(root, "tcp-timeout.ash");
        var originalTimeout = Runner.TcpFixtureAcceptTimeout;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                filePath,
                "// tcp-server: accept\n// expect:\nAshes.IO.print(\"\")\n");

            Runner.TcpFixtureAcceptTimeout = TimeSpan.FromMilliseconds(250);

            using var output = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(output)
            });

            var exitCode = Runner.RunTests([filePath], BackendFactory.DefaultForCurrentOS(), console);

            exitCode.ShouldBe(1);
            output.ToString().ShouldContain("tcp fixture timed out waiting for connection");
        }
        finally
        {
            Runner.TcpFixtureAcceptTimeout = originalTimeout;

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
