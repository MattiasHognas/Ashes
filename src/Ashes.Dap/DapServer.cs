using System.Text.Json;

namespace Ashes.Dap;

/// <summary>
/// Debug Adapter Protocol server for Ashes. Communicates with the IDE
/// (VS Code) over stdin/stdout and delegates to GDB via the MI protocol.
/// </summary>
public sealed class DapServer : IDisposable
{
    private readonly DapTransport _transport;
    private readonly GdbDebuggerBackend _debugger;
    private bool _initialized;
    private bool _configurationDone;
    private DapLaunchArguments? _launchArgs;
    private readonly List<(string Path, int Line)> _pendingBreakpoints = [];

    public DapServer(Stream input, Stream output)
    {
        _transport = new DapTransport(input, output);
        _debugger = new GdbDebuggerBackend();

        _debugger.OnStopped += reason =>
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

        _debugger.OnExited += exitCode =>
        {
            _transport.SendEvent("exited", new DapExitedEventBody { ExitCode = exitCode });
            _transport.SendEvent("terminated", new DapTerminatedEventBody());
        };
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
                HandleSetBreakpoints(request);
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
            await _debugger.StartAsync(
                _launchArgs.Program,
                _launchArgs.Cwd,
                _launchArgs.Args,
                _launchArgs.DebuggerPath);

            // Set any breakpoints that were sent before launch
            foreach (var (path, line) in _pendingBreakpoints)
            {
                await _debugger.SetBreakpointAsync(path, line);
            }

            _pendingBreakpoints.Clear();
            _transport.SendResponse(request, success: true);

            // If stopOnEntry, don't auto-run (GDB will stop at entry)
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

    private void HandleSetBreakpoints(DapRequest request)
    {
        var args = request.Arguments.HasValue
            ? JsonSerializer.Deserialize<DapSetBreakpointsArguments>(request.Arguments.Value.GetRawText())
            : null;

        var breakpoints = new List<DapBreakpoint>();
        if (args?.Source.Path is not null && args.Breakpoints is not null)
        {
            int id = 1;
            foreach (var bp in args.Breakpoints)
            {
                _pendingBreakpoints.Add((args.Source.Path, bp.Line));
                breakpoints.Add(new DapBreakpoint
                {
                    Id = id++,
                    Verified = true,
                    Line = bp.Line,
                    Source = args.Source,
                });

                // If debugger is already running, set breakpoint immediately
                if (_launchArgs is not null)
                {
                    _ = _debugger.SetBreakpointAsync(args.Source.Path, bp.Line);
                }
            }
        }

        _transport.SendResponse(request, success: true, body: new { breakpoints });
    }

    private async Task HandleConfigurationDoneAsync(DapRequest request)
    {
        _configurationDone = true;
        _transport.SendResponse(request, success: true);

        // If launch was already called and not stopOnEntry, start execution
        if (_launchArgs is not null && !_launchArgs.StopOnEntry)
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
        // TODO: Parse GDB MI stack-list-frames response
        await _debugger.GetStackTraceAsync();
        _transport.SendResponse(request, success: true, body: new
        {
            stackFrames = Array.Empty<DapStackFrame>(),
            totalFrames = 0,
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
        // TODO: Parse GDB MI stack-list-locals response
        await _debugger.GetLocalsAsync();
        _transport.SendResponse(request, success: true, body: new
        {
            variables = Array.Empty<DapVariable>()
        });
    }

    private async Task HandleContinueAsync(DapRequest request)
    {
        await _debugger.ContinueAsync();
        _transport.SendResponse(request, success: true, body: new { allThreadsContinued = true });
    }

    private async Task HandleNextAsync(DapRequest request)
    {
        await _debugger.StepOverAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleStepInAsync(DapRequest request)
    {
        await _debugger.StepInAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleStepOutAsync(DapRequest request)
    {
        await _debugger.StepOutAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleDisconnectAsync(DapRequest request)
    {
        await _debugger.TerminateAsync();
        _transport.SendResponse(request, success: true);
    }

    private async Task HandleTerminateAsync(DapRequest request)
    {
        await _debugger.TerminateAsync();
        _transport.SendResponse(request, success: true);
    }

    public void Dispose()
    {
        _debugger.Dispose();
    }
}
