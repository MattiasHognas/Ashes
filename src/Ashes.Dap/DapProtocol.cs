using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashes.Dap;

/// <summary>
/// Debug Adapter Protocol (DAP) base message types.
/// See https://microsoft.github.io/debug-adapter-protocol/specification
/// </summary>
public abstract record DapMessage
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed record DapRequest : DapMessage
{
    [JsonPropertyName("type")]
    public override string Type => "request";

    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

public sealed record DapResponse : DapMessage
{
    [JsonPropertyName("type")]
    public override string Type => "response";

    [JsonPropertyName("request_seq")]
    public int RequestSeq { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; init; }
}

public sealed record DapEvent : DapMessage
{
    [JsonPropertyName("type")]
    public override string Type => "event";

    [JsonPropertyName("event")]
    public string Event { get; init; } = "";

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; init; }
}

// ── Capability types ────────────────────────────────────────────────────

public sealed record DapCapabilities
{
    [JsonPropertyName("supportsConfigurationDoneRequest")]
    public bool SupportsConfigurationDoneRequest => true;

    [JsonPropertyName("supportsFunctionBreakpoints")]
    public bool SupportsFunctionBreakpoints => false;

    [JsonPropertyName("supportsSetVariable")]
    public bool SupportsSetVariable => false;

    [JsonPropertyName("supportsStepBack")]
    public bool SupportsStepBack => false;

    [JsonPropertyName("supportsTerminateRequest")]
    public bool SupportsTerminateRequest => true;
}

// ── Event body types ────────────────────────────────────────────────────

public sealed record DapInitializedEventBody;

public sealed record DapStoppedEventBody
{
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("threadId")]
    public int ThreadId { get; init; } = 1;

    [JsonPropertyName("allThreadsStopped")]
    public bool AllThreadsStopped { get; init; } = true;
}

public sealed record DapTerminatedEventBody;

public sealed record DapExitedEventBody
{
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }
}

// ── Request argument types ──────────────────────────────────────────────

public sealed record DapLaunchArguments
{
    [JsonPropertyName("program")]
    public string Program { get; init; } = "";

    [JsonPropertyName("args")]
    public string[]? Args { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("stopOnEntry")]
    public bool StopOnEntry { get; init; }

    [JsonPropertyName("debuggerPath")]
    public string? DebuggerPath { get; init; }
}

public sealed record DapSetBreakpointsArguments
{
    [JsonPropertyName("source")]
    public DapSource Source { get; init; } = new();

    [JsonPropertyName("breakpoints")]
    public DapSourceBreakpoint[]? Breakpoints { get; init; }
}

public sealed record DapSource
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}

public sealed record DapSourceBreakpoint
{
    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Column { get; init; }
}

// ── Response body types ─────────────────────────────────────────────────

public sealed record DapBreakpoint
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Line { get; init; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DapSource? Source { get; init; }
}

public sealed record DapThread
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
}

public sealed record DapStackFrame
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DapSource? Source { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("column")]
    public int Column { get; init; }
}

public sealed record DapScope
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; init; }

    [JsonPropertyName("expensive")]
    public bool Expensive { get; init; }
}

public sealed record DapVariable
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; init; }
}
