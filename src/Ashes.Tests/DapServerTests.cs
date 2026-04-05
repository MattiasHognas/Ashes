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
        var received = await transport.ReadRequestAsync();

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

        var result = await transport.ReadRequestAsync();
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
        await server.RunAsync();

        var responses = ParseDapOutput(outputStream);
        var initResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && cmd.GetString() == "initialize"
            && r.TryGetProperty("type", out var type) && type.GetString() == "response");

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
        await server.RunAsync();

        var messages = ParseDapOutput(outputStream);
        var initializedEvent = messages.FirstOrDefault(m =>
            m.TryGetProperty("type", out var type) && type.GetString() == "event"
            && m.TryGetProperty("event", out var evt) && evt.GetString() == "initialized");

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
        await server.RunAsync();

        var responses = ParseDapOutput(outputStream);
        var threadsResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && cmd.GetString() == "threads"
            && r.TryGetProperty("type", out var type) && type.GetString() == "response");

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
        await server.RunAsync();

        var responses = ParseDapOutput(outputStream);
        var errorResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && cmd.GetString() == "nonExistentCommand"
            && r.TryGetProperty("type", out var type) && type.GetString() == "response");

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
        await server.RunAsync();

        var responses = ParseDapOutput(outputStream);
        var bpResponse = responses.FirstOrDefault(r =>
            r.TryGetProperty("command", out var cmd) && cmd.GetString() == "setBreakpoints"
            && r.TryGetProperty("type", out var type) && type.GetString() == "response");

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

    // ── MI response parser tests ──────────────────────────────────────────

    [Test]
    public void ParseStackFrames_extracts_frames_from_mi_response()
    {
        var mi = """1^done,stack=[frame={level="0",addr="0x401000",func="main",file="main.ash",fullname="/home/user/main.ash",line="5"},frame={level="1",addr="0x401100",func="helper",file="lib.ash",fullname="/home/user/lib.ash",line="12"}]""";

        var frames = MiResponseParser.ParseStackFrames(mi);

        frames.Length.ShouldBe(2);

        frames[0].Id.ShouldBe(0);
        frames[0].Name.ShouldBe("main");
        frames[0].Line.ShouldBe(5);
        frames[0].Source.ShouldNotBeNull();
        frames[0].Source!.Name.ShouldBe("main.ash");
        frames[0].Source!.Path.ShouldBe("/home/user/main.ash");

        frames[1].Id.ShouldBe(1);
        frames[1].Name.ShouldBe("helper");
        frames[1].Line.ShouldBe(12);
        frames[1].Source.ShouldNotBeNull();
        frames[1].Source!.Path.ShouldBe("/home/user/lib.ash");
    }

    [Test]
    public void ParseStackFrames_returns_empty_for_empty_response()
    {
        var frames = MiResponseParser.ParseStackFrames("");
        frames.Length.ShouldBe(0);
    }

    [Test]
    public void ParseLocals_extracts_variables_from_mi_response()
    {
        var mi = """1^done,locals=[{name="x",value="42"},{name="msg",value="hello"}]""";

        var vars = MiResponseParser.ParseLocals(mi);

        vars.Length.ShouldBe(2);
        vars[0].Name.ShouldBe("x");
        vars[0].Value.ShouldBe("42");
        vars[1].Name.ShouldBe("msg");
        vars[1].Value.ShouldBe("hello");
    }

    [Test]
    public void ParseLocals_returns_empty_for_no_locals()
    {
        var mi = """1^done,locals=[]""";
        var vars = MiResponseParser.ParseLocals(mi);
        vars.Length.ShouldBe(0);
    }

    [Test]
    public void ParseLocals_returns_empty_for_empty_response()
    {
        var vars = MiResponseParser.ParseLocals("");
        vars.Length.ShouldBe(0);
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
