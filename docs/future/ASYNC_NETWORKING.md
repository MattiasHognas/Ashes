# Async Networking — Status & Roadmap

Async-only TCP/HTTP is now part of the language surface.
The public APIs return `Task(E, A)` and must be used inside `async`
blocks with `await`.

The current implementation still performs blocking TCP/HTTP work at task
execution time, but networking now lowers through dedicated leaf-task IR
nodes and named runtime ABI symbols instead of running as ordinary
task-coroutine bodies. The remaining roadmap is a true non-blocking
runtime backed by readiness-based I/O plus the missing
concurrency-focused test coverage.

------------------------------------------------------------------------

## Completed Work

The async-only API redesign is partially complete:

| Area | What was done |
|------|---------------|
| **Language specification** | `docs/LANGUAGE_SPEC.md` now documents `Ashes.Http.get`, `Ashes.Http.post`, `Ashes.Net.Tcp.connect`, `send`, `receive`, and `close` as returning `Task(Str, ...)` and requiring `async`/`await`. |
| **Standard library docs** | `docs/STANDARD_LIBRARY.md` now describes HTTP and TCP as async-only APIs returning `Task(Str, ...)`. |
| **Builtin registry shape** | Networking remains under `BuiltinValueKind` and is still dispatched as qualified builtins (`HttpGet`, `HttpPost`, `NetTcpConnect`, `NetTcpSend`, `NetTcpReceive`, `NetTcpClose`). |
| **Type bindings** | The `CreateXxxBinding()` methods in `Lowering.cs` now return `Task(Str, X)` instead of `Result(Str, X)` for all six networking builtins. |
| **Leaf-task lowering** | `LowerHttpGet`, `LowerHttpPost`, `LowerNetTcpConnect`, `LowerNetTcpSend`, `LowerNetTcpReceive`, and `LowerNetTcpClose` now lower directly to dedicated task-producing IR instructions: `CreateHttpGetTask`, `CreateHttpPostTask`, `CreateTcpConnectTask`, `CreateTcpSendTask`, `CreateTcpReceiveTask`, and `CreateTcpCloseTask`. |
| **Async-only enforcement** | Calling the networking builtins outside `async` now emits `ASH012`, a dedicated compile-time diagnostic for async-only networking APIs. |
| **Supporting compiler passes** | The IR optimizer and state-machine transform now understand the dedicated networking task instructions, so lowering, temp remapping, and suspend/resume analysis all preserve them correctly. |
| **Runtime ABI symbols** | The LLVM backend now emits named runtime symbols for networking: `ashes_tcp_connect`, `ashes_tcp_send`, `ashes_tcp_receive`, `ashes_tcp_close`, `ashes_http_get`, and `ashes_http_post`. |
| **ABI-based lowering path** | HTTP/TCP instruction codegen and `Drop(Socket)` now route through the runtime ABI symbols instead of calling the backend networking helpers directly at each instruction site. |
| **Platform runtime shims** | The named runtime symbols are currently implemented as compiler-emitted runtime helper functions in the generated module, keeping Linux and Windows socket details behind the ABI layer. |
| **Leaf-step ABI boundary** | Networking leaf tasks are now stepped through dedicated task-level runtime symbols (`ashes_step_tcp_connect_task`, `ashes_step_tcp_send_task`, `ashes_step_tcp_receive_task`, `ashes_step_tcp_close_task`, `ashes_step_http_get_task`, `ashes_step_http_post_task`) that take the task pointer and return a step status. |
| **Task runtime integration** | The LLVM task runner now recognizes negative-state leaf tasks for sleep, TCP, and HTTP operations, stores task arguments in the task header, completes those tasks through the ABI layer, and propagates awaited leaf-task results back into parent coroutines. |
| **Backend compatibility path** | The existing `HttpGet`, `HttpPost`, `NetTcpConnect`, `NetTcpSend`, `NetTcpReceive`, and `NetTcpClose` IR instructions remain intact and still call through the runtime ABI, but the public async networking surface now lowers through the dedicated leaf-task IR path instead of wrapping those instructions in coroutines. |
| **Test migration** | The HTTP and TCP end-to-end tests were rewritten to use `async`, `await`, and `Ashes.Async.run`, and compile-error coverage was added for using HTTP/TCP outside `async`. |
| **Future-features status** | `docs/future/FUTURE_FEATURES.md` now treats the async-only API surface as complete while leaving the non-blocking runtime optimization as future work. |

------------------------------------------------------------------------

## Ordered Roadmap — Next Work Items

Every remaining task below is still open. The leaf-task IR path, the
runtime ABI boundary, and the task-level leaf-step ABI boundary are now
in place, so the remaining work starts with replacing the current
blocking leaf-task execution model with true readiness-driven suspension
and resumption.

### 1. Replace blocking networking with non-blocking readiness I/O

Move from "blocking call during leaf-task completion" to true async
networking backed by a readiness-aware runtime.

- Add explicit task wait-state metadata and a readiness registry/event-loop
    model so pending leaf tasks can suspend without busy-spinning and be
    resumed coherently from `run`, `all`, and `race`.
- Use `epoll` on Linux.
- Use IO completion ports on Windows.
- Implement non-blocking connect, send, receive, and close for TCP.
- Rebuild HTTP get/post on top of async TCP rather than the current
    direct blocking helper path.
- Ensure task suspension and resumption are driven by runtime readiness
    rather than a blocking call inside the task runner's leaf-task path.

### 2. Add concurrency-focused networking tests

The current test set validates the async-only API surface, but it does
not yet prove the behavior expected from a non-blocking runtime.

- Add concurrent HTTP request tests.
- Add parallel TCP connection tests.
- Add explicit async resource-cleanup coverage.
- Add error-propagation-across-await coverage for networking failures.
- Keep the existing outside-async and compile-error tests.

### 3. Re-run full verification after the runtime work lands

The runtime and ABI changes cross multiple compiler layers and must be
validated as a full stack, not only with targeted tests.

- Run `dotnet run --project src/Ashes.Tests -- --no-progress`.
- Run `dotnet run --project src/Ashes.Lsp.Tests -- --no-progress`.
- Run `dotnet run --project src/Ashes.Cli -- test tests`.
- Run `dotnet format Ashes.slnx --verify-no-changes`.

------------------------------------------------------------------------

## Explicitly Deferred

The following items are not implemented yet and should not be described
as done:

- The current runtime ABI symbols are compiler-emitted helper functions,
    not a separately packaged shared runtime yet.
- TCP/HTTP operations are not yet truly non-blocking at runtime; the
    current leaf tasks still complete synchronously when run.
- No readiness-based `epoll`/IOCP networking runtime has landed yet.
- The current tests do not yet cover the concurrency-focused
    deliverables for async networking.
