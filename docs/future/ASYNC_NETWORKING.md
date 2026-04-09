# Async-Only TCP/HTTP API Redesign

## Goal

Remove all synchronous TCP/HTTP APIs and replace them with async-only
versions.  After this change, `Ashes.Http.get`, `Ashes.Http.post`,
`Ashes.Net.Tcp.connect`, `.send`, `.receive`, and `.close` will all
return `Task(E, A)` instead of `Result(E, A)`, and can only be used
inside `async` blocks with `await`.

## Current State

| API | Return type |
|-----|-------------|
| `Ashes.Http.get(url)` | `Result(Str, Str)` — synchronous, blocking |
| `Ashes.Http.post(url, body)` | `Result(Str, Str)` — synchronous, blocking |
| `Ashes.Net.Tcp.connect(host)(port)` | `Result(Str, Socket)` — synchronous, blocking |
| `Ashes.Net.Tcp.send(socket)(text)` | `Result(Str, Int)` — synchronous, blocking |
| `Ashes.Net.Tcp.receive(socket)(maxBytes)` | `Result(Str, Str)` — synchronous, blocking |
| `Ashes.Net.Tcp.close(socket)` | `Result(Str, Unit)` — synchronous, blocking |

## Target State

| API | Return type |
|-----|-------------|
| `Ashes.Http.get(url)` | `Task(Str, Str)` — async-only |
| `Ashes.Http.post(url, body)` | `Task(Str, Str)` — async-only |
| `Ashes.Net.Tcp.connect(host)(port)` | `Task(Str, Socket)` — async-only |
| `Ashes.Net.Tcp.send(socket)(text)` | `Task(Str, Int)` — async-only |
| `Ashes.Net.Tcp.receive(socket)(maxBytes)` | `Task(Str, Str)` — async-only |
| `Ashes.Net.Tcp.close(socket)` | `Task(Str, Unit)` — async-only |

All six APIs become intrinsics that must be called inside `async`
blocks and awaited.  There is no synchronous fallback.

------------------------------------------------------------------------

## Phase 1 — Update Language Specification (`docs/LANGUAGE_SPEC.md`)

Spec-first is a ground rule.  Update before any implementation.

1. **Section 1 (Program Structure, lines ~55-60)** — Update the builtin
   listing to show the new return types (`Task(Str, ...)` instead of
   `Result(Str, ...)`).
2. **Section 2.2 (Networking APIs, lines ~124-161)** —
   - Change all TCP return types from `Result(Str, X)` to `Task(Str, X)`.
   - Change all HTTP return types from `Result(Str, Str)` to
     `Task(Str, Str)`.
   - Add a rule: "All networking APIs return `Task` and must be used
     inside `async` blocks."
   - Remove "blocking" from the descriptions.
   - Update examples to show `async`/`await` usage.
3. **Section 15 (Builtins, lines ~1058-1063)** — Update the builtin
   summary from "blocking TCP/HTTP" to "async TCP/HTTP", change return
   types.
4. **Section 19 (Async, lines ~1528-1582)** — Verify the examples that
   show `Ashes.Http.get` are consistent with the new return types (the
   examples already `await` the HTTP call, which becomes correct once the
   function returns `Task` directly).

## Phase 2 — Update Standard Library Docs (`docs/STANDARD_LIBRARY.md`)

5. Update **Ashes.Http** section: change return types to
   `Task(Str, Str)`.
6. Update **Ashes.Net.Tcp** section: change return types to
   `Task(Str, Socket)`, `Task(Str, Int)`, `Task(Str, Str)`,
   `Task(Str, Unit)`.
7. Add note: "All networking APIs are async-only and must be awaited
   inside `async` blocks."

## Phase 3 — Semantics: Builtin Registry (`src/Ashes.Semantics/BuiltinRegistry.cs`)

8. **HTTP/TCP dispatch** — Currently `Http`/`Tcp` are `BuiltinValueKind`
   entries dispatched as qualified builtin calls.  Two options:

   *Option A (recommended):* Keep `BuiltinValueKind` entries as-is
   (`HttpGet`, `HttpPost`, `NetTcpConnect`, etc.) and change their
   lowering methods to wrap the existing blocking IR in a
   coroutine/task.

   *Option B:* Move them to `IntrinsicKind` so they flow through the
   intrinsic lowering path.

9. **Update type bindings** — In the `CreateXxxBinding()` methods in
   `Lowering.cs`, change return types from `Result(Str, X)` to
   `Task(Str, X)`.

## Phase 4 — Semantics: Lowering (`src/Ashes.Semantics/Lowering.cs`)

This is the most complex phase.  Each of the 6 APIs must produce
`Task(E, A)` instead of `Result(E, A)`.

**Recommended approach (Option A):** Each API becomes an intrinsic that
creates an async block internally (like `async { <blocking-call> }`).
The blocking IR instruction stays the same at codegen level; the
lowering wraps it in `CreateTask` + coroutine.  The caller must `await`
it inside their own `async` block.

This achieves the API design goal (async-only, no sync alternatives)
without requiring a full event loop rewrite.  The blocking syscalls
still happen, but inside a task coroutine.  The event loop can be
optimized later.

Implementation per API:

10. **`LowerHttpGet`** — Change return type from `Result(Str, Str)` to
    `Task(Str, Str)`.  Wrap the `HttpGet` IR instruction inside a
    coroutine body that produces a `CreateTask`.
11. **`LowerHttpPost`** — Same pattern with `HttpPost`.
12. **`LowerNetTcpConnect`** — Wrap `NetTcpConnect` IR in a task
    returning `Task(Str, Socket)`.
13. **`LowerNetTcpSend`** — Wrap `NetTcpSend` IR in a task returning
    `Task(Str, Int)`.
14. **`LowerNetTcpReceive`** — Wrap `NetTcpReceive` IR in a task
    returning `Task(Str, Str)`.
15. **`LowerNetTcpClose`** — Wrap `NetTcpClose` IR in a task returning
    `Task(Str, Unit)`.
16. **Update `CreateXxxBinding()` methods** — Each of the 6 methods must
    return `Task(Str, X)` instead of `Result(Str, X)` in their type
    scheme.
17. **Enforce async-only usage** — Add a compiler check (new diagnostic,
    e.g. `ASH011`) that TCP/HTTP calls outside `async` blocks produce a
    compile-time error.

## Phase 5 — Semantics: Supporting Changes

18. **`ProjectSupport.CollectReferencedNames`** — Verify all `Expr`
    types are handled (any new AST nodes need a case here).
19. **`IrOptimizer.CollectUsedTemps` / `StateMachineTransform`** —
    Verify the new task-wrapped IR instructions are handled in optimizer
    and state machine passes.
20. **Type checker** — When HTTP/TCP returns `Task(Str, X)`, the `await`
    of that value should correctly produce `X` via existing Task/await
    unification.

## Phase 6 — Backend: Codegen (No changes needed for Option A)

21. **No IR instruction changes** — The existing `HttpGet`, `HttpPost`,
    `NetTcpConnect`, `NetTcpSend`, `NetTcpReceive`, `NetTcpClose` IR
    instructions remain.  They still perform blocking I/O but are now
    always emitted inside a coroutine function.
22. **Verify codegen works inside coroutines** — The blocking network
    instructions must work correctly when emitted inside a coroutine
    function (called from `EmitRunTask`'s event loop).  The coroutine is
    a regular LLVM function, so standard blocking calls should work fine.

## Phase 7 — Update All Tests

23. **Update TCP tests** (`tcp_connect_success.ash`,
    `tcp_connect_localhost.ash`, `tcp_close.ash`, `tcp_send.ash`,
    `tcp_receive.ash`, `tcp_receive_bounded.ash`,
    `tcp_connect_invalid_host.ash`) — Wrap all TCP calls in `async`
    blocks, `await` each operation, run with `Ashes.Async.run`, update
    expected output where needed.
24. **Update HTTP tests** (`http_get.ash`, `http_post.ash`,
    `http_https_not_supported.ash`, `http_informational_status.ash`,
    `http_non_success_status.ash`) — Wrap in `async` blocks, `await`
    each HTTP call, run with `Ashes.Async.run`.
25. **Update compile-error tests** (`tcp_send_after_close.ash`,
    `tcp_receive_invalid_max_bytes.ash`) — Update to use async/await
    while still triggering the same errors.
26. **Update async tests that use HTTP** (e.g. `async_basic.ash`) —
    Verify they still work; the `let!` examples already show
    `await Ashes.Http.get(...)`.
27. **Add new test** `tcp_connect_outside_async.ash` — Verify calling
    `Ashes.Net.Tcp.connect` outside `async` produces compile error
    `ASH011`.
28. **Add new test** `http_get_outside_async.ash` — Verify calling
    `Ashes.Http.get` outside `async` produces compile error `ASH011`.

## Phase 8 — Format All `.ash` Files

29. Run `dotnet run --project src/Ashes.Cli -- fmt -w tests/` on all
    modified test files.

## Phase 9 — Update Future Features Doc

30. Update `docs/FUTURE_FEATURES.md`: Mark "Async Networking" API
    surface as complete.  The remaining future work is the event loop
    optimisation (replacing blocking syscalls with non-blocking +
    `epoll`/IOCP).

------------------------------------------------------------------------

## File Impact Summary

| File | Change type |
|------|-------------|
| `docs/LANGUAGE_SPEC.md` | Return types, examples, rules |
| `docs/STANDARD_LIBRARY.md` | Return types, async note |
| `docs/FUTURE_FEATURES.md` | Status update |
| `src/Ashes.Semantics/BuiltinRegistry.cs` | Possibly move to `IntrinsicKind` |
| `src/Ashes.Semantics/Lowering.cs` | Major — wrap 6 APIs in Task, new diagnostic, updated type bindings |
| `src/Ashes.Semantics/Ir.cs` | Possibly new IR variant or no change |
| `src/Ashes.Semantics/Diagnostics.cs` | New `ASH011` diagnostic |
| `tests/*.ash` | ~15 test files updated + 2 new error tests |

## Risk Assessment

- **Low risk** — Spec + doc changes, test updates, formatter.
- **Medium risk** — Lowering changes: wrapping blocking IR in coroutine
  tasks requires careful handling of the state machine transform,
  capture analysis, and scope isolation.
- **Key invariant** — The blocking network syscalls must work correctly
  when emitted inside coroutine functions.  The coroutine is just a
  regular function pointer called from `EmitRunTask`, so standard
  blocking calls should work fine.
- **Socket ownership** — Socket is a resource type with Drop semantics.
  When wrapped in a task the socket's lifetime must be properly tracked.
  The socket is created inside the coroutine and returned as the task
  result; ownership transfers to the caller upon `await`.

## Implementation Order

1. Spec first (Phases 1–2)
2. Semantics (Phases 3–5) — all 6 APIs change together
3. Backend verification (Phase 6)
4. Tests (Phases 7–8)
5. Docs cleanup (Phase 9)
6. Build + test verification
