using System.Text;
using System.Text.Json;
using Ashes.Dap;
using Shouldly;

namespace Ashes.Tests;

public sealed class DapServerTests
{
    // ── Transport tests ─────────────────────────────────────────────────

    [Test]
    public async Task Transport_can_read_and_write_messages()
    {
        var inputStream = new MemoryStream();
        var outputStream = new MemoryStream();

        // Write a DAP request into the input stream
        var request = new DapRequest
        {
            Seq = 1,
            Command = "initialize",
        };
        var json = JsonSerializer.Serialize(request);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        inputStream.Write(header);
        inputStream.Write(body);
        inputStream.Position = 0;

        var transport = new DapTransport(inputStream, outputStream);
        var received = await transport.ReadRequestAsync().ConfigureAwait(false);

        received.ShouldNotBeNull();
        received.Command.ShouldBe("initialize");
        received.Seq.ShouldBe(1);
    }

    [Test]
    public void Transport_send_response_includes_content_length_header()
    {
        var inputStream = new MemoryStream();
        var outputStream = new MemoryStream();
        var transport = new DapTransport(inputStream, outputStream);

        var request = new DapRequest { Seq = 1, Command = "initialize" };
        transport.SendResponse(request, success: true, body: new DapCapabilities());

        outputStream.Position = 0;
        var allBytes = outputStream.ToArray();
        var text = Encoding.UTF8.GetString(allBytes);

        text.ShouldContain("Content-Length:");
        text.ShouldContain("\"success\":true");
        text.ShouldContain("\"command\":\"initialize\"");
    }

    [Test]
    public void Transport_send_event_produces_valid_dap_event()
    {
        var inputStream = new MemoryStream();
        var outputStream = new MemoryStream();
        var transport = new DapTransport(inputStream, outputStream);

        transport.SendEvent("initialized", new DapInitializedEventBody());

        outputStream.Position = 0;
        var text = Encoding.UTF8.GetString(outputStream.ToArray());

        text.ShouldContain("Content-Length:");
        text.ShouldContain("\"type\":\"event\"");
        text.ShouldContain("\"event\":\"initialized\"");
    }

    [Test]
    public async Task Transport_returns_null_on_empty_input()
    {
        var inputStream = new MemoryStream(); // empty
        var outputStream = new MemoryStream();
        var transport = new DapTransport(inputStream, outputStream);

        var result = await transport.ReadRequestAsync().ConfigureAwait(false);
        result.ShouldBeNull();
    }

    // ── Server integration tests ────────────────────────────────────────

    [Test]
    public async Task Server_responds_to_initialize_with_capabilities()
    {
        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "disconnect"));

        using var server = new DapServer(inputStream, outputStream);
        await server.RunAsync().ConfigureAwait(false);

        var responses = ParseDapOutput(outputStream);
        var initResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && string.Equals(cmd.GetString(), "initialize"
, StringComparison.Ordinal) && r.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "response", StringComparison.Ordinal));

        initResponse.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        initResponse.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task Server_sends_initialized_event_after_initialize()
    {
        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "disconnect"));

        using var server = new DapServer(inputStream, outputStream);
        await server.RunAsync().ConfigureAwait(false);

        var messages = ParseDapOutput(outputStream);
        var initializedEvent = messages.FirstOrDefault(m =>
            m.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "event"
, StringComparison.Ordinal) && m.TryGetProperty("event", out var evt) && string.Equals(evt.GetString(), "initialized", StringComparison.Ordinal));

        initializedEvent.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
    }

    [Test]
    public async Task Server_responds_to_threads_request()
    {
        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "threads"),
            CreateRequest(3, "disconnect"));

        using var server = new DapServer(inputStream, outputStream);
        await server.RunAsync().ConfigureAwait(false);

        var responses = ParseDapOutput(outputStream);
        var threadsResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && string.Equals(cmd.GetString(), "threads"
, StringComparison.Ordinal) && r.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "response", StringComparison.Ordinal));

        threadsResponse.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        threadsResponse.GetProperty("success").GetBoolean().ShouldBeTrue();
        threadsResponse.GetProperty("body").GetProperty("threads").GetArrayLength().ShouldBe(1);
    }

    [Test]
    public async Task Server_responds_to_unknown_command_with_failure()
    {
        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "nonExistentCommand"),
            CreateRequest(3, "disconnect"));

        using var server = new DapServer(inputStream, outputStream);
        await server.RunAsync().ConfigureAwait(false);

        var responses = ParseDapOutput(outputStream);
        var errorResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && string.Equals(cmd.GetString(), "nonExistentCommand"
, StringComparison.Ordinal) && r.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "response", StringComparison.Ordinal));

        errorResponse.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        errorResponse.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task Server_handles_setBreakpoints_before_launch()
    {
        var bpArgs = JsonSerializer.SerializeToElement(new
        {
            source = new { path = "/test/main.ash" },
            breakpoints = new[] { new { line = 5 } }
        });

        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "setBreakpoints", bpArgs),
            CreateRequest(3, "disconnect"));

        using var server = new DapServer(inputStream, outputStream);
        await server.RunAsync().ConfigureAwait(false);

        var responses = ParseDapOutput(outputStream);
        var bpResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && string.Equals(cmd.GetString(), "setBreakpoints"
, StringComparison.Ordinal) && r.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "response", StringComparison.Ordinal));

        bpResponse.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        bpResponse.GetProperty("success").GetBoolean().ShouldBeTrue();
        bpResponse.GetProperty("body").GetProperty("breakpoints").GetArrayLength().ShouldBe(1);
    }

    // ── Protocol type tests ─────────────────────────────────────────────

    [Test]
    public void DapRequest_serializes_correctly()
    {
        var request = new DapRequest { Seq = 1, Command = "initialize" };
        var json = JsonSerializer.Serialize(request);
        json.ShouldContain("\"type\":\"request\"");
        json.ShouldContain("\"command\":\"initialize\"");
    }

    [Test]
    public void DapResponse_serializes_correctly()
    {
        var response = new DapResponse
        {
            Seq = 1,
            RequestSeq = 1,
            Success = true,
            Command = "initialize",
            Body = new DapCapabilities(),
        };
        var json = JsonSerializer.Serialize(response);
        json.ShouldContain("\"type\":\"response\"");
        json.ShouldContain("\"success\":true");
    }

    [Test]
    public void DapEvent_serializes_correctly()
    {
        var evt = new DapEvent { Seq = 1, Event = "stopped", Body = new DapStoppedEventBody { Reason = "breakpoint" } };
        var json = JsonSerializer.Serialize(evt);
        json.ShouldContain("\"type\":\"event\"");
        json.ShouldContain("\"event\":\"stopped\"");
    }

    [Test]
    public void DapCapabilities_exposes_expected_features()
    {
        var caps = new DapCapabilities();
        caps.SupportsConfigurationDoneRequest.ShouldBeTrue();
        caps.SupportsTerminateRequest.ShouldBeTrue();
        caps.SupportsFunctionBreakpoints.ShouldBeFalse();
    }

    // ── Backend selection and integration tests ────────────────────────

    [Test]
    public void CreateBackend_defaults_to_gdb_for_null()
    {
        using var backend = DapServer.CreateBackend(null);
        backend.ShouldBeOfType<GdbDebuggerBackend>();
    }

    [Test]
    public void CreateBackend_defaults_to_gdb_for_unknown_value()
    {
        using var backend = DapServer.CreateBackend("unknown");
        backend.ShouldBeOfType<GdbDebuggerBackend>();
    }

    [Test]
    public void CreateBackend_returns_gdb_for_gdb()
    {
        using var backend = DapServer.CreateBackend("gdb");
        backend.ShouldBeOfType<GdbDebuggerBackend>();
    }

    [Test]
    public void CreateBackend_returns_lldb_for_lldb()
    {
        using var backend = DapServer.CreateBackend("lldb");
        backend.ShouldBeOfType<LldbDebuggerBackend>();
    }

    [Test]
    public void CreateBackend_is_case_insensitive()
    {
        using var backend = DapServer.CreateBackend("LLDB");
        backend.ShouldBeOfType<LldbDebuggerBackend>();
    }

    [Test]
    public void Lldb_start_info_defaults_to_lldb_dap()
    {
        var startInfo = LldbDebuggerBackend.CreateStartInfo(null);

        startInfo.FileName.ShouldBe("lldb-dap");
        startInfo.ArgumentList.ShouldBeEmpty();
    }

    [Test]
    public void Lldb_start_info_uses_explicit_debugger_path()
    {
        var startInfo = LldbDebuggerBackend.CreateStartInfo("/opt/llvm/bin/lldb-dap");

        startInfo.FileName.ShouldBe("/opt/llvm/bin/lldb-dap");
        startInfo.ArgumentList.ShouldBeEmpty();
    }

    [Test]
    public void Gdb_start_info_defaults_to_gdb_dap_interpreter()
    {
        var startInfo = GdbDebuggerBackend.CreateStartInfo(null);

        startInfo.FileName.ShouldBe("gdb");
        startInfo.ArgumentList.ShouldBe(["--interpreter=dap", "--quiet"]);
    }

    [Test]
    public void Gdb_start_info_uses_explicit_debugger_path()
    {
        var startInfo = GdbDebuggerBackend.CreateStartInfo("/opt/gdb/bin/gdb");

        startInfo.FileName.ShouldBe("/opt/gdb/bin/gdb");
        startInfo.ArgumentList.ShouldBe(["--interpreter=dap", "--quiet"]);
    }

    [Test]
    public async Task Lldb_start_reports_adapter_startup_errors()
    {
        var debuggerPath = CreateFailingDebuggerScript();

        try
        {
            using var backend = new LldbDebuggerBackend();
            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => backend.StartAsync("/tmp/test", null, null, debuggerPath, stopOnEntry: false)).ConfigureAwait(false);

            ex.Message.ShouldContain("lldb-dap exited immediately");
            ex.Message.ShouldContain("liblldb.so: cannot open shared object file");
        }
        finally
        {
            DeleteDebuggerScript(debuggerPath);
        }
    }

    [Test]
    public async Task Server_launch_with_gdb_type_uses_gdb_backend()
    {
        IDebuggerBackend? capturedBackend = null;

        var launchArgs = JsonSerializer.SerializeToElement(new
        {
            program = "/tmp/test",
            debuggerType = "gdb",
        });

        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "launch", launchArgs),
            CreateRequest(3, "disconnect"));

        using var server = new DapServer(inputStream, outputStream, debuggerType =>
        {
            var mock = new MockDebuggerBackend();
            capturedBackend = mock;
            return mock;
        });
        await server.RunAsync().ConfigureAwait(false);

        capturedBackend.ShouldNotBeNull();
        capturedBackend.ShouldBeOfType<MockDebuggerBackend>();

        var responses = ParseDapOutput(outputStream);
        var launchResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && string.Equals(cmd.GetString(), "launch"
, StringComparison.Ordinal) && r.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "response", StringComparison.Ordinal));

        launchResponse.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        launchResponse.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task Server_launch_with_lldb_type_passes_debugger_type_to_factory()
    {
        string? receivedDebuggerType = null;

        var launchArgs = JsonSerializer.SerializeToElement(new
        {
            program = "/tmp/test",
            debuggerType = "lldb",
        });

        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "launch", launchArgs),
            CreateRequest(3, "disconnect"));

        using var server = new DapServer(inputStream, outputStream, debuggerType =>
        {
            receivedDebuggerType = debuggerType;
            return new MockDebuggerBackend();
        });
        await server.RunAsync().ConfigureAwait(false);

        receivedDebuggerType.ShouldBe("lldb");
    }

    [Test]
    public async Task Server_launch_with_mock_backend_sets_pending_breakpoints()
    {
        var mock = new MockDebuggerBackend();

        var bpArgs = JsonSerializer.SerializeToElement(new
        {
            source = new { path = "/test/main.ash" },
            breakpoints = new[] { new { line = 5 }, new { line = 10 } }
        });

        var launchArgs = JsonSerializer.SerializeToElement(new
        {
            program = "/tmp/test",
            stopOnEntry = true,
        });

        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "setBreakpoints", bpArgs),
            CreateRequest(3, "launch", launchArgs),
            CreateRequest(4, "disconnect"));

        using var server = new DapServer(inputStream, outputStream, _ => mock);
        await server.RunAsync().ConfigureAwait(false);

        mock.BreakpointsSet.ShouldContain(("/test/main.ash", 5));
        mock.BreakpointsSet.ShouldContain(("/test/main.ash", 10));
    }

    [Test]
    public async Task Server_stackTrace_returns_parsed_frames_from_backend()
    {
        var mock = new MockDebuggerBackend
        {
            StackFrames =
            [
                new DapStackFrame
                {
                    Id = 0,
                    Name = "main",
                    Source = new DapSource { Name = "main.ash", Path = "/home/user/main.ash" },
                    Line = 5,
                },
            ],
        };

        var launchArgs = JsonSerializer.SerializeToElement(new
        {
            program = "/tmp/test",
            stopOnEntry = true,
        });

        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "launch", launchArgs),
            CreateRequest(3, "stackTrace"),
            CreateRequest(4, "disconnect"));

        using var server = new DapServer(inputStream, outputStream, _ => mock);
        await server.RunAsync().ConfigureAwait(false);

        var responses = ParseDapOutput(outputStream);
        var stResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && string.Equals(cmd.GetString(), "stackTrace"
, StringComparison.Ordinal) && r.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "response", StringComparison.Ordinal));

        stResponse.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        stResponse.GetProperty("success").GetBoolean().ShouldBeTrue();

        var body = stResponse.GetProperty("body");
        body.GetProperty("totalFrames").GetInt32().ShouldBe(1);
        var frames = body.GetProperty("stackFrames");
        frames.GetArrayLength().ShouldBe(1);
        frames[0].GetProperty("name").GetString().ShouldBe("main");
        frames[0].GetProperty("line").GetInt32().ShouldBe(5);
    }

    [Test]
    public async Task Server_variables_returns_parsed_locals_from_backend()
    {
        var mock = new MockDebuggerBackend
        {
            Locals =
            [
                new DapVariable { Name = "x", Value = "42", Type = "Int", VariablesReference = 0 },
                new DapVariable { Name = "tail", Value = "[3, 9]", Type = "List<Int> *", VariablesReference = 0 },
            ],
        };

        var launchArgs = JsonSerializer.SerializeToElement(new
        {
            program = "/tmp/test",
            stopOnEntry = true,
        });

        var scopeArgs = JsonSerializer.SerializeToElement(new
        {
            variablesReference = 1,
        });

        var (inputStream, outputStream) = CreateDapStreams(
            CreateRequest(1, "initialize"),
            CreateRequest(2, "launch", launchArgs),
            CreateRequest(3, "variables", scopeArgs),
            CreateRequest(4, "disconnect"));

        using var server = new DapServer(inputStream, outputStream, _ => mock);
        await server.RunAsync().ConfigureAwait(false);

        var responses = ParseDapOutput(outputStream);
        var varResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && string.Equals(cmd.GetString(), "variables"
, StringComparison.Ordinal) && r.TryGetProperty("type", out var type) && string.Equals(type.GetString(), "response", StringComparison.Ordinal));

        varResponse.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        varResponse.GetProperty("success").GetBoolean().ShouldBeTrue();

        var variables = varResponse.GetProperty("body").GetProperty("variables");
        variables.GetArrayLength().ShouldBe(2);
        variables[0].GetProperty("name").GetString().ShouldBe("x");
        variables[0].GetProperty("value").GetString().ShouldBe("42");
        variables[1].GetProperty("name").GetString().ShouldBe("tail");
        variables[1].GetProperty("value").GetString().ShouldBe("[3, 9]");
        variables[1].GetProperty("type").GetString().ShouldBe("List<Int> *");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string CreateRequest(int seq, string command, JsonElement? arguments = null)
    {
        var request = new DapRequest { Seq = seq, Command = command, Arguments = arguments };
        return JsonSerializer.Serialize(request);
    }

    private static (MemoryStream Input, MemoryStream Output) CreateDapStreams(params string[] requests)
    {
        var inputStream = new MemoryStream();
        foreach (var json in requests)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            inputStream.Write(header);
            inputStream.Write(body);
        }

        inputStream.Position = 0;
        return (inputStream, new MemoryStream());
    }

    private static string CreateFailingDebuggerScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(root, "lldb-dap.cmd");
            File.WriteAllText(path, "@echo error: liblldb.so: cannot open shared object file 1>&2\r\n@exit /b 1\r\n");
            return path;
        }

        var scriptPath = Path.Combine(root, "lldb-dap");
        File.WriteAllText(scriptPath, "#!/bin/sh\necho 'error: liblldb.so: cannot open shared object file' >&2\nexit 1\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return scriptPath;
    }

    private static void DeleteDebuggerScript(string debuggerPath)
    {
        var directory = Path.GetDirectoryName(debuggerPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        Directory.Delete(directory, recursive: true);
    }

    private static List<JsonElement> ParseDapOutput(MemoryStream outputStream)
    {
        var messages = new List<JsonElement>();
        var text = Encoding.UTF8.GetString(outputStream.ToArray());
        var remaining = text;

        while (remaining.Contains("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            var headerEnd = remaining.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                break;
            }

            var headerPart = remaining[..headerEnd];
            var contentLengthLine = headerPart.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));

            if (contentLengthLine is null)
            {
                break;
            }

            var lengthStr = contentLengthLine["Content-Length:".Length..].Trim();
            var length = int.Parse(lengthStr, System.Globalization.CultureInfo.InvariantCulture);
            var bodyStart = headerEnd + 4;
            var jsonBody = remaining[bodyStart..(bodyStart + length)];
            remaining = remaining[(bodyStart + length)..];

            try
            {
                var doc = JsonDocument.Parse(jsonBody);
                messages.Add(doc.RootElement.Clone());
            }
            catch
            {
                // Skip malformed messages
            }
        }

        return messages;
    }
}

/// <summary>
/// In-memory mock debugger backend for testing DapServer without requiring
/// a real GDB or LLDB process.
/// </summary>
internal sealed class MockDebuggerBackend : IDebuggerBackend
{
    public event Action<string>? OnStopped;
    public event Action<int>? OnExited;
    public event Action<string>? OnOutput;

    public List<(string Path, int Line)> BreakpointsSet { get; } = [];
    public DapStackFrame[] StackFrames { get; init; } = [];
    public DapVariable[] Locals { get; init; } = [];
    public bool Started { get; private set; }
    public bool StopOnEntryReceived { get; private set; }
    public bool Terminated { get; private set; }
    public bool RunCalled { get; private set; }

    public Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath, bool stopOnEntry)
    {
        Started = true;
        StopOnEntryReceived = stopOnEntry;
        return Task.CompletedTask;
    }

    public Task SetBreakpointAsync(string filePath, int line)
    {
        BreakpointsSet.Add((filePath, line));
        return Task.CompletedTask;
    }

    public Task ContinueAsync() => Task.CompletedTask;
    public Task StepOverAsync() => Task.CompletedTask;
    public Task StepInAsync() => Task.CompletedTask;
    public Task StepOutAsync() => Task.CompletedTask;

    public Task RunAsync()
    {
        RunCalled = true;
        return Task.CompletedTask;
    }

    public Task<DapStackFrame[]> GetStackTraceAsync() => Task.FromResult(StackFrames);
    public Task<DapVariable[]> GetLocalsAsync() => Task.FromResult(Locals);

    public Task TerminateAsync()
    {
        Terminated = true;
        return Task.CompletedTask;
    }

    public void FireStopped(string reason) => OnStopped?.Invoke(reason);
    public void FireExited(int exitCode) => OnExited?.Invoke(exitCode);
    public void FireOutput(string text) => OnOutput?.Invoke(text);

    public void Dispose() { }
}
