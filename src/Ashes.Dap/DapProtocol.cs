using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashes.Dap;

/// <summary>
/// Debug Adapter Protocol (DAP) base message types.
/// See https://microsoft.github.io/debug-adapter-protocol/specification
/// </summary>
public abstract record DapMessage
{
    /// <summary>Monotonic sequence number identifying this message within the session.</summary>
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    /// <summary>Message kind discriminator: <c>"request"</c>, <c>"response"</c>, or <c>"event"</c>.</summary>
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>A DAP request sent by the client, naming a command and its optional argument payload.</summary>
public sealed record DapRequest : DapMessage
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public override string Type => "request";

    /// <summary>Name of the requested command (e.g. <c>"launch"</c>, <c>"setBreakpoints"</c>).</summary>
    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    /// <summary>Raw command-specific argument object, deserialized on demand per command.</summary>
    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }
}

/// <summary>A DAP response to a client request, carrying success status and an optional result body.</summary>
public sealed record DapResponse : DapMessage
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public override string Type => "response";

    /// <summary>Sequence number of the request this response answers.</summary>
    [JsonPropertyName("request_seq")]
    public int RequestSeq { get; init; }

    /// <summary>Whether the request succeeded.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>Name of the command this response corresponds to.</summary>
    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    /// <summary>Human-readable error text, present only when the request failed.</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    /// <summary>Command-specific result payload, omitted when there is none.</summary>
    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; init; }
}

/// <summary>A DAP event pushed from the server to the client, such as <c>stopped</c> or <c>output</c>.</summary>
public sealed record DapEvent : DapMessage
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public override string Type => "event";

    /// <summary>Name of the event (e.g. <c>"stopped"</c>, <c>"exited"</c>, <c>"output"</c>).</summary>
    [JsonPropertyName("event")]
    public string Event { get; init; } = "";

    /// <summary>Event-specific payload, omitted when the event has no body.</summary>
    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; init; }
}

// Capability types

/// <summary>Capabilities the Ashes DAP server advertises to the client in the <c>initialize</c> response.</summary>
public sealed record DapCapabilities
{
    /// <summary>Whether the server expects a <c>configurationDone</c> request after configuration is complete.</summary>
    [JsonPropertyName("supportsConfigurationDoneRequest")]
    public bool SupportsConfigurationDoneRequest => true;

    /// <summary>Whether the server supports function breakpoints.</summary>
    [JsonPropertyName("supportsFunctionBreakpoints")]
    public bool SupportsFunctionBreakpoints => false;

    /// <summary>Whether the server supports setting variable values from the client.</summary>
    [JsonPropertyName("supportsSetVariable")]
    public bool SupportsSetVariable => false;

    /// <summary>Whether the server supports stepping backwards.</summary>
    [JsonPropertyName("supportsStepBack")]
    public bool SupportsStepBack => false;

    /// <summary>Whether the server supports the <c>terminate</c> request.</summary>
    [JsonPropertyName("supportsTerminateRequest")]
    public bool SupportsTerminateRequest => true;
}

// Event body types

/// <summary>Body of the <c>initialized</c> event; empty, signalling the server is ready for configuration.</summary>
public sealed record DapInitializedEventBody;

/// <summary>Body of the <c>stopped</c> event, describing why and where the debuggee halted.</summary>
public sealed record DapStoppedEventBody
{
    /// <summary>Reason the debuggee stopped (e.g. <c>"breakpoint"</c>, <c>"step"</c>, <c>"pause"</c>).</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    /// <summary>Identifier of the thread that stopped.</summary>
    [JsonPropertyName("threadId")]
    public int ThreadId { get; init; } = 1;

    /// <summary>Whether all threads stopped along with the reporting thread.</summary>
    [JsonPropertyName("allThreadsStopped")]
    public bool AllThreadsStopped { get; init; } = true;
}

/// <summary>Body of the <c>terminated</c> event; empty, signalling the debug session has ended.</summary>
public sealed record DapTerminatedEventBody;

/// <summary>Body of the <c>exited</c> event, reporting the debuggee's process exit code.</summary>
public sealed record DapExitedEventBody
{
    /// <summary>Exit code the debuggee process returned.</summary>
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }
}

// Request argument types

/// <summary>Arguments for the <c>launch</c> request describing the Ashes program to debug and how.</summary>
public sealed record DapLaunchArguments
{
    /// <summary>Path to the compiled Ashes executable to launch under the debugger.</summary>
    [JsonPropertyName("program")]
    public string Program { get; init; } = "";

    /// <summary>Command-line arguments passed to the debuggee.</summary>
    [JsonPropertyName("args")]
    public string[]? Args { get; init; }

    /// <summary>Working directory for the debuggee; defaults to the launcher's when null.</summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    /// <summary>Whether to stop at the program entry point before running any user code.</summary>
    [JsonPropertyName("stopOnEntry")]
    public bool StopOnEntry { get; init; }

    /// <summary>Explicit path to the native debugger binary; the backend's default is used when null.</summary>
    [JsonPropertyName("debuggerPath")]
    public string? DebuggerPath { get; init; }

    /// <summary>
    /// Selects the native debugger backend. Accepted values are
    /// <c>"gdb"</c> (default) and <c>"lldb"</c>.
    /// </summary>
    [JsonPropertyName("debuggerType")]
    public string? DebuggerType { get; init; }
}

/// <summary>Arguments for the <c>setBreakpoints</c> request: the source file and the breakpoints it should carry.</summary>
public sealed record DapSetBreakpointsArguments
{
    /// <summary>Source file the breakpoints apply to.</summary>
    [JsonPropertyName("source")]
    public DapSource Source { get; init; } = new();

    /// <summary>Full set of breakpoints for the source; DAP replaces all prior breakpoints for the file.</summary>
    [JsonPropertyName("breakpoints")]
    public DapSourceBreakpoint[]? Breakpoints { get; init; }
}

/// <summary>Reference to a source file by display name and/or filesystem path.</summary>
public sealed record DapSource
{
    /// <summary>Short display name of the source, shown in the UI when present.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>Filesystem path of the source, when it is backed by a file.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}

/// <summary>A breakpoint requested by the client at a line (and optionally column) within a source.</summary>
public sealed record DapSourceBreakpoint
{
    /// <summary>One-based line number where the breakpoint should be placed.</summary>
    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>One-based column within the line; zero when unspecified.</summary>
    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Column { get; init; }
}

// Response body types

/// <summary>A breakpoint as resolved by the server, reported back in the <c>setBreakpoints</c> response.</summary>
public sealed record DapBreakpoint
{
    /// <summary>Server-assigned identifier for the breakpoint.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Whether the debugger was able to bind the breakpoint to executable code.</summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    /// <summary>Actual line the breakpoint resolved to; zero when unspecified.</summary>
    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Line { get; init; }

    /// <summary>Source the breakpoint belongs to, when known.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DapSource? Source { get; init; }
}

/// <summary>A thread of execution reported in the <c>threads</c> response.</summary>
public sealed record DapThread
{
    /// <summary>Identifier of the thread.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Display name of the thread.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
}

/// <summary>A single frame in a stack trace, locating a call by function, source, and position.</summary>
public sealed record DapStackFrame
{
    /// <summary>Identifier used to reference this frame in later requests (e.g. <c>scopes</c>).</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Display name of the frame, typically the function name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Source file the frame executes in, when known.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DapSource? Source { get; init; }

    /// <summary>One-based line within the source for the frame's current position.</summary>
    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>One-based column within the line for the frame's current position.</summary>
    [JsonPropertyName("column")]
    public int Column { get; init; }
}

/// <summary>A variable scope (such as Locals) reported in the <c>scopes</c> response.</summary>
public sealed record DapScope
{
    /// <summary>Display name of the scope (e.g. <c>"Locals"</c>).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Reference used to fetch the scope's variables via the <c>variables</c> request.</summary>
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; init; }

    /// <summary>Whether retrieving the scope's variables is expensive and should be deferred.</summary>
    [JsonPropertyName("expensive")]
    public bool Expensive { get; init; }
}

/// <summary>A variable reported in the <c>variables</c> response, with its Ashes-formatted value.</summary>
public sealed record DapVariable
{
    /// <summary>Name of the variable.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Displayed value of the variable, after Ashes-aware formatting.</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

    /// <summary>Type name of the variable, when the debugger reports one.</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>Reference for expanding structured children; zero for leaf values.</summary>
    [JsonPropertyName("variablesReference")]
    public int VariablesReference { get; init; }
}
