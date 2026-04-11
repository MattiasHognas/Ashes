using System.Text.Json;

namespace Ashes.Dap;

/// <summary>
/// Debug Adapter Protocol server for Ashes. Communicates with the IDE
/// (VS Code) over stdin/stdout and delegates to a native debugger
/// (GDB or LLDB) via the Machine Interface protocol.
/// </summary>
public sealed class DapServer : IDisposable
{
    private readonly DapTransport _transport;
    private readonly Func<string?, IDebuggerBackend> _backendFactory;
    private IDebuggerBackend? _debugger;
    private bool _initialized;
    private bool _configurationDone;
    private DapLaunchArguments? _launchArgs;
    private readonly Dictionary<string, List<int>> _pendingBreakpoints = [];

    public DapServer(Stream input, Stream output)
        : this(input, output, CreateBackend)
    {
    }

    /// <summary>
    /// Creates a DapServer with a custom backend factory (used for testing).
    /// </summary>
    public DapServer(Stream input, Stream output, Func<string?, IDebuggerBackend> backendFactory)
    {
        _transport = new DapTransport(input, output);
        _backendFactory = backendFactory;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var request = await _transport.ReadRequestAsync(ct);
            if (request is null)
            {
                break; // EOF
            }

            await HandleRequestAsync(request);
        }
    }

    private async Task HandleRequestAsync(DapRequest request)
    {
        switch (request.Command)
        {
            case "initialize":
                HandleInitialize(request);
                break;
            case "launch":
                await HandleLaunchAsync(request);
                break;
            case "setBreakpoints":
                await HandleSetBreakpointsAsync(request);
                break;
            case "configurationDone":
                await HandleConfigurationDoneAsync(request);
                break;
            case "threads":
                HandleThreads(request);
                break;
            case "stackTrace":
                await HandleStackTraceAsync(request);
                break;
            case "scopes":
                HandleScopes(request);
                break;
            case "variables":
                await HandleVariablesAsync(request);
                break;
            case "continue":
                await HandleContinueAsync(request);
                break;
            case "next":
                await HandleNextAsync(request);
                break;
            case "stepIn":
                await HandleStepInAsync(request);
                break;
            case "stepOut":
                await HandleStepOutAsync(request);
                break;
            case "disconnect":
                await HandleDisconnectAsync(request);
                break;
            case "terminate":
                await HandleTerminateAsync(request);
                break;
            default:
                _transport.SendResponse(request, success: false, message: $"Unknown command: {request.Command}");
                break;
        }
    }

    private void HandleInitialize(DapRequest request)
    {
        _initialized = true;
        _transport.SendResponse(request, success: true, body: new DapCapabilities());
        _transport.SendEvent("initialized", new DapInitializedEventBody());
    }

    private async Task HandleLaunchAsync(DapRequest request)
    {
        if (!_initialized)
        {
            _transport.SendResponse(request, success: false, message: "Not initialized.");
            return;
        }

        _launchArgs = request.Arguments.HasValue
            ? JsonSerializer.Deserialize<DapLaunchArguments>(request.Arguments.Value.GetRawText())
            : new DapLaunchArguments();

        if (_launchArgs is null || string.IsNullOrEmpty(_launchArgs.Program))
        {
            _transport.SendResponse(request, success: false, message: "Missing 'program' in launch arguments.");
            return;
        }

        try
        {
            _debugger = _backendFactory(_launchArgs.DebuggerType);
            WireDebuggerEvents(_debugger);

            await _debugger.StartAsync(
                _launchArgs.Program,
                _launchArgs.Cwd,
                _launchArgs.Args,
                _launchArgs.DebuggerPath);

            // Set any breakpoints that were sent before launch
            foreach (var (path, lines) in _pendingBreakpoints)
            {
                foreach (var line in lines)
                {
                    await _debugger.SetBreakpointAsync(path, line);
                }
            }

            _pendingBreakpoints.Clear();
            _transport.SendResponse(request, success: true);

            // If stopOnEntry, don't auto-run (debugger will stop at entry)
            if (!_launchArgs.StopOnEntry && _configurationDone)
            {
                await _debugger.RunAsync();
            }
        }
        catch (Exception ex)
        {
            _transport.SendResponse(request, success: false, message: ex.Message);
        }
    }

    public static IDebuggerBackend CreateBackend(string? debuggerType)
    {
        return debuggerType?.ToLowerInvariant() switch
        {
            "lldb" => new LldbDebuggerBackend(),
            _ => new GdbDebuggerBackend(),
        };
    }

    private void WireDebuggerEvents(IDebuggerBackend backend)
    {
        backend.OnStopped += reason =>
        {
            _transport.SendEvent("stopped", new DapStoppedEventBody
            {
                Reason = reason switch
                {
                    "breakpoint-hit" => "breakpoint",
                    "end-stepping-range" => "step",
                    "signal-received" => "pause",
                    _ => reason,
                },
            });
        };

        backend.OnExited += exitCode =>
        {
            _transport.SendEvent("exited", new DapExitedEventBody { ExitCode = exitCode });
            _transport.SendEvent("terminated", new DapTerminatedEventBody());
        };

        backend.OnOutput += text =>
        {
            _transport.SendEvent("output", new
            {
                category = "console",
                output = text,
            });
        };
    }

    private async Task HandleSetBreakpointsAsync(DapRequest request)
    {
        var args = request.Arguments.HasValue
            ? JsonSerializer.Deserialize<DapSetBreakpointsArguments>(request.Arguments.Value.GetRawText())
            : null;

        var breakpoints = new List<DapBreakpoint>();
        if (args?.Source.Path is not null && args.Breakpoints is not null)
        {
            // Replace breakpoints for this source (DAP spec: setBreakpoints replaces all for a file)
            var lines = args.Breakpoints.Select(bp => bp.Line).ToList();
            _pendingBreakpoints[args.Source.Path] = lines;

            int id = 1;
            foreach (var bp in args.Breakpoints)
            {
                bool verified = true;

                // If debugger is already running, set breakpoint immediately and track success
                if (_debugger is not null && _launchArgs is not null)
                {
                    try
                    {
                        await _debugger.SetBreakpointAsync(args.Source.Path, bp.Line);
                    }
                    catch (Exception ex)
                    {
                        verified = false;
                        _transport.SendEvent("output", new
                        {
                            category = "console",
                            output = $"Failed to set breakpoint at {args.Source.Path}:{bp.Line}: {ex.Message}\n",
                        });
                    }
                }

                breakpoints.Add(new DapBreakpoint
                {
                    Id = id++,
                    Verified = verified,
                    Line = bp.Line,
                    Source = args.Source,
                });
            }
        }
        else if (args?.Source.Path is not null)
        {
            // Empty breakpoints array means clear all breakpoints for this source
            _pendingBreakpoints.Remove(args.Source.Path);
        }

        _transport.SendResponse(request, success: true, body: new { breakpoints });
    }

    private async Task HandleConfigurationDoneAsync(DapRequest request)
    {
        _configurationDone = true;
        _transport.SendResponse(request, success: true);

        // If launch was already called and not stopOnEntry, start execution
        if (_debugger is not null && _launchArgs is not null && !_launchArgs.StopOnEntry)
        {
            await _debugger.RunAsync();
        }
    }

    private void HandleThreads(DapRequest request)
    {
        _transport.SendResponse(request, success: true, body: new
        {
            threads = new[] { new DapThread { Id = 1, Name = "main" } }
        });
    }

    private async Task HandleStackTraceAsync(DapRequest request)
    {
        DapStackFrame[] stackFrames = [];
        if (_debugger is not null)
        {
            var miResponse = await _debugger.GetStackTraceAsync();
            stackFrames = MiResponseParser.ParseStackFrames(miResponse);
        }

        _transport.SendResponse(request, success: true, body: new
        {
            stackFrames,
            totalFrames = stackFrames.Length,
        });
    }

    private void HandleScopes(DapRequest request)
    {
        _transport.SendResponse(request, success: true, body: new
        {
            scopes = new[]
            {
                new DapScope { Name = "Locals", VariablesReference = 1 }
            }
        });
    }

    private async Task HandleVariablesAsync(DapRequest request)
    {
        DapVariable[] variables = [];
        if (_debugger is not null)
        {
            var miResponse = await _debugger.GetLocalsAsync();
            variables = MiResponseParser.ParseLocals(miResponse);
        }

        _transport.SendResponse(request, success: true, body: new
        {
            variables,
        });
    }

    private async Task HandleContinueAsync(DapRequest request)
    {
        if (_debugger is not null) await _debugger.ContinueAsync();
        _transport.SendResponse(request, success: true, body: new { allThreadsContinued = true });
    }

    private async Task HandleNextAsync(DapRequest request)
    {
        if (_debugger is not null) await _debugger.StepOverAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleStepInAsync(DapRequest request)
    {
        if (_debugger is not null) await _debugger.StepInAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleStepOutAsync(DapRequest request)
    {
        if (_debugger is not null) await _debugger.StepOutAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleDisconnectAsync(DapRequest request)
    {
        if (_debugger is not null) await _debugger.TerminateAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleTerminateAsync(DapRequest request)
    {
        if (_debugger is not null) await _debugger.TerminateAsync();
        _transport.SendResponse(request, success: true);
    }

    public void Dispose()
    {
        _debugger?.Dispose();
    }
}
