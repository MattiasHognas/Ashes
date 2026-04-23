# Async Networking — Status & Roadmap

Async-only TCP/HTTP is now part of the language surface.
The public APIs return `Task(E, A)` and must be used inside `async`
blocks with `await`.

The current implementation now includes explicit task wait metadata, a
pending-leaf wait path in the task runner, shared task-list scheduling
for `Ashes.Async.all` / `race`, staged HTTP leaf tasks built on top of
TCP leaf tasks, non-blocking TCP stepping on Linux and Windows, and
deterministic fixture coverage for cleanup, error propagation, and
networking under task combinators. The remaining roadmap is now focused
on replacing the current Windows `WSAPoll` bridge with the intended IO
completion port runtime.

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
| **Linux pending TCP stepping** | The Linux TCP connect/send/receive leaf-step helpers now preserve per-task progress, switch sockets into non-blocking mode, return pending on would-block conditions, and resume through `epoll` readiness waits instead of completing through the older blocking helper path. |
| **Windows pending TCP stepping** | The Windows TCP connect/send/receive leaf-step helpers now preserve per-task progress, switch sockets into non-blocking mode, return pending on would-block conditions, and resume through socket-readiness waits instead of always completing inline. |
| **Platform readiness scaffolding** | The backend now carries the Linux syscall and Windows import/link scaffolding needed for readiness waits (`epoll` syscalls on Linux; non-blocking Winsock plus `WSAPoll` support on Windows). |
| **Shared combinator scheduler** | `Ashes.Async.all` and `Ashes.Async.race` now step tasks incrementally through shared runtime helpers that can drive arbitrary tasks until they are pending or complete, then wait on any registered pending task in the list before resuming the scan. |
| **Staged HTTP leaf tasks** | HTTP get/post leaf tasks no longer call the old direct blocking helper path; they now advance through connect, send, receive, and close stages on top of TCP leaf tasks and parse the final HTTP response once the staged transport work completes. |
| **Backend compatibility path** | The existing `HttpGet`, `HttpPost`, `NetTcpConnect`, `NetTcpSend`, `NetTcpReceive`, and `NetTcpClose` IR instructions remain intact and still call through the runtime ABI, but the public async networking surface now lowers through the dedicated leaf-task IR path instead of wrapping those instructions in coroutines. |
| **Test migration** | The HTTP and TCP end-to-end tests were rewritten to use `async`, `await`, and `Ashes.Async.run`, and compile-error coverage was added for using HTTP/TCP outside `async`. |
| **Concurrency-focused fixture tests** | The socket fixture suite now covers async cleanup after `Tcp.close`, HTTP error propagation across `await`, and deterministic HTTP composition under `Ashes.Async.all` / `Ashes.Async.race`. |
| **Full verification rerun** | `Ashes.Tests`, `Ashes.Lsp.Tests`, the full `.ash` suite, and `dotnet format Ashes.slnx --verify-no-changes` were rerun after the runtime and test changes and now pass together. |
| **Future-features status** | `docs/future/FUTURE_FEATURES.md` now treats the async-only API surface as complete while leaving the non-blocking runtime optimization as future work. |

------------------------------------------------------------------------

## Ordered Roadmap — Next Work Items

The public async networking surface, cross-platform non-blocking TCP
leaf stepping, combinator behavior, and current verification gaps are
now addressed. The only substantial runtime item still open is swapping
the current Windows readiness bridge for the intended IO completion port
implementation.

### 1. Replace the Windows `WSAPoll` bridge with an IOCP runtime

The current Windows backend uses non-blocking Winsock sockets together
with `WSAPoll` to wait for readiness. That is sufficient for the current
leaf-task runtime, but it is still a bridge rather than the intended
completion-port-based implementation.

- Replace the current Windows non-blocking-socket + `WSAPoll` bridge with
    the intended IO completion port runtime.
- Preserve the current task-header wait metadata and shared task-list
    scheduler surface while moving the Windows wait backend underneath it.
- Re-run the full verification stack once the IOCP backend lands:
    `Ashes.Tests`, `Ashes.Lsp.Tests`, `Ashes.Cli -- test tests`, and
    `dotnet format Ashes.slnx --verify-no-changes`.

------------------------------------------------------------------------

## Explicitly Deferred

The following items are not implemented yet and should not be described
as done:

- The current runtime ABI symbols are compiler-emitted helper functions,
    not a separately packaged shared runtime yet.
- No Windows IO completion port runtime has landed yet; the current
    Windows readiness bridge still uses non-blocking Winsock plus
    `WSAPoll`.
