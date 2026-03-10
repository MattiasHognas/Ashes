using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Spectre.Console;

namespace Ashes.TestRunner;

public static class Runner
{
    private const string TcpPortPlaceholder = "__TCP_PORT__";

    public sealed record TestFileFixture(string RelativePath, byte[] Content);

    public sealed record TcpServerFixture(
        bool Enabled,
        string? ExpectedText,
        string? SendText);

    public sealed record TestDirectives(
        string Expected,
        bool HasExpected,
        int ExpectedExitCode,
        bool IsCompileError,
        string? Stdin,
        IReadOnlyList<TestFileFixture> FileFixtures,
        TcpServerFixture TcpServer);

    public sealed record TestResult(string Path, bool Passed, string Expected, string Actual, int ExitCode, int ExpectedExitCode, bool HasExpected = true, long ElapsedMs = 0);

    public static int RunTests(IEnumerable<string> paths, string? targetId, IAnsiConsole console)
    {
        targetId ??= BackendFactory.DefaultForCurrentOS();

        var files = DiscoverAshFiles(paths.Any() ? paths : new[] { "tests" })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            console.MarkupLine("[yellow]No tests found.[/]");
            return 0;
        }

        var results = new List<TestResult>();

        foreach (var file in files)
        {
            var rawSource = File.ReadAllText(file);
            var directives = ParseTestDirectives(rawSource);
            var expected = directives.Expected;
            var hasExpected = directives.HasExpected;
            var expectedExitCode = directives.ExpectedExitCode;
            var isCompileError = directives.IsCompileError;
            var stdin = directives.Stdin;
            if (!hasExpected)
            {
                results.Add(new TestResult(file, Passed: true, Expected: "", Actual: "", ExitCode: 0, ExpectedExitCode: 0, HasExpected: false));
                continue;
            }

            var sw = Stopwatch.StartNew();
            int exit;
            string actual;
            string stderr = "";
            TcpServerInstance? tcpServer = null;
            try
            {
                string? sourceOverride = null;
                if (directives.TcpServer.Enabled)
                {
                    tcpServer = TcpServerInstance.Start(directives.TcpServer);
                    sourceOverride = rawSource.Replace(TcpPortPlaceholder, tcpServer.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                }

                var image = CompileFileToImage(file, targetId, sourceOverride);
                var (runExit, stdout, runStderr) = RunImageCapture(image, targetId, stdin, directives.FileFixtures);
                exit = runExit;
                actual = (stdout ?? "").TrimEnd();
                stderr = runStderr ?? "";
                if (tcpServer is not null)
                {
                    var fixtureError = tcpServer.Complete();
                    if (!string.IsNullOrWhiteSpace(fixtureError))
                    {
                        exit = 1;
                        actual = fixtureError;
                    }
                }
            }
            catch (CompileDiagnosticException ex)
            {
                exit = 1;
                var isUnexpectedFailure = expectedExitCode != 1;
                actual = (isUnexpectedFailure || isCompileError)
                    ? DiagnosticTextRenderer.RenderCompilerDiagnostics(ex, source: null, displayPath: file).TrimEnd()
                    : "";
            }
            catch (InvalidOperationException ex)
            {
                exit = 1;
                var isUnexpectedFailure = expectedExitCode != 1;
                actual = (isUnexpectedFailure || isCompileError)
                    ? DiagnosticTextRenderer.RenderFailure("compile error", ex.Message ?? string.Empty, file).TrimEnd()
                    : "";
            }
            finally
            {
                tcpServer?.Dispose();
            }
            sw.Stop();

            var exp = expected.TrimEnd();
            var passed = exit == expectedExitCode && (isCompileError
                ? actual.Contains(exp, StringComparison.Ordinal)
                : actual == exp);

            // If stderr present, append for diagnostics in 'Actual' when the test fails
            if (!string.IsNullOrWhiteSpace(stderr) && !passed)
            {
                actual = actual + "\n[stderr]\n" + stderr.TrimEnd();
            }

            results.Add(new TestResult(file, passed, exp, actual, exit, expectedExitCode, ElapsedMs: sw.ElapsedMilliseconds));
        }

        RenderResults(results, console);

        return results.Any(r => !r.Passed && r.HasExpected) ? 1 : 0;
    }

    private static IEnumerable<string> DiscoverAshFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p) && p.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(p);
                continue;
            }

            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*.ash", SearchOption.AllDirectories))
                {
                    yield return Path.GetFullPath(f);
                }
            }
        }
    }

    public static TestDirectives ParseTestDirectives(string source)
    {
        string expected = "";
        var hasExpected = false;
        var expectedExitCode = 0;
        var isCompileError = false;
        string? stdin = null;
        var fileFixtures = new List<TestFileFixture>();
        var tcpServerEnabled = false;
        string? tcpExpectedText = null;
        string? tcpSendText = null;

        using var sr = new StringReader(source);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            var commentPrefixIndex = line.IndexOf("//", StringComparison.Ordinal);
            var commentText = commentPrefixIndex >= 0
                ? line[(commentPrefixIndex + 2)..].TrimStart()
                : trimmed[2..].TrimStart();

            if (commentText.StartsWith("expect:", StringComparison.OrdinalIgnoreCase))
            {
                expected = commentText.Substring("expect:".Length).Trim();
                hasExpected = true;
                isCompileError = false;
                continue;
            }

            if (commentText.StartsWith("expect-compile-error:", StringComparison.OrdinalIgnoreCase))
            {
                expected = commentText.Substring("expect-compile-error:".Length).Trim();
                hasExpected = true;
                expectedExitCode = 1;
                isCompileError = true;
                continue;
            }

            if (commentText.StartsWith("exit:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(commentText.Substring("exit:".Length).Trim(), out var parsedExitCode))
            {
                expectedExitCode = parsedExitCode;
                continue;
            }

            if (commentText.StartsWith("stdin:", StringComparison.OrdinalIgnoreCase))
            {
                stdin = DecodeTestInput(commentText.Substring("stdin:".Length).Trim());
                continue;
            }

            if (commentText.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                fileFixtures.Add(ParseFileFixture(commentText.Substring("file:".Length), parseBytes: false));
                continue;
            }

            if (commentText.StartsWith("file-bytes:", StringComparison.OrdinalIgnoreCase))
            {
                fileFixtures.Add(ParseFileFixture(commentText.Substring("file-bytes:".Length), parseBytes: true));
                continue;
            }

            if (commentText.StartsWith("tcp-server:", StringComparison.OrdinalIgnoreCase))
            {
                var mode = commentText.Substring("tcp-server:".Length).Trim();
                if (!string.Equals(mode, "accept", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unsupported tcp-server mode '{mode}'. Expected 'accept'.");
                }

                tcpServerEnabled = true;
                continue;
            }

            if (commentText.StartsWith("tcp-expect:", StringComparison.OrdinalIgnoreCase))
            {
                tcpServerEnabled = true;
                tcpExpectedText = DecodeTestInput(commentText.Substring("tcp-expect:".Length).Trim());
                continue;
            }

            if (commentText.StartsWith("tcp-send:", StringComparison.OrdinalIgnoreCase))
            {
                tcpServerEnabled = true;
                tcpSendText = DecodeTestInput(commentText.Substring("tcp-send:".Length).Trim());
            }
        }

        return new TestDirectives(
            expected,
            hasExpected,
            expectedExitCode,
            isCompileError,
            stdin,
            fileFixtures,
            new TcpServerFixture(tcpServerEnabled, tcpExpectedText, tcpSendText));
    }

    public static void MaterializeTestFixtures(string rootDirectory, IReadOnlyList<TestFileFixture> fixtures)
    {
        Directory.CreateDirectory(rootDirectory);

        foreach (var fixture in fixtures)
        {
            var destination = GetFixtureDestinationPath(rootDirectory, fixture.RelativePath);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(destination, fixture.Content);
        }
    }

    private static TestFileFixture ParseFileFixture(string directiveBody, bool parseBytes)
    {
        var separatorIndex = directiveBody.IndexOf('=');
        if (separatorIndex < 0)
        {
            throw new InvalidOperationException($"Invalid test fixture directive '{directiveBody.Trim()}'. Expected '<path> = <content>'.");
        }

        var rawPath = directiveBody[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidOperationException("Test fixture path cannot be empty.");
        }

        var rawContent = directiveBody[(separatorIndex + 1)..];
        if (rawContent.StartsWith(' '))
        {
            rawContent = rawContent[1..];
        }

        var content = parseBytes
            ? ParseFixtureBytes(rawContent)
            : System.Text.Encoding.UTF8.GetBytes(rawContent);

        return new TestFileFixture(rawPath, content);
    }

    private static byte[] ParseFixtureBytes(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return [];
        }

        var parts = rawContent
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException($"Invalid hex byte '{parts[i]}' in test fixture directive.");
            }

            bytes[i] = value;
        }

        return bytes;
    }

    private static string GetFixtureDestinationPath(string rootDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Test fixture path '{relativePath}' must be relative.");
        }

        var candidate = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        var normalizedRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Test fixture path '{relativePath}' escapes the test working directory.");
        }

        return candidate;
    }

    private static string DecodeTestInput(string escaped)
    {
        var builder = new System.Text.StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            var ch = escaped[i];
            if (ch != '\\' || i == escaped.Length - 1)
            {
                builder.Append(ch);
                continue;
            }

            i++;
            builder.Append(escaped[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                _ => escaped[i]
            });
        }

        return builder.ToString();
    }

    private static readonly Regex ImportPattern = new(
        ProjectSupport.ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool HasImports(string filePath)
    {
        return File.ReadLines(filePath).Any(line => ImportPattern.IsMatch(line));
    }

    private static bool HasImports(string source, bool isSourceText)
    {
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (ImportPattern.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] CompileFileToImage(string filePath, string targetId, string? sourceOverride = null)
    {
        var source = sourceOverride ?? File.ReadAllText(filePath);

        if (HasImports(source, isSourceText: true))
        {
            if (sourceOverride is null)
            {
                var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath))
                    ?? throw new InvalidOperationException($"Cannot determine directory for: {filePath}");
                var project = new AshesProject(
                    ProjectFilePath: filePath,
                    ProjectDirectory: fileDir,
                    EntryPath: filePath,
                    EntryModuleName: Path.GetFileNameWithoutExtension(filePath),
                    Name: null,
                    SourceRoots: [fileDir],
                    Include: [],
                    OutDir: Path.GetTempPath(),
                    Target: targetId
                );
                var plan = ProjectSupport.BuildCompilationPlan(project);
                var compilationSource = ProjectSupport.BuildCompilationSource(plan);
                return CompileToImage(compilationSource, targetId, plan.ImportedStdModules);
            }

            var parsed = ProjectSupport.ParseImportHeader(source, filePath);
            var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
            var importedStdModules = parsed.ImportNames
                .Where(ProjectSupport.IsStdModule)
                .ToHashSet(StringComparer.Ordinal);
            return CompileToImage(layout.Source, targetId, importedStdModules);
        }

        return CompileToImage(source, targetId);
    }

    private static byte[] CompileToImage(string source, string targetId, IReadOnlySet<string>? importedStdModules = null)
    {
        var diag = new Diagnostics();
        var program = new Parser(StripLeadingCommentLines(source), diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules).Lower(program);
        diag.ThrowIfAny();

        var backend = BackendFactory.Create(targetId);
        return backend.Compile(ir);
    }

    private static string StripLeadingCommentLines(string source)
    {
        using var reader = new StringReader(source);
        var lines = new List<string>();
        var skipping = true;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (skipping)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                skipping = false;
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunImageCapture(byte[] image, string targetId, string? stdin = null, IReadOnlyList<TestFileFixture>? fileFixtures = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tempRoot);

        var workDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            if (fileFixtures is not null && fileFixtures.Count > 0)
            {
                MaterializeTestFixtures(workDir, fileFixtures);
            }

            var exeName = targetId == TargetIds.WindowsX64 ? "program.exe" : "program";
            var exePath = Path.Combine(workDir, exeName);
            File.WriteAllBytes(exePath, image);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(exePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch
                {
                }
            }

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir
            };

            using var p = Process.Start(psi)!;
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout, stderr);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void RenderResults(List<TestResult> results, IAnsiConsole console)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey);
        table.AddColumn("[grey]Test[/]");
        table.AddColumn("[grey]Result[/]");
        table.AddColumn("[grey]Time[/]");

        int pass = 0, fail = 0, skip = 0;

        foreach (var r in results)
        {
            var name = Path.GetFileName(r.Path);
            if (!r.HasExpected)
            {
                skip++;
                table.AddRow(name, "[grey]SKIP (no // expect:)[/]", "[grey]—[/]");
                continue;
            }

            var time = FormatElapsed(r.ElapsedMs);
            if (r.Passed)
            {
                pass++;
                table.AddRow(name, "[green]PASS[/]", $"[grey]{time}[/]");
            }
            else
            {
                fail++;
                table.AddRow(name, "[red]FAIL[/]", $"[grey]{time}[/]");
            }
        }

        console.Write(table);
        console.WriteLine();

        var totalMs = results.Sum(r => r.ElapsedMs);
        console.MarkupLine($"[green]{pass} passed[/], [red]{fail} failed[/], [grey]{skip} skipped[/] in [bold]{FormatElapsed(totalMs)}[/]");

        if (fail > 0)
        {
            console.WriteLine();
            foreach (var r in results.Where(x => x.HasExpected && !x.Passed))
            {
                console.Write(new Rule(Path.GetFileName(r.Path)).RuleStyle("red").LeftJustified());
                console.MarkupLine($"[grey]Expected exit:[/] {r.ExpectedExitCode}");
                console.MarkupLine($"[grey]Actual exit:[/] {r.ExitCode}");
                console.MarkupLine("[grey]Expected:[/]");
                console.WriteLine(string.IsNullOrEmpty(r.Expected) ? "(empty)" : r.Expected);
                console.MarkupLine("[grey]Actual:[/]");
                console.WriteLine(string.IsNullOrEmpty(r.Actual) ? "(empty)" : r.Actual);
                console.WriteLine();
            }
        }
    }

    public static string FormatElapsed(long ms)
    {
        if (ms < 1000)
        {
            return $"{ms}ms";
        }

        var seconds = ms / 1000.0;
        return seconds < 60
            ? seconds.ToString("F2", CultureInfo.InvariantCulture) + "s"
            : (seconds / 60.0).ToString("F2", CultureInfo.InvariantCulture) + "min";
    }

    private sealed class TcpServerInstance : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<string?> _serverTask;

        private TcpServerInstance(TcpListener listener, Task<string?> serverTask, int port)
        {
            _listener = listener;
            _serverTask = serverTask;
            Port = port;
        }

        public int Port { get; }

        public static TcpServerInstance Start(TcpServerFixture fixture)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    using var stream = client.GetStream();

                    if (fixture.ExpectedText is not null)
                    {
                        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(fixture.ExpectedText);
                        var receivedBytes = new byte[expectedBytes.Length];
                        var read = 0;
                        while (read < expectedBytes.Length)
                        {
                            var n = await stream.ReadAsync(receivedBytes.AsMemory(read, expectedBytes.Length - read), cts.Token);
                            if (n == 0)
                            {
                                return $"tcp fixture expected '{fixture.ExpectedText}' but connection closed early";
                            }

                            read += n;
                        }

                        if (!receivedBytes.AsSpan().SequenceEqual(expectedBytes))
                        {
                            return $"tcp fixture expected '{fixture.ExpectedText}' but received '{System.Text.Encoding.UTF8.GetString(receivedBytes)}'";
                        }
                    }

                    if (fixture.SendText is not null)
                    {
                        var sendBytes = System.Text.Encoding.UTF8.GetBytes(fixture.SendText);
                        await stream.WriteAsync(sendBytes, cts.Token);
                        await stream.FlushAsync(cts.Token);
                    }

                    return null;
                }
                catch (OperationCanceledException)
                {
                    return "tcp fixture timed out waiting for client connection or data";
                }
                catch (Exception ex)
                {
                    return $"tcp fixture failed: {ex.Message}";
                }
                finally
                {
                    listener.Stop();
                }
            });

            return new TcpServerInstance(listener, serverTask, port);
        }

        public string? Complete()
        {
            return _serverTask.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
            }
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
        }

        return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }
}
