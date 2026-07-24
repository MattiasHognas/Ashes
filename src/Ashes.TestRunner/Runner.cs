using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Spectre.Console;

namespace Ashes.TestRunner;

/// <summary>
/// Executes end-to-end <c>.ash</c> tests: it discovers test files, parses their leading directive
/// block, compiles each program to a native executable, runs it (feeding stdin, materializing file
/// fixtures, and hosting TCP/TLS loopback fixtures as directed), and compares stdout and exit code
/// against the expectations. Pass/fail results are rendered to the console.
/// </summary>
public static class Runner
{
    private const string TcpPortPlaceholder = "__TCP_PORT__";
    private const string TlsPortPlaceholder = "__TLS_PORT__";

    // These timeouts are intentionally per-async-context (AsyncLocal) rather than
    // process-wide statics. The TUnit suite runs tests in parallel, and tests that
    // exercise the timeout paths (e.g. RunTests_times_out_test_process_when_program_hangs,
    // RunTests_times_out_tcp_fixture_when_program_never_connects) need to lower these
    // values without leaking the override into concurrently-running fixture tests
    // (which would otherwise be killed mid-handshake). Using AsyncLocal keeps the
    // override scoped to the test's execution context.
    private static readonly AsyncLocal<TimeSpan?> _tcpFixtureAcceptTimeout = new();
    private static readonly AsyncLocal<TimeSpan?> _testProcessTimeout = new();

    internal static TimeSpan TcpFixtureAcceptTimeout
    {
        get => _tcpFixtureAcceptTimeout.Value ?? TimeSpan.FromSeconds(5);
        set => _tcpFixtureAcceptTimeout.Value = value;
    }

    internal static TimeSpan TestProcessTimeout
    {
        get => _testProcessTimeout.Value ?? TimeSpan.FromSeconds(30);
        set => _testProcessTimeout.Value = value;
    }

    /// <summary>A file the test program expects to exist on disk, declared by a <c>// file:</c> or
    /// <c>// file-bytes:</c> directive and materialized before the program runs.</summary>
    /// <param name="RelativePath">Path of the fixture file relative to the program's working directory.</param>
    /// <param name="Content">The raw bytes written to that path.</param>
    public sealed record TestFileFixture(string RelativePath, byte[] Content);

    /// <summary>Whether a TLS fixture presents a certificate the client is configured to trust.</summary>
    public enum TlsFixtureTrustMode
    {
        /// <summary>The fixture's certificate is trusted by the client (the handshake can succeed).</summary>
        Trusted,
        /// <summary>The fixture's certificate is not trusted, exercising the rejection path.</summary>
        Untrusted,
    }

    /// <summary>Whether a TLS fixture completes its handshake or deliberately fails it.</summary>
    public enum TlsFixtureHandshakeMode
    {
        /// <summary>The fixture completes the TLS handshake normally.</summary>
        Success,
        /// <summary>The fixture aborts the handshake to exercise the client's failure path.</summary>
        Failure,
    }

    /// <summary>A loopback TCP server fixture the test program connects to, driven by the
    /// <c>// tcp-server</c>, <c>// tcp-expect</c>, and <c>// tcp-send</c> directives.</summary>
    /// <param name="Enabled">Whether a TCP fixture is active for this test.</param>
    /// <param name="ExpectedText">Text the fixture asserts it receives from the program, or null to skip the check.</param>
    /// <param name="SendText">Text the fixture sends to the program, or null to send nothing.</param>
    public sealed record TcpServerFixture(
        bool Enabled,
        string? ExpectedText,
        string? SendText);

    /// <summary>A loopback TLS server fixture the test program connects to, extending
    /// <see cref="TcpServerFixture"/> with certificate trust and handshake behavior.</summary>
    /// <param name="Enabled">Whether a TLS fixture is active for this test.</param>
    /// <param name="ExpectedText">Text the fixture asserts it receives from the program, or null to skip the check.</param>
    /// <param name="SendText">Text the fixture sends to the program, or null to send nothing.</param>
    /// <param name="TrustMode">Whether the fixture certificate is trusted by the client.</param>
    /// <param name="HandshakeMode">Whether the fixture completes or aborts the handshake.</param>
    /// <param name="CertificateHost">The host name the fixture certificate is issued for.</param>
    public sealed record TlsServerFixture(
        bool Enabled,
        string? ExpectedText,
        string? SendText,
        TlsFixtureTrustMode TrustMode,
        TlsFixtureHandshakeMode HandshakeMode,
        string CertificateHost);

    /// <summary>The parsed contents of a test file's leading <c>//</c> directive block: the expected
    /// output, exit code, and every configured stdin, file, and network fixture.</summary>
    /// <param name="Expected">The expected stdout, exactly (trailing whitespace trimmed).</param>
    /// <param name="HasExpected">Whether an <c>// expect:</c> directive was present at all.</param>
    /// <param name="ExpectedExitCode">The expected process exit code.</param>
    /// <param name="IsCompileError">True when the test expects a compile error rather than a run.</param>
    /// <param name="Stdin">Text fed to the program's stdin, or null for none.</param>
    /// <param name="FileFixtures">Files to materialize before the program runs.</param>
    /// <param name="TcpServer">The TCP loopback fixture configuration.</param>
    /// <param name="TlsServer">The TLS loopback fixture configuration.</param>
    /// <param name="SkipOnTargets">Target RIDs on which this test is skipped.</param>
    public sealed record TestDirectives(
        string Expected,
        bool HasExpected,
        int ExpectedExitCode,
        bool IsCompileError,
        string? Stdin,
        IReadOnlyList<TestFileFixture> FileFixtures,
        TcpServerFixture TcpServer,
        TlsServerFixture TlsServer,
        IReadOnlyList<string> SkipOnTargets);

    /// <summary>The outcome of running one test file: whether it passed, plus the compared values for
    /// reporting.</summary>
    /// <param name="Path">The test file path.</param>
    /// <param name="Passed">Whether the test passed.</param>
    /// <param name="Expected">The expected stdout (or compile-error substring).</param>
    /// <param name="Actual">The actual stdout (or compiler output) produced.</param>
    /// <param name="ExitCode">The actual process exit code.</param>
    /// <param name="ExpectedExitCode">The expected exit code.</param>
    /// <param name="HasExpected">Whether the test declared an expected output.</param>
    /// <param name="ElapsedMs">Wall-clock time the test took, in milliseconds.</param>
    public sealed record TestResult(string Path, bool Passed, string Expected, string Actual, int ExitCode, int ExpectedExitCode, bool HasExpected = true, long ElapsedMs = 0);

    /// <summary>
    /// Discovers and runs every <c>.ash</c> test under <paramref name="paths"/>, compiling for
    /// <paramref name="targetId"/> (defaulting to the project target or the host target), renders the
    /// per-test results to <paramref name="console"/>, and returns a process exit code that is zero
    /// only when every test passed. <paramref name="project"/> supplies project-mode discovery and
    /// <paramref name="backendOptions"/> overrides the backend compile settings.
    /// </summary>
    public static int RunTests(IEnumerable<string> paths, string? targetId, IAnsiConsole console, AshesProject? project = null, BackendCompileOptions? backendOptions = null)
    {
        targetId ??= project?.Target ?? BackendFactory.DefaultForCurrentOS();
        backendOptions ??= BackendCompileOptions.Default;

        var files = DiscoverAshFiles(paths, project)
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
            results.Add(RunSingleTestFile(file, targetId, backendOptions, project, console));
        }

        RenderResults(results, console);

        return results.Any(r => !r.Passed && r.HasExpected) ? 1 : 0;
    }

    private static TestResult RunSingleTestFile(string file, string targetId, BackendCompileOptions backendOptions, AshesProject? project, IAnsiConsole console)
    {
        var rawSource = File.ReadAllText(file);
        var effectiveProject = ResolveProjectForTestFile(file, project);
        var directives = ParseTestDirectives(rawSource);
        var expected = directives.Expected;
        var hasExpected = directives.HasExpected;
        var expectedExitCode = directives.ExpectedExitCode;
        var isCompileError = directives.IsCompileError;
        if (!hasExpected)
        {
            console.MarkupLine($"[grey]{Markup.Escape(Path.GetFileName(file))}[/] [grey]SKIP[/]");
            return new TestResult(file, Passed: true, Expected: "", Actual: "", ExitCode: 0, ExpectedExitCode: 0, HasExpected: false);
        }

        if (directives.SkipOnTargets.Any(t => string.Equals(t, targetId, StringComparison.OrdinalIgnoreCase)))
        {
            console.MarkupLine($"[grey]{Markup.Escape(Path.GetFileName(file))}[/] [grey]SKIP ({Markup.Escape(targetId)})[/]");
            return new TestResult(file, Passed: true, Expected: "", Actual: "", ExitCode: 0, ExpectedExitCode: 0, HasExpected: false);
        }

        var sw = Stopwatch.StartNew();
        var (exit, actual, stderr) = ExecuteTestRun(file, targetId, backendOptions, effectiveProject, directives, rawSource);
        sw.Stop();

        var exp = expected.TrimEnd();
        var passed = exit == expectedExitCode && (isCompileError
            ? actual.Contains(exp, StringComparison.Ordinal)
            : string.Equals(actual, exp, StringComparison.Ordinal));

        // If stderr present, append for diagnostics in 'Actual' when the test fails
        if (!string.IsNullOrWhiteSpace(stderr) && !passed)
        {
            actual = actual + "\n[stderr]\n" + stderr.TrimEnd();
        }

        console.MarkupLine($"[grey]{Markup.Escape(Path.GetFileName(file))}[/] {(passed ? "[green]PASS[/]" : "[red]FAIL[/]")} [grey]{FormatElapsed(sw.ElapsedMilliseconds)}[/]");
        return new TestResult(file, passed, exp, actual, exit, expectedExitCode, ElapsedMs: sw.ElapsedMilliseconds);
    }

    private static (int Exit, string Actual, string Stderr) ExecuteTestRun(string file, string targetId, BackendCompileOptions backendOptions, AshesProject? effectiveProject, TestDirectives directives, string rawSource)
    {
        int exit;
        string actual;
        string stderr = "";
        LoopbackServerInstance? loopbackServer = null;
        try
        {
            string? sourceOverride = null;
            IReadOnlyDictionary<string, string>? environmentVariables = null;
            if (directives.TcpServer.Enabled)
            {
                loopbackServer = TcpServerInstance.Start(directives.TcpServer);
                sourceOverride = ReplacePortPlaceholders(rawSource, loopbackServer.Port);
            }
            else if (directives.TlsServer.Enabled)
            {
                loopbackServer = TlsServerInstance.Start(directives.TlsServer);
                sourceOverride = ReplacePortPlaceholders(rawSource, loopbackServer.Port);
                environmentVariables = loopbackServer.GetEnvironmentVariables();
            }

            var image = CompileFileToImage(file, targetId, backendOptions, effectiveProject, sourceOverride);
            loopbackServer?.StartAccepting();
            var (runExit, stdout, runStderr) = RunImageCapture(image, targetId, directives.Stdin, directives.FileFixtures, environmentVariables);
            exit = runExit;
            actual = (stdout ?? "").TrimEnd();
            stderr = runStderr ?? "";
            if (loopbackServer is not null)
            {
                var fixtureError = loopbackServer.Complete();
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
            var isUnexpectedFailure = directives.ExpectedExitCode != 1;
            actual = (isUnexpectedFailure || directives.IsCompileError)
                ? DiagnosticTextRenderer.RenderCompilerDiagnostics(ex, source: null, displayPath: file).TrimEnd()
                : "";
        }
        catch (InvalidOperationException ex)
        {
            exit = 1;
            var isUnexpectedFailure = directives.ExpectedExitCode != 1;
            actual = (isUnexpectedFailure || directives.IsCompileError)
                ? DiagnosticTextRenderer.RenderFailure("compile error", ex.Message ?? string.Empty, file).TrimEnd()
                : "";
        }
        finally
        {
            loopbackServer?.Dispose();
        }

        return (exit, actual, stderr);
    }

    private static AshesProject? ResolveProjectForTestFile(string filePath, AshesProject? project)
    {
        if (project is not null)
        {
            return project;
        }

        var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrWhiteSpace(fileDirectory))
        {
            return null;
        }

        var projectFile = ProjectSupport.DiscoverProjectFile(fileDirectory);
        return string.IsNullOrWhiteSpace(projectFile)
            ? null
            : ProjectSupport.LoadProject(projectFile);
    }

    private static IEnumerable<string> DiscoverAshFiles(IEnumerable<string> paths, AshesProject? project)
    {
        var effectivePaths = paths.Any()
            ? paths.ToList()
            : GetDefaultDiscoveryPaths(project);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in effectivePaths)
        {
            var p = ResolveDiscoveryPath(rawPath, project);
            if (File.Exists(p) && p.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(p);
                if (seen.Add(full))
                {
                    yield return full;
                }
                continue;
            }

            if (Directory.Exists(p))
            {
                foreach (var f in EnumerateAshFilesRecursively(p))
                {
                    var full = Path.GetFullPath(f);
                    if (seen.Add(full))
                    {
                        yield return full;
                    }
                }
            }
        }
    }

    private static List<string> GetDefaultDiscoveryPaths(AshesProject? project)
    {
        if (project is null)
        {
            return ["tests"];
        }

        var projectTestsDirectory = Path.Combine(project.ProjectDirectory, "tests");
        if (Directory.Exists(projectTestsDirectory))
        {
            return [projectTestsDirectory];
        }

        return [project.EntryPath];
    }

    private static string ResolveDiscoveryPath(string rawPath, AshesProject? project)
    {
        if (Path.IsPathRooted(rawPath) || project is null)
        {
            return rawPath;
        }

        if (File.Exists(rawPath) || Directory.Exists(rawPath))
        {
            return rawPath;
        }

        return Path.Combine(project.ProjectDirectory, rawPath);
    }

    private static IEnumerable<string> EnumerateAshFilesRecursively(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var dir in Directory.EnumerateDirectories(current)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldSkipDirectory(dir))
                {
                    continue;
                }

                pending.Push(dir);
            }

            foreach (var file in Directory.EnumerateFiles(current, "*.ash", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static bool ShouldSkipDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(".", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != FileAttributes.None;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses the leading <c>//</c> directive block of a test file's <paramref name="source"/> into a
    /// <see cref="TestDirectives"/>, reading the expected output, exit code, stdin, file fixtures, and
    /// TCP/TLS loopback fixture configuration.
    /// </summary>
    public static TestDirectives ParseTestDirectives(string source)
    {
        var acc = new DirectiveAccumulator();

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

            ApplyExpectationDirective(commentText, acc);
            ApplyFixtureDirective(commentText, acc);
            ApplyTlsDirective(commentText, acc);
            ApplyCertHostAndSkipDirective(commentText, acc);
        }

        if (acc.TcpServerEnabled && acc.TlsServerEnabled)
        {
            throw new InvalidOperationException("A test cannot declare both tcp-server and tls-server fixtures.");
        }

        return new TestDirectives(
            acc.Expected,
            acc.HasExpected,
            acc.ExpectedExitCode,
            acc.IsCompileError,
            acc.Stdin,
            acc.FileFixtures,
            new TcpServerFixture(acc.TcpServerEnabled, acc.TcpExpectedText, acc.TcpSendText),
            new TlsServerFixture(acc.TlsServerEnabled, acc.TlsExpectedText, acc.TlsSendText, acc.TlsTrustMode, acc.TlsHandshakeMode, acc.TlsCertificateHost),
            acc.SkipOnTargets);
    }

    private sealed class DirectiveAccumulator
    {
        public string Expected = "";
        public bool HasExpected;
        public int ExpectedExitCode;
        public bool IsCompileError;
        public string? Stdin;
        public List<TestFileFixture> FileFixtures = new();
        public bool TcpServerEnabled;
        public string? TcpExpectedText;
        public string? TcpSendText;
        public bool TlsServerEnabled;
        public string? TlsExpectedText;
        public string? TlsSendText;
        public TlsFixtureTrustMode TlsTrustMode = TlsFixtureTrustMode.Trusted;
        public TlsFixtureHandshakeMode TlsHandshakeMode = TlsFixtureHandshakeMode.Success;
        public string TlsCertificateHost = "localhost";
        public List<string> SkipOnTargets = new();
    }

    private static void ApplyExpectationDirective(string commentText, DirectiveAccumulator acc)
    {
        if (commentText.StartsWith("expect:", StringComparison.OrdinalIgnoreCase))
        {
            acc.Expected = commentText.Substring("expect:".Length).Trim();
            acc.HasExpected = true;
            acc.IsCompileError = false;
            return;
        }

        if (commentText.StartsWith("expect-compile-error:", StringComparison.OrdinalIgnoreCase))
        {
            acc.Expected = commentText.Substring("expect-compile-error:".Length).Trim();
            acc.HasExpected = true;
            acc.ExpectedExitCode = 1;
            acc.IsCompileError = true;
            return;
        }

        if (commentText.StartsWith("exit:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(commentText.Substring("exit:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsedExitCode))
        {
            acc.ExpectedExitCode = parsedExitCode;
            return;
        }

        if (commentText.StartsWith("stdin:", StringComparison.OrdinalIgnoreCase))
        {
            acc.Stdin = DecodeTestInput(commentText.Substring("stdin:".Length).Trim());
        }
    }

    private static void ApplyFixtureDirective(string commentText, DirectiveAccumulator acc)
    {
        if (commentText.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            acc.FileFixtures.Add(ParseFileFixture(commentText.Substring("file:".Length), parseBytes: false));
            return;
        }

        if (commentText.StartsWith("file-bytes:", StringComparison.OrdinalIgnoreCase))
        {
            acc.FileFixtures.Add(ParseFileFixture(commentText.Substring("file-bytes:".Length), parseBytes: true));
            return;
        }

        if (commentText.StartsWith("tcp-server:", StringComparison.OrdinalIgnoreCase))
        {
            var mode = commentText.Substring("tcp-server:".Length).Trim();
            if (!string.Equals(mode, "accept", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported tcp-server mode '{mode}'. Expected 'accept'.");
            }

            acc.TcpServerEnabled = true;
            return;
        }

        if (commentText.StartsWith("tcp-expect:", StringComparison.OrdinalIgnoreCase))
        {
            acc.TcpServerEnabled = true;
            acc.TcpExpectedText = DecodeTestInput(commentText.Substring("tcp-expect:".Length).Trim());
            return;
        }

        if (commentText.StartsWith("tcp-send:", StringComparison.OrdinalIgnoreCase))
        {
            acc.TcpServerEnabled = true;
            acc.TcpSendText = DecodeTestInput(commentText.Substring("tcp-send:".Length).Trim());
        }
    }

    private static void ApplyTlsDirective(string commentText, DirectiveAccumulator acc)
    {
        if (commentText.StartsWith("tls-server:", StringComparison.OrdinalIgnoreCase))
        {
            var mode = commentText.Substring("tls-server:".Length).Trim();
            if (!string.Equals(mode, "accept", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported tls-server mode '{mode}'. Expected 'accept'.");
            }

            acc.TlsServerEnabled = true;
            return;
        }

        if (commentText.StartsWith("tls-expect:", StringComparison.OrdinalIgnoreCase))
        {
            acc.TlsServerEnabled = true;
            acc.TlsExpectedText = DecodeTestInput(commentText.Substring("tls-expect:".Length).Trim());
            return;
        }

        if (commentText.StartsWith("tls-send:", StringComparison.OrdinalIgnoreCase))
        {
            acc.TlsServerEnabled = true;
            acc.TlsSendText = DecodeTestInput(commentText.Substring("tls-send:".Length).Trim());
            return;
        }

        if (commentText.StartsWith("tls-trust:", StringComparison.OrdinalIgnoreCase))
        {
            acc.TlsServerEnabled = true;
            acc.TlsTrustMode = ParseTlsFixtureTrustMode(commentText.Substring("tls-trust:".Length).Trim());
            return;
        }

        if (commentText.StartsWith("tls-handshake:", StringComparison.OrdinalIgnoreCase))
        {
            acc.TlsServerEnabled = true;
            acc.TlsHandshakeMode = ParseTlsFixtureHandshakeMode(commentText.Substring("tls-handshake:".Length).Trim());
        }
    }

    private static void ApplyCertHostAndSkipDirective(string commentText, DirectiveAccumulator acc)
    {
        if (commentText.StartsWith("tls-cert-host:", StringComparison.OrdinalIgnoreCase))
        {
            acc.TlsServerEnabled = true;
            acc.TlsCertificateHost = commentText.Substring("tls-cert-host:".Length).Trim();
            if (string.IsNullOrWhiteSpace(acc.TlsCertificateHost))
            {
                throw new InvalidOperationException("TLS fixture certificate host cannot be empty.");
            }
        }

        if (commentText.StartsWith("skip-on:", StringComparison.OrdinalIgnoreCase))
        {
            var list = commentText.Substring("skip-on:".Length);
            foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.Length > 0)
                {
                    acc.SkipOnTargets.Add(raw);
                }
            }
        }
    }

    private static TlsFixtureTrustMode ParseTlsFixtureTrustMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "trusted" => TlsFixtureTrustMode.Trusted,
            "untrusted" => TlsFixtureTrustMode.Untrusted,
            _ => throw new InvalidOperationException($"Unsupported tls-trust mode '{value}'. Expected 'trusted' or 'untrusted'."),
        };
    }

    private static TlsFixtureHandshakeMode ParseTlsFixtureHandshakeMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "success" => TlsFixtureHandshakeMode.Success,
            "failure" => TlsFixtureHandshakeMode.Failure,
            _ => throw new InvalidOperationException($"Unsupported tls-handshake mode '{value}'. Expected 'success' or 'failure'."),
        };
    }

    /// <summary>
    /// Writes each fixture in <paramref name="fixtures"/> to disk under <paramref name="rootDirectory"/>,
    /// creating any intermediate directories, so the test program finds the files it expects when it
    /// runs.
    /// </summary>
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
        var normalizedRelativePath = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new InvalidOperationException($"Test fixture path '{relativePath}' must be relative.");
        }

        var candidate = Path.GetFullPath(Path.Combine(rootDirectory, normalizedRelativePath));
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

    private static string ReplacePortPlaceholders(string source, int port)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        return source
            .Replace(TcpPortPlaceholder, portText, StringComparison.Ordinal)
            .Replace(TlsPortPlaceholder, portText, StringComparison.Ordinal);
    }

    private static readonly Regex ImportPattern = new(
        ProjectSupport.ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static bool HasImports(string source)
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

    private static byte[] CompileFileToImage(string filePath, string targetId, BackendCompileOptions backendOptions, AshesProject? project = null, string? sourceOverride = null)
    {
        var source = sourceOverride ?? File.ReadAllText(filePath);

        if (project is not null)
        {
            return CompileProjectTestImage(filePath, targetId, backendOptions, project, source, sourceOverride);
        }

        if (HasImports(source))
        {
            return CompileImportsTestImage(filePath, targetId, backendOptions, source, sourceOverride);
        }

        // A file with inline `module` blocks but no imports still needs the stitching layout so the
        // blocks are lifted into submodules; the raw parser has no inline-module construct.
        if (ProjectSupport.ContainsInlineModule(source))
        {
            var parsed = ProjectSupport.ParseImportHeader(source, filePath);
            var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames, filePath, parsed.ImportSelectors);
            return CompileToImage(layout.Source, targetId, backendOptions, null, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases, layout.ConstructorModules);
        }

        return CompileToImage(source, targetId, backendOptions);
    }

    private static byte[] CompileProjectTestImage(string filePath, string targetId, BackendCompileOptions backendOptions, AshesProject project, string source, string? sourceOverride)
    {
        var testProject = new AshesProject(
            ProjectFilePath: project.ProjectFilePath,
            ProjectDirectory: project.ProjectDirectory,
            EntryPath: Path.GetFullPath(filePath),
            EntryModuleName: Path.GetFileNameWithoutExtension(filePath),
            Name: project.Name,
            SourceRoots: project.SourceRoots,
            Include: project.Include,
            OutDir: project.OutDir,
            Target: project.Target);

        var plan = ProjectSupport.BuildCompilationPlan(testProject);
        if (sourceOverride is null)
        {
            var compilationLayout = ProjectSupport.BuildCompilationLayout(plan);
            return CompileToImage(compilationLayout.Source, targetId, backendOptions, plan.ImportedStdModules, plan.MergedAliases.Count == 0 ? null : plan.MergedAliases, compilationLayout.ConstructorModules);
        }

        var parsed = ProjectSupport.ParseImportHeader(source, filePath);
        var layout = ProjectSupport.BuildCompilationLayout(plan, parsed.SourceWithoutImports);
        var importedStdModules = plan.ImportedStdModules
            .Concat(parsed.ImportNames.Where(ProjectSupport.IsStdModule))
            .ToHashSet(StringComparer.Ordinal);
        var mergedAliases = new Dictionary<string, string>(plan.MergedAliases, StringComparer.Ordinal);
        foreach (var (alias, moduleName) in parsed.ImportAliases)
        {
            if (mergedAliases.TryGetValue(alias, out var existing) && !string.Equals(existing, moduleName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Conflicting alias '{alias}': maps to '{moduleName}', but already mapped to '{existing}'.");
            }

            mergedAliases.TryAdd(alias, moduleName);
        }
        return CompileToImage(layout.Source, targetId, backendOptions, importedStdModules.Count == 0 ? null : importedStdModules, mergedAliases.Count == 0 ? null : mergedAliases, layout.ConstructorModules);
    }

    private static byte[] CompileImportsTestImage(string filePath, string targetId, BackendCompileOptions backendOptions, string source, string? sourceOverride)
    {
        if (sourceOverride is null)
        {
            var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath))
                ?? throw new InvalidOperationException($"Cannot determine directory for: {filePath}");
            var standaloneProject = new AshesProject(
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
            var plan = ProjectSupport.BuildCompilationPlan(standaloneProject);
            var compilationLayout = ProjectSupport.BuildCompilationLayout(plan);
            return CompileToImage(compilationLayout.Source, targetId, backendOptions, plan.ImportedStdModules, plan.MergedAliases.Count == 0 ? null : plan.MergedAliases, compilationLayout.ConstructorModules);
        }

        var parsed = ProjectSupport.ParseImportHeader(source, filePath);
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
        var importedStdModules = parsed.ImportNames
            .Where(ProjectSupport.IsStdModule)
            .ToHashSet(StringComparer.Ordinal);
        return CompileToImage(layout.Source, targetId, backendOptions, importedStdModules, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases, layout.ConstructorModules);
    }

    private static byte[] CompileToImage(
        string source,
        string targetId,
        BackendCompileOptions backendOptions,
        IReadOnlySet<string>? importedStdModules = null,
        IReadOnlyDictionary<string, string>? moduleAliases = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? constructorModulesByName = null)
    {
        var diag = new Diagnostics();
        var program = new Parser(StripLeadingCommentLines(source), diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules, moduleAliases, constructorModulesByName).Lower(program);
        diag.ThrowIfAny();

        var backend = BackendFactory.Create(targetId);
        return backend.Compile(ir, backendOptions);
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

    private static (int ExitCode, string Stdout, string Stderr) RunImageCapture(
        byte[] image,
        string targetId,
        string? stdin = null,
        IReadOnlyList<TestFileFixture>? fileFixtures = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
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

            var exeName = TargetIds.IsWindows(targetId) ? "program.exe" : "program";
            var exePath = Path.Combine(workDir, exeName);
            using (var fs = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(image);
                fs.Flush(flushToDisk: true);
            }

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

            var psi = BuildRunProcessStartInfo(exePath, workDir, targetId, stdin, environmentVariables);
            return RunProcessCaptureOutput(psi, stdin);
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

    private static ProcessStartInfo BuildRunProcessStartInfo(string exePath, string workDir, string targetId, string? stdin, IReadOnlyDictionary<string, string>? environmentVariables)
    {
        // UTF-8 without a BOM. The default StreamReader/Writer encoding falls
        // back to Console.OutputEncoding, which is the OEM/ANSI code page on
        // Windows (and under Wine) and mangles the non-ASCII bytes Ashes
        // programs emit as UTF-8. The BOM-less encoder is essential for stdin:
        // the shared Encoding.UTF8 emits a preamble, which would prepend a stray
        // BOM to the bytes fed to the child (corrupting readExact/readLine).
        var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
            WorkingDirectory = workDir
        };
        if (stdin is not null)
        {
            psi.StandardInputEncoding = utf8NoBom;
        }

        // Win-x64 PE images run via Wine (binfmt_misc) on Linux. Ashes binaries are standalone
        // native PE — they never load the .NET (mscoree) or Gecko (mshtml) runtimes — so suppress
        // Wine's first-run installer dialogs, which would otherwise block the run on a GUI popup.
        if (string.Equals(targetId, TargetIds.WindowsX64, StringComparison.Ordinal) && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            psi.Environment["WINEDEBUG"] = "-all";
            psi.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=d";
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                psi.Environment[key] = value;
            }
        }

        return psi;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcessCaptureOutput(ProcessStartInfo psi, string? stdin)
    {
        using var p = StartProcessWithRetry(psi);
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var timeout = TestProcessTimeout;
        var timedOut = false;
        if (timeout > TimeSpan.Zero && !p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            timedOut = true;
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            try
            {
                p.WaitForExit(5000);
            }
            catch
            {
            }
        }

        // Exited-on-its-own closes the pipes, so the read tasks complete; wait unconditionally to
        // avoid truncating output under a saturated thread pool. Bound the wait only after a kill,
        // where a leaked child may hold the pipe open and block the reads forever.
        try
        {
            if (timedOut)
            {
                Task.WaitAll(new[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(5));
            }
            else
            {
                Task.WaitAll(stdoutTask, stderrTask);
            }
        }
        catch
        {
        }
        p.WaitForExit();
        var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
        if (timedOut)
        {
            var notice = $"test process timed out after {(long)timeout.TotalMilliseconds} ms and was killed";
            stderr = string.IsNullOrEmpty(stderr) ? notice : stderr.TrimEnd() + "\n" + notice;
            return (1, stdout, stderr);
        }
        return (p.ExitCode, stdout, stderr);
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

    /// <summary>Renders a duration of <paramref name="ms"/> milliseconds as a compact human-readable
    /// string, scaling to <c>ms</c>, seconds, or minutes as appropriate.</summary>
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

    private abstract class LoopbackServerInstance : IDisposable
    {
        public abstract int Port { get; }

        public abstract void StartAccepting();

        public virtual IReadOnlyDictionary<string, string>? GetEnvironmentVariables()
        {
            return null;
        }

        public abstract string? Complete();

        public abstract void Dispose();
    }

    private sealed class TcpServerInstance : LoopbackServerInstance
    {
        private readonly TcpListener _listener;
        private readonly TcpServerFixture _fixture;
        private Task<string?>? _serverTask;

        private TcpServerInstance(TcpListener listener, TcpServerFixture fixture, int port)
        {
            _listener = listener;
            _fixture = fixture;
            Port = port;
        }

        public override int Port { get; }

        public static TcpServerInstance Start(TcpServerFixture fixture)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new TcpServerInstance(listener, fixture, port);
        }

        public override void StartAccepting()
        {
            _serverTask ??= Task.Run(async () =>
            {
                try
                {
                    using var acceptCts = new CancellationTokenSource(TcpFixtureAcceptTimeout);
                    using var client = await _listener.AcceptTcpClientAsync(acceptCts.Token).ConfigureAwait(false);
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using var stream = client.GetStream();

                    if (_fixture.ExpectedText is not null)
                    {
                        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(_fixture.ExpectedText);
                        var receivedBytes = new byte[expectedBytes.Length];
                        var read = 0;
                        while (read < expectedBytes.Length)
                        {
                            var n = await stream.ReadAsync(receivedBytes.AsMemory(read, expectedBytes.Length - read)).ConfigureAwait(false);
                            if (n == 0)
                            {
                                return $"tcp fixture expected '{_fixture.ExpectedText}' but connection closed early";
                            }

                            read += n;
                        }

                        if (!receivedBytes.AsSpan().SequenceEqual(expectedBytes))
                        {
                            return $"tcp fixture expected '{_fixture.ExpectedText}' but received '{System.Text.Encoding.UTF8.GetString(receivedBytes)}'";
                        }
                    }

                    if (_fixture.SendText is not null)
                    {
                        var sendBytes = System.Text.Encoding.UTF8.GetBytes(_fixture.SendText);
                        await stream.WriteAsync(sendBytes).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }

                    return null;
                }
                catch (OperationCanceledException)
                {
                    return $"tcp fixture timed out waiting for connection after {FormatElapsed((long)TcpFixtureAcceptTimeout.TotalMilliseconds)}";
                }
                catch (Exception ex)
                {
                    return $"tcp fixture failed: {ex.Message}";
                }
                finally
                {
                    _listener.Stop();
                }
            });
        }

        public override string? Complete()
        {
            return _serverTask?.GetAwaiter().GetResult();
        }

        public override void Dispose()
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

    private sealed class TlsServerInstance : LoopbackServerInstance
    {
        private readonly TcpListener _listener;
        private readonly TlsServerFixture _fixture;
        private readonly TlsFixtureHost _host;
        private readonly TlsFixtureTrustMode _trustMode;
        private Task<string?>? _serverTask;

        private TlsServerInstance(
            TcpListener listener,
            int port,
            TlsServerFixture fixture,
            TlsFixtureHost host,
            TlsFixtureTrustMode trustMode)
        {
            _listener = listener;
            _fixture = fixture;
            _host = host;
            _trustMode = trustMode;
            Port = port;
        }

        public override int Port { get; }

        public static TlsServerInstance Start(TlsServerFixture fixture)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var host = TlsFixtureHost.Create(fixture.CertificateHost);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new TlsServerInstance(listener, port, fixture, host, fixture.TrustMode);
        }

        public override void StartAccepting()
        {
            _serverTask ??= Task.Run(RunTlsFixtureServerAsync);
        }

        private async Task<string?> RunTlsFixtureServerAsync()
        {
            try
            {
                using var acceptCts = new CancellationTokenSource(TcpFixtureAcceptTimeout);
                using var client = await _listener.AcceptTcpClientAsync(acceptCts.Token).ConfigureAwait(false);
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    await stream.AuthenticateAsServerAsync(_host.ServerCertificate, clientCertificateRequired: false, enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false).ConfigureAwait(false);
                }
                catch (AuthenticationException) when (_fixture.HandshakeMode == TlsFixtureHandshakeMode.Failure)
                {
                    return null;
                }
                catch (IOException) when (_fixture.HandshakeMode == TlsFixtureHandshakeMode.Failure)
                {
                    return null;
                }

                if (_fixture.HandshakeMode == TlsFixtureHandshakeMode.Failure)
                {
                    return "tls fixture expected the client handshake to fail, but it succeeded";
                }

                if (_fixture.ExpectedText is not null)
                {
                    var mismatch = await ReadAndVerifyTlsExpectedTextAsync(stream).ConfigureAwait(false);
                    if (mismatch is not null)
                    {
                        return mismatch;
                    }
                }

                if (_fixture.SendText is not null)
                {
                    var sendBytes = System.Text.Encoding.UTF8.GetBytes(_fixture.SendText);
                    await stream.WriteAsync(sendBytes).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    await stream.ShutdownAsync().ConfigureAwait(false);
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return $"tls fixture timed out waiting for connection after {FormatElapsed((long)TcpFixtureAcceptTimeout.TotalMilliseconds)}";
            }
            catch (Exception ex)
            {
                return $"tls fixture failed: {ex.Message}";
            }
            finally
            {
                _listener.Stop();
            }
        }

        private async Task<string?> ReadAndVerifyTlsExpectedTextAsync(SslStream stream)
        {
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(_fixture.ExpectedText!);
            var receivedBytes = new byte[expectedBytes.Length];
            var read = 0;
            while (read < expectedBytes.Length)
            {
                var n = await stream.ReadAsync(receivedBytes.AsMemory(read, expectedBytes.Length - read)).ConfigureAwait(false);
                if (n == 0)
                {
                    return $"tls fixture expected '{_fixture.ExpectedText}' but connection closed early";
                }

                read += n;
            }

            if (!receivedBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                return $"tls fixture expected '{_fixture.ExpectedText}' but received '{System.Text.Encoding.UTF8.GetString(receivedBytes)}'";
            }

            return null;
        }

        public override IReadOnlyDictionary<string, string>? GetEnvironmentVariables()
        {
            if (_trustMode != TlsFixtureTrustMode.Trusted)
            {
                return null;
            }

            // Always route trust verification through the embedded TLS runtime's PEM roots by pointing
            // SSL_CERT_FILE at the fixture's CA PEM. This is portable across Linux, Wine,
            // and Windows hosts, and avoids touching the Windows certificate store
            // (CurrentUser\Root.Add can trigger a SmartScreen confirmation prompt that
            // hangs indefinitely on headless GitHub Actions Windows runners).
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SSL_CERT_FILE"] = _host.TrustCertificatePath,
            };
        }

        public override string? Complete()
        {
            return _serverTask?.GetAwaiter().GetResult();
        }

        public override void Dispose()
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
            }

            _host.Dispose();
        }
    }

    private sealed class TlsFixtureHost : IDisposable
    {
        private readonly string _tempDirectory;

        private TlsFixtureHost(string tempDirectory, X509Certificate2 serverCertificate, X509Certificate2 trustCertificate, string trustCertificatePath)
        {
            _tempDirectory = tempDirectory;
            ServerCertificate = serverCertificate;
            TrustCertificate = trustCertificate;
            TrustCertificatePath = trustCertificatePath;
        }

        public X509Certificate2 ServerCertificate { get; }

        public X509Certificate2 TrustCertificate { get; }

        public string TrustCertificatePath { get; }

        public static TlsFixtureHost Create(string hostName)
        {
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var rootNotAfter = notBefore.AddDays(2);
            var serverNotAfter = notBefore.AddDays(1);

            using RSA rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Ashes Test Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));

            using var rootCertificateEphemeral = rootRequest.CreateSelfSigned(notBefore, rootNotAfter);
            byte[] rootCertificatePfx = rootCertificateEphemeral.Export(X509ContentType.Pfx);
            var rootCertificate = X509CertificateLoader.LoadPkcs12(rootCertificatePfx, string.Empty, X509KeyStorageFlags.Exportable, Pkcs12LoaderLimits.Defaults);

            using RSA serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest($"CN={hostName}", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, false));
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(hostName, out var ipAddress))
            {
                subjectAlternativeNames.AddIpAddress(ipAddress);
            }
            else
            {
                subjectAlternativeNames.AddDnsName(hostName);
            }

            serverRequest.CertificateExtensions.Add(subjectAlternativeNames.Build());

            byte[] serialNumber = new byte[16];
            RandomNumberGenerator.Fill(serialNumber);
            using var serverCertificateEphemeral = serverRequest.Create(rootCertificate, notBefore, serverNotAfter, serialNumber);
            var serverCertificateWithKey = serverCertificateEphemeral.CopyWithPrivateKey(serverKey);
            byte[] serverCertificatePfx = serverCertificateWithKey.Export(X509ContentType.Pfx);
            var serverCertificate = X509CertificateLoader.LoadPkcs12(serverCertificatePfx, string.Empty, X509KeyStorageFlags.Exportable, Pkcs12LoaderLimits.Defaults);
            serverCertificateWithKey.Dispose();

            byte[] trustCertificateBytes = rootCertificate.Export(X509ContentType.Cert);
            var trustCertificate = X509CertificateLoader.LoadCertificate(trustCertificateBytes);

            var tempDirectory = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var trustCertificatePath = Path.Combine(tempDirectory, "tls-fixture-cert.pem");
            File.WriteAllText(trustCertificatePath, rootCertificate.ExportCertificatePem());

            return new TlsFixtureHost(tempDirectory, serverCertificate, trustCertificate, trustCertificatePath);
        }

        public void Dispose()
        {
            ServerCertificate.Dispose();
            TrustCertificate.Dispose();
            TryDeleteFile(TrustCertificatePath);
            TryDeleteDirectory(_tempDirectory);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Renders a byte count as a compact human-readable string, scaling to <c>B</c>, <c>KB</c>,
    /// <c>MB</c>, or larger units as appropriate.</summary>
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

    /// <summary>
    /// Starts a process, retrying on transient ETXTBSY ("Text file busy") errors.
    /// On Linux, a freshly-written executable can briefly fail to exec while the
    /// kernel page cache is still finishing writeback.
    /// </summary>
    private static Process StartProcessWithRetry(ProcessStartInfo psi)
    {
        // ETXTBSY is errno 26 on Linux.
        const int textFileBusyError = 26;
        const int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return Process.Start(psi)!;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == textFileBusyError && attempt < maxAttempts - 1)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
        }

        throw new InvalidOperationException("Failed to start process after retrying transient ETXTBSY errors.");
    }
}
