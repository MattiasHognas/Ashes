# Async Networking — Status & Roadmap

Async-only TCP/HTTP is now part of the language surface.
The public APIs return `Task(E, A)` and must be used inside `async`
blocks with `await`.

The current implementation now includes explicit task wait metadata, a
pending-leaf wait path in the task runner, and pending-aware TCP leaf
stepping on the Windows backend. Linux leaf networking and HTTP still
use the older blocking helper path, and `Ashes.Async.all` / `race` do
not yet drive multiple pending tasks through a shared readiness-aware
scheduler. The remaining roadmap is finishing that cross-platform,
cross-task non-blocking runtime plus the missing concurrency-focused
test coverage.

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
| **Task wait metadata** | The task header now carries explicit wait metadata (`WaitKind`, `WaitHandle`, `WaitData0`, `WaitData1`) so leaf tasks can preserve readiness state and incremental progress across steps. |
| **Pending-leaf wait path** | The LLVM task runner now blocks on registered pending leaf waits instead of immediately busy-rechecking leaf tasks in the root-task and awaited-subtask run loops. |
| **Windows pending TCP stepping** | The Windows TCP connect/send/receive leaf-step helpers now preserve per-task progress, switch sockets into non-blocking mode, return pending on would-block conditions, and resume through socket-readiness waits instead of always completing inline. |
| **Platform readiness scaffolding** | The backend now carries the Linux syscall and Windows import/link scaffolding needed for readiness waits (`epoll` syscalls on Linux; non-blocking Winsock plus `WSAPoll` support on Windows). |
| **Backend compatibility path** | The existing `HttpGet`, `HttpPost`, `NetTcpConnect`, `NetTcpSend`, `NetTcpReceive`, and `NetTcpClose` IR instructions remain intact and still call through the runtime ABI, but the public async networking surface now lowers through the dedicated leaf-task IR path instead of wrapping those instructions in coroutines. |
| **Test migration** | The HTTP and TCP end-to-end tests were rewritten to use `async`, `await`, and `Ashes.Async.run`, and compile-error coverage was added for using HTTP/TCP outside `async`. |
| **Future-features status** | `docs/future/FUTURE_FEATURES.md` now treats the async-only API surface as complete while leaving the non-blocking runtime optimization as future work. |

------------------------------------------------------------------------

## Ordered Roadmap — Next Work Items

Each roadmap section below is still incomplete. The leaf-task IR path,
the runtime ABI boundary, the task-level leaf-step ABI boundary, task
wait metadata, and the first pending-aware TCP step path are now in
place, so the remaining work is finishing the shared readiness runtime
across platforms and task combinators.

### 1. Replace blocking networking with non-blocking readiness I/O

Finish the move from "blocking call during leaf-task completion" to a
fully shared async networking runtime backed by readiness-aware I/O.

- Extend the current task wait-state metadata into a shared readiness
    registry / event-loop model so pending tasks can be driven coherently
    across `run`, `all`, and `race` rather than only through the current
    single-task wait path.
- Finish the Linux `epoll` runtime and convert Linux TCP leaf tasks off
    the current blocking helper path.
- Replace the current Windows non-blocking-socket + `WSAPoll` bridge with
    the intended IO completion port runtime.
- Finish non-blocking connect, send, receive, and close for TCP on all
    supported backends.
- Rebuild HTTP get/post on top of async TCP rather than the current
    direct blocking helper path.
- Extend readiness-driven suspension and resumption to the task
    combinators so `Ashes.Async.all` and `Ashes.Async.race` no longer
    just drive their task lists sequentially.

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
- Linux TCP/HTTP operations still use the older blocking helper path.
- HTTP leaf tasks still complete synchronously when run.
- `Ashes.Async.all` and `Ashes.Async.race` do not yet use a shared
    readiness-driven wait-any scheduler.
- No full `epoll`/IOCP networking runtime has landed yet; the current
    Windows readiness bridge uses non-blocking Winsock plus `WSAPoll`,
    and Linux has only the syscall scaffolding so far.
- The current tests do not yet cover the concurrency-focused
    deliverables for async networking.
