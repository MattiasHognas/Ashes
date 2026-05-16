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

public static class Runner
{
    private const string TcpPortPlaceholder = "__TCP_PORT__";
    private const string TlsPortPlaceholder = "__TLS_PORT__";
    internal static TimeSpan TcpFixtureAcceptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public sealed record TestFileFixture(string RelativePath, byte[] Content);

    public enum TlsFixtureTrustMode
    {
        Trusted,
        Untrusted,
    }

    public enum TlsFixtureHandshakeMode
    {
        Success,
        Failure,
    }

    public sealed record TcpServerFixture(
        bool Enabled,
        string? ExpectedText,
        string? SendText);

    public sealed record TlsServerFixture(
        bool Enabled,
        string? ExpectedText,
        string? SendText,
        TlsFixtureTrustMode TrustMode,
        TlsFixtureHandshakeMode HandshakeMode,
        string CertificateHost);

    public sealed record TestDirectives(
        string Expected,
        bool HasExpected,
        int ExpectedExitCode,
        bool IsCompileError,
        string? Stdin,
        IReadOnlyList<TestFileFixture> FileFixtures,
        TcpServerFixture TcpServer,
        TlsServerFixture TlsServer);

    public sealed record TestResult(string Path, bool Passed, string Expected, string Actual, int ExitCode, int ExpectedExitCode, bool HasExpected = true, long ElapsedMs = 0);

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
            var rawSource = File.ReadAllText(file);
            var effectiveProject = ResolveProjectForTestFile(file, project);
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
                var (runExit, stdout, runStderr) = RunImageCapture(image, targetId, stdin, directives.FileFixtures, environmentVariables);
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
                loopbackServer?.Dispose();
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
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch
        {
            return false;
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
        var tlsServerEnabled = false;
        string? tlsExpectedText = null;
        string? tlsSendText = null;
        var tlsTrustMode = TlsFixtureTrustMode.Trusted;
        var tlsHandshakeMode = TlsFixtureHandshakeMode.Success;
        var tlsCertificateHost = "localhost";

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
                continue;
            }

            if (commentText.StartsWith("tls-server:", StringComparison.OrdinalIgnoreCase))
            {
                var mode = commentText.Substring("tls-server:".Length).Trim();
                if (!string.Equals(mode, "accept", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unsupported tls-server mode '{mode}'. Expected 'accept'.");
                }

                tlsServerEnabled = true;
                continue;
            }

            if (commentText.StartsWith("tls-expect:", StringComparison.OrdinalIgnoreCase))
            {
                tlsServerEnabled = true;
                tlsExpectedText = DecodeTestInput(commentText.Substring("tls-expect:".Length).Trim());
                continue;
            }

            if (commentText.StartsWith("tls-send:", StringComparison.OrdinalIgnoreCase))
            {
                tlsServerEnabled = true;
                tlsSendText = DecodeTestInput(commentText.Substring("tls-send:".Length).Trim());
                continue;
            }

            if (commentText.StartsWith("tls-trust:", StringComparison.OrdinalIgnoreCase))
            {
                tlsServerEnabled = true;
                tlsTrustMode = ParseTlsFixtureTrustMode(commentText.Substring("tls-trust:".Length).Trim());
                continue;
            }

            if (commentText.StartsWith("tls-handshake:", StringComparison.OrdinalIgnoreCase))
            {
                tlsServerEnabled = true;
                tlsHandshakeMode = ParseTlsFixtureHandshakeMode(commentText.Substring("tls-handshake:".Length).Trim());
                continue;
            }

            if (commentText.StartsWith("tls-cert-host:", StringComparison.OrdinalIgnoreCase))
            {
                tlsServerEnabled = true;
                tlsCertificateHost = commentText.Substring("tls-cert-host:".Length).Trim();
                if (string.IsNullOrWhiteSpace(tlsCertificateHost))
                {
                    throw new InvalidOperationException("TLS fixture certificate host cannot be empty.");
                }
            }
        }

        if (tcpServerEnabled && tlsServerEnabled)
        {
            throw new InvalidOperationException("A test cannot declare both tcp-server and tls-server fixtures.");
        }

        return new TestDirectives(
            expected,
            hasExpected,
            expectedExitCode,
            isCompileError,
            stdin,
            fileFixtures,
            new TcpServerFixture(tcpServerEnabled, tcpExpectedText, tcpSendText),
            new TlsServerFixture(tlsServerEnabled, tlsExpectedText, tlsSendText, tlsTrustMode, tlsHandshakeMode, tlsCertificateHost));
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
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
                var compilationSource = ProjectSupport.BuildCompilationSource(plan);
                return CompileToImage(compilationSource, targetId, backendOptions, plan.ImportedStdModules, plan.MergedAliases.Count == 0 ? null : plan.MergedAliases);
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
            return CompileToImage(layout.Source, targetId, backendOptions, importedStdModules.Count == 0 ? null : importedStdModules, mergedAliases.Count == 0 ? null : mergedAliases);
        }

        if (HasImports(source))
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
                var compilationSource = ProjectSupport.BuildCompilationSource(plan);
                return CompileToImage(compilationSource, targetId, backendOptions, plan.ImportedStdModules, plan.MergedAliases.Count == 0 ? null : plan.MergedAliases);
            }

            var parsed = ProjectSupport.ParseImportHeader(source, filePath);
            var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
            var importedStdModules = parsed.ImportNames
                .Where(ProjectSupport.IsStdModule)
                .ToHashSet(StringComparer.Ordinal);
            return CompileToImage(layout.Source, targetId, backendOptions, importedStdModules, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases);
        }

        return CompileToImage(source, targetId, backendOptions);
    }

    private static byte[] CompileToImage(string source, string targetId, BackendCompileOptions backendOptions, IReadOnlySet<string>? importedStdModules = null, IReadOnlyDictionary<string, string>? moduleAliases = null)
    {
        var diag = new Diagnostics();
        var program = new Parser(StripLeadingCommentLines(source), diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag, importedStdModules, moduleAliases).Lower(program);
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

            var exeName = targetId == TargetIds.WindowsX64 ? "program.exe" : "program";
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

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir
            };

            if (environmentVariables is not null)
            {
                foreach (var (key, value) in environmentVariables)
                {
                    psi.Environment[key] = value;
                }
            }

            using var p = StartProcessWithRetry(psi);
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            Task.WaitAll(stdoutTask, stderrTask);
            p.WaitForExit();
            return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
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

    private abstract class LoopbackServerInstance : IDisposable
    {
        public abstract int Port { get; }

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
        private readonly Task<string?> _serverTask;

        private TcpServerInstance(TcpListener listener, Task<string?> serverTask, int port)
        {
            _listener = listener;
            _serverTask = serverTask;
            Port = port;
        }

        public override int Port { get; }

        public static TcpServerInstance Start(TcpServerFixture fixture)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var acceptCts = new CancellationTokenSource(TcpFixtureAcceptTimeout);
                    using var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using var stream = client.GetStream();

                    if (fixture.ExpectedText is not null)
                    {
                        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(fixture.ExpectedText);
                        var receivedBytes = new byte[expectedBytes.Length];
                        var read = 0;
                        while (read < expectedBytes.Length)
                        {
                            var n = await stream.ReadAsync(receivedBytes.AsMemory(read, expectedBytes.Length - read));
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
                        await stream.WriteAsync(sendBytes);
                        await stream.FlushAsync();
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
                    listener.Stop();
                }
            });

            return new TcpServerInstance(listener, serverTask, port);
        }

        public override string? Complete()
        {
            return _serverTask.GetAwaiter().GetResult();
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
        private readonly Task<string?> _serverTask;
        private readonly TlsFixtureHost _host;
        private readonly TlsFixtureTrustMode _trustMode;
        private readonly X509Certificate2? _trustedCertificate;

        private TlsServerInstance(
            TcpListener listener,
            Task<string?> serverTask,
            int port,
            TlsFixtureHost host,
            TlsFixtureTrustMode trustMode,
            X509Certificate2? trustedCertificate)
        {
            _listener = listener;
            _serverTask = serverTask;
            _host = host;
            _trustMode = trustMode;
            _trustedCertificate = trustedCertificate;
            Port = port;
        }

        public override int Port { get; }

        public static TlsServerInstance Start(TlsServerFixture fixture)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var host = TlsFixtureHost.Create(fixture.CertificateHost);
            X509Certificate2? trustedCertificate = null;
            if (fixture.TrustMode == TlsFixtureTrustMode.Trusted && OperatingSystem.IsWindows())
            {
                trustedCertificate = X509CertificateLoader.LoadCertificate(host.ServerCertificate.Export(X509ContentType.Cert));
                using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                rootStore.Open(OpenFlags.ReadWrite);
                rootStore.Add(trustedCertificate);
            }

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var acceptCts = new CancellationTokenSource(TcpFixtureAcceptTimeout);
                    using var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    try
                    {
                        await stream.AuthenticateAsServerAsync(host.ServerCertificate, clientCertificateRequired: false, enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);
                    }
                    catch (AuthenticationException) when (fixture.HandshakeMode == TlsFixtureHandshakeMode.Failure)
                    {
                        return null;
                    }
                    catch (IOException) when (fixture.HandshakeMode == TlsFixtureHandshakeMode.Failure)
                    {
                        return null;
                    }

                    if (fixture.HandshakeMode == TlsFixtureHandshakeMode.Failure)
                    {
                        return "tls fixture expected the client handshake to fail, but it succeeded";
                    }

                    if (fixture.ExpectedText is not null)
                    {
                        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(fixture.ExpectedText);
                        var receivedBytes = new byte[expectedBytes.Length];
                        var read = 0;
                        while (read < expectedBytes.Length)
                        {
                            var n = await stream.ReadAsync(receivedBytes.AsMemory(read, expectedBytes.Length - read));
                            if (n == 0)
                            {
                                return $"tls fixture expected '{fixture.ExpectedText}' but connection closed early";
                            }

                            read += n;
                        }

                        if (!receivedBytes.AsSpan().SequenceEqual(expectedBytes))
                        {
                            return $"tls fixture expected '{fixture.ExpectedText}' but received '{System.Text.Encoding.UTF8.GetString(receivedBytes)}'";
                        }
                    }

                    if (fixture.SendText is not null)
                    {
                        var sendBytes = System.Text.Encoding.UTF8.GetBytes(fixture.SendText);
                        await stream.WriteAsync(sendBytes);
                        await stream.FlushAsync();
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
                    listener.Stop();
                }
            });

            return new TlsServerInstance(listener, serverTask, port, host, fixture.TrustMode, trustedCertificate);
        }

        public override IReadOnlyDictionary<string, string>? GetEnvironmentVariables()
        {
            if (_trustMode != TlsFixtureTrustMode.Trusted || OperatingSystem.IsWindows())
            {
                return null;
            }

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SSL_CERT_FILE"] = _host.TrustCertificatePath,
            };
        }

        public override string? Complete()
        {
            return _serverTask.GetAwaiter().GetResult();
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

            if (_trustedCertificate is not null)
            {
                try
                {
                    using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    rootStore.Open(OpenFlags.ReadWrite);
                    rootStore.Remove(_trustedCertificate);
                }
                catch
                {
                }

                _trustedCertificate.Dispose();
            }

            _host.Dispose();
        }
    }

    private sealed class TlsFixtureHost : IDisposable
    {
        private readonly string _tempDirectory;

        private TlsFixtureHost(string tempDirectory, X509Certificate2 serverCertificate, string trustCertificatePath)
        {
            _tempDirectory = tempDirectory;
            ServerCertificate = serverCertificate;
            TrustCertificatePath = trustCertificatePath;
        }

        public X509Certificate2 ServerCertificate { get; }

        public string TrustCertificatePath { get; }

        public static TlsFixtureHost Create(string hostName)
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={hostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(hostName, out var ipAddress))
            {
                subjectAlternativeNames.AddIpAddress(ipAddress);
            }
            else
            {
                subjectAlternativeNames.AddDnsName(hostName);
            }

            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            byte[] certificatePfx = certificate.Export(X509ContentType.Pfx);
            var serverCertificate = X509CertificateLoader.LoadPkcs12(certificatePfx, string.Empty, X509KeyStorageFlags.Exportable, Pkcs12LoaderLimits.Defaults);

            var tempDirectory = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var trustCertificatePath = Path.Combine(tempDirectory, "tls-fixture-cert.pem");
            File.WriteAllText(trustCertificatePath, certificate.ExportCertificatePem());

            return new TlsFixtureHost(tempDirectory, serverCertificate, trustCertificatePath);
        }

        public void Dispose()
        {
            ServerCertificate.Dispose();
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
