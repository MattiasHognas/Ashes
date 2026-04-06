# Future Features

The core language (ownership, borrowing, pattern matching, optimization)
is complete. Subsequent work focuses on runtime and ecosystem.

| Feature | Area |
|---------|------|
| Async/Await | Async syntax and core primitives |
| Async Runtime | Scheduler, concurrency and runtime support |
| Networking | HTTP and TCP layers with async |
| Package Manager | Ecosystem and dependency management |
| HTTPS/TLS | TLS, encryption, certificates and security concerns |
| Pattern Guards | Pattern matching enhancements |
| Type Annotations | User-written type annotations |
| Import Aliasing | `import Ashes.IO as IO` |
| Selective Imports | `import Ashes.IO (print)` |
| Effects / IO Types | Effect system or IO types |
| Inline Modules | Inline module declarations in source files |
| Ashes.String | Standard library string utilities |
| Ashes.Bytes | Standard library byte utilities |
| Ashes.Net.Http | Standard library HTTP module |
| Ashes.Math | Standard library math utilities |

------------------------------------------------------------------------

## Ground Rules

1. **Spec first.** Update `LANGUAGE_SPEC.md` before implementing any new
   syntax or semantic rule.
2. **Layer discipline.** Respect the project dependency graph
   (Frontend → Semantics → Backend). Runtime behaviour never goes in
   Frontend.
3. **Test every invariant.** Each feature must ship with tests that prove
   the new guarantees.
4. **No user-visible `Drop`.** `Drop` is a compiler concept. Users see
   automatic cleanup.
5. **Purity preserved.** All values are immutable. There is no mutation.
   All APIs — standard library and user-defined — are pure: they return
   new values and never modify their arguments. There are no in-place
   updates visible to user code.
6. **No GC.** All resource and memory management is deterministic and
   compile-time verified.

------------------------------------------------------------------------

# Async/Await — Implementation Plan

## Overview

Async/await adds structured concurrency to Ashes while preserving the
language's core guarantees: purity, immutability, deterministic
resource cleanup, and no GC.

The design uses **stackless coroutines** compiled into state machines.
An event-loop runtime drives non-blocking I/O. All user-visible
behaviour remains pure and expression-based.

------------------------------------------------------------------------

## 1. Surface Syntax

### 1.1 Async Expressions

An `async` block creates a suspended computation:

    let task = async
        let response = await Ashes.Http.get("http://example.com")
        in response
    in Ashes.Async.run(task)

`async <expr>` wraps an expression into a `Task(E, A)` value.
The body may contain `await` sub-expressions.

### 1.2 Await Expressions

`await` unwraps a `Task(E, A)` inside an `async` block:

    async
        let a = await taskA
        let b = await taskB
        in a + b

`await` suspends the enclosing computation until the task completes,
then binds the success value. If the awaited task fails, the error
propagates immediately (like `let?` for `Result`).

### 1.3 Async Let

`let!` is sugar for `await` in a binding position:

    async
        let! response = Ashes.Http.get("http://example.com")
        in response

This desugars to:

    async
        let response = await Ashes.Http.get("http://example.com")
        in response

### 1.4 Restrictions

- `await` may only appear inside an `async` block. Using `await`
  outside `async` is a compile-time error.
- `async` blocks are expressions and return `Task(E, A)`.
- `async` blocks may be nested. Inner `async` blocks create child
  tasks, not implicit awaits.

------------------------------------------------------------------------

## 2. Type System

### 2.1 Task Type

    Task(E, A)

A new built-in parametric type representing an asynchronous computation
that may fail with error type `E` or succeed with value type `A`.

- `Task` is an **owned type** (like `String`, `List`, closures).
- `Task` values are **not resource types** — they do not have
  use-after-close or double-close restrictions.
- The compiler inserts `Drop` for `Task` at scope exit like any
  other owned type.

### 2.2 Inference Rules

- `async <expr>` where `<expr> : A` produces `Task(E, A)`.
  `E` is unified from any `await` sub-expression.
- `await <expr>` where `<expr> : Task(E, A)` produces `A`.
  `E` unifies with the enclosing `async` block's error type.
- Functions returning `Task` are regular functions; there is no
  `async fun` modifier. `async` is an expression, not a declaration.

### 2.3 Result Interop

`Task(E, A)` and `Result(E, A)` share the same error-propagation
model. Conversions:

- `Ashes.Async.fromResult(result)` — wraps a `Result(E, A)` into
  a `Task(E, A)` that completes immediately.
- When an `async` block finishes, `Ashes.Async.run(task)` returns
  `Result(E, A)`.

------------------------------------------------------------------------

## 3. Compiler Changes

Implementation touches every compiler phase, respecting the existing
dependency graph:

    Frontend → Semantics → Backend

### 3.1 Frontend (Ashes.Frontend)

**Tokens** — add to `TokenKind` enum in `Tokens.cs`:

- `Async`
- `Await`

**Lexer** — add keyword mappings in `Lexer.GetIdentifierTokenKind()`:

- `"async"` → `TokenKind.Async`
- `"await"` → `TokenKind.Await`

**AST** — add to `Expr` in `Ast.cs`:

- `Expr.Async(Expr Body)` — async block
- `Expr.Await(Expr Task)` — await point

**Parser** — add parsing rules in `Parser.cs`:

- `ParseAsync()` — when `TokenKind.Async` is current, consume it
  and parse the body expression.
- `ParseAwait()` — when `TokenKind.Await` is current, consume it
  and parse the operand expression.
- Insert `ParseAsync` at the top of the expression precedence chain
  (same level as `let`/`match`/`if`).
- Insert `ParseAwait` at primary-expression level (prefix operator).

**Formatter** — add formatting rules in `Ashes.Formatter` for the
new AST nodes, following existing indentation patterns.

### 3.2 Semantics (Ashes.Semantics)

**Type definitions** — add to `TypeRef` in `Ir.cs`:

- No new `TypeRef` variant needed. `Task(E, A)` is represented as
  `TypeRef.TNamedType` using a new `TypeSymbol` for `Task`, with two
  type arguments, exactly like the existing `Result(E, A)`.

**Built-in registration** — add `Task` to `BuiltinRegistry.cs`
alongside `Result` and `Maybe` as a built-in ADT.

**IR instructions** — add to `IrInst` in `Ir.cs`:

- `CreateTask(int Target, int ClosureTemp)` — wraps a closure into
  a task value.
- `AwaitTask(int Target, int TaskTemp)` — suspends and resumes,
  binding the result.
- `Suspend(int StateSlot)` — saves state and yields to scheduler.
- `Resume(int StateSlot)` — restores state after scheduler wakes
  the coroutine.

**Lowering** — add to `Lowering.cs` main `LowerExpr` switch:

- `Expr.Async` → `LowerAsync()`:
  1. Lower the body into a separate `IrFunction` (the coroutine).
  2. Emit `MakeClosure` for captured bindings.
  3. Emit `CreateTask` wrapping the closure.
  4. Return the task temp with type `Task(E, A)`.

- `Expr.Await` → `LowerAwait()`:
  1. Verify we are inside an async context (tracked via a flag on
     the lowering state). Report diagnostic if not.
  2. Lower the operand expression.
  3. Emit `AwaitTask` which becomes a suspend/resume point.
  4. Return the success-value temp with type `A`.

**State machine transform** — after lowering an `async` body, run a
transform pass that:

  1. Identifies every `AwaitTask` instruction as a suspend point.
  2. Splits the coroutine into numbered states (0, 1, 2, …).
  3. Replaces the linear instruction sequence with a state-dispatch
     jump table: state 0 runs until the first `AwaitTask`, saves
     live variables, returns; state 1 restores variables and
     continues after the first `AwaitTask`, etc.
  4. Live variables across suspend points are stored in a
     heap-allocated state struct (one per coroutine instance).

**Ownership across await** — ownership rules extend naturally:

- Copy types (`Int`, `Float`, `Bool`) are saved/restored by value
  across suspend points — no special handling.
- Owned types live across an `await` must be stored in the state
  struct. The compiler tracks them for `Drop` at coroutine
  completion or cancellation.
- Resource types (`Socket`) across `await` keep their ASH006/ASH007
  compile-time checks. A socket closed before an `await` cannot be
  used after it. The same linear ownership tracking applies.
- `Borrow` instructions do not cross `await` boundaries. The
  compiler re-borrows after resume.

**New diagnostics:**

- `ASH010` — `await` used outside `async` block.
- `ASH011` — `async` block has incompatible error types across
  await points (error-type unification failure).

### 3.3 Backend (Ashes.Backend)

**Codegen for new IR instructions** — add to `EmitInstruction()` in
`LlvmCodegen.cs`:

- `CreateTask` — allocate a task struct on the heap. The struct
  contains: state index (i64), state data pointer (i8*), closure
  pointer, result slot. Emit a call to `__ashes_task_create`.

- `AwaitTask` — emit a call to `__ashes_task_await(task)`. This is
  a runtime call that either returns immediately (task already
  complete) or saves the current coroutine state and returns to the
  scheduler.

- `Suspend` / `Resume` — emit save/restore sequences for the state
  struct. Save: store each live-variable temp into the state struct
  at known offsets. Restore: load them back.

**State machine codegen** — each async coroutine compiles to a
single LLVM function with a switch on the state index:

    define i64 @__async_coroutine_0(i8* %state) {
    entry:
      %state_idx = load i64, i8* %state
      switch i64 %state_idx, label %unreachable [
        i64 0, label %state_0
        i64 1, label %state_1
        ...
      ]
    state_0:
      ; ... code until first await ...
      ; save live vars to state struct
      ; store next state index = 1
      ; return SUSPENDED
    state_1:
      ; restore live vars from state struct
      ; ... code from after first await to next await or end ...
      ; return COMPLETED
    }

**Drop integration** — when a coroutine completes or is cancelled,
emit `Drop` for all owned values still live in the state struct.
This reuses the existing `EmitDrop()` dispatch in
`LlvmCodegenBuiltins.cs`.

------------------------------------------------------------------------

## 4. Async Runtime

A minimal event-loop runtime, implemented in the backend as
compiler-emitted native code (not a C library dependency).

### 4.1 Components

**Task queue** — a simple FIFO queue of runnable coroutine pointers.
Allocated from the existing heap allocator.

**Event loop** — `__ashes_runtime_run(task)`:

    1. Push the root task onto the queue.
    2. While the queue is non-empty:
       a. Pop a task.
       b. Call the coroutine function with its state struct.
       c. If SUSPENDED: register for I/O readiness, re-enqueue
          when ready.
       d. If COMPLETED: store the result, wake any task awaiting
          this one.
    3. Return the root task's result as Result(E, A).

**I/O readiness** — platform-specific:

- **Linux**: `epoll_create1`, `epoll_ctl`, `epoll_wait` syscalls.
- **Linux ARM64**: same syscalls, different syscall numbers.
- **Windows**: `WSAPoll` or IO completion ports.

This mirrors the existing platform split in `LlvmCodegenBuiltins.cs`
for TCP syscalls.

### 4.2 Non-Blocking I/O

Existing blocking TCP and HTTP operations become async-aware:

- `Ashes.Net.Tcp.connect` — set `O_NONBLOCK` / `FIONBIO`, initiate
  connect, return a `Task(Str, Socket)` that completes when the
  socket is writable.
- `Ashes.Net.Tcp.send` — attempt write; if `EAGAIN`/`EWOULDBLOCK`,
  suspend and register for write-readiness.
- `Ashes.Net.Tcp.receive` — attempt read; if `EAGAIN`, suspend and
  register for read-readiness.
- `Ashes.Http.get` / `Ashes.Http.post` — rewritten atop async TCP.

Outside an `async` block, these operations remain synchronous
(the compiler emits a blocking call, not a task). Inside `async`,
the compiler emits the non-blocking + suspend variant.

### 4.3 Entry Point

`Ashes.Async.run(task)` — a built-in that starts the event loop and
blocks until the given task completes. Returns `Result(E, A)`.

    let result = Ashes.Async.run(
        async
            let! body = Ashes.Http.get("http://example.com")
            in body
    )
    in match result with
    | Ok(text) -> Ashes.IO.print(text)
    | Error(e) -> Ashes.IO.print("Failed: " + e)

### 4.4 Concurrency Primitives

**Parallel await** — `Ashes.Async.all(taskList)`:

    async
        let tasks = [
            Ashes.Http.get("http://a.com"),
            Ashes.Http.get("http://b.com")
        ]
        let! results = Ashes.Async.all(tasks)
        in results

Returns `Task(E, List(A))` — completes when all tasks finish.
If any task fails, the combined task fails with the first error.

**Race** — `Ashes.Async.race(taskList)`:

Returns `Task(E, A)` — completes with the first task to finish.

**Sleep** — `Ashes.Async.sleep(milliseconds)`:

Returns `Task(Str, Unit)` — suspends for the given duration.

------------------------------------------------------------------------

## 5. Standard Library Additions

### 5.1 Ashes.Async Module

| Function | Type |
|----------|------|
| `Ashes.Async.run(task)` | `Task(E, A) -> Result(E, A)` |
| `Ashes.Async.all(tasks)` | `List(Task(E, A)) -> Task(E, List(A))` |
| `Ashes.Async.race(tasks)` | `List(Task(E, A)) -> Task(E, A)` |
| `Ashes.Async.sleep(ms)` | `Int -> Task(Str, Unit)` |
| `Ashes.Async.fromResult(r)` | `Result(E, A) -> Task(E, A)` |

### 5.2 Updated Networking Signatures (inside async)

Inside `async` blocks, the existing networking functions produce
tasks instead of blocking results:

| Function (inside async) | Type |
|------------------------|------|
| `Ashes.Net.Tcp.connect(host)(port)` | `Task(Str, Socket)` |
| `Ashes.Net.Tcp.send(socket)(text)` | `Task(Str, Int)` |
| `Ashes.Net.Tcp.receive(socket)(max)` | `Task(Str, Str)` |
| `Ashes.Http.get(url)` | `Task(Str, Str)` |
| `Ashes.Http.post(url, body)` | `Task(Str, Str)` |

Outside `async`, these functions retain their current synchronous
`Result(Str, ...)` signatures for backward compatibility.

------------------------------------------------------------------------

## 6. Implementation Phases

### Phase A — Syntax and Type Checking

Add `async`/`await` tokens, AST nodes, parser rules, and type
inference for `Task(E, A)`. Async blocks lower to regular closures
(no state machine yet). `await` is a no-op that unwraps the task
synchronously. All existing tests continue to pass.

Deliverables:
- `TokenKind.Async`, `TokenKind.Await`
- `Expr.Async`, `Expr.Await`
- `Task` built-in type symbol
- `ASH010` diagnostic for `await` outside `async`
- Formatter support
- LSP support (syntax highlighting, diagnostics)
- Tests: parse round-trips, type inference, error diagnostics

### Phase B — State Machine Transform

Implement the coroutine state machine transform in Semantics.
Async bodies are split at await points. State structs are
allocated. Ownership tracking extended across suspend points.

Deliverables:
- `IrInst.CreateTask`, `IrInst.AwaitTask`, `IrInst.Suspend`,
  `IrInst.Resume`
- State machine splitter pass in Lowering
- State struct layout computation
- Ownership/Drop across suspend points
- Tests: multi-await coroutines, owned values across awaits,
  resource safety across awaits

### Phase C — Event Loop Runtime

Implement the async runtime: task queue, event loop, platform
I/O readiness (`epoll` on Linux, `WSAPoll` on Windows).

Deliverables:
- `__ashes_runtime_run` event loop
- `__ashes_task_create`, `__ashes_task_await` runtime functions
- `epoll`-based I/O registration (Linux x64 + ARM64)
- `WSAPoll`-based I/O registration (Windows)
- `Ashes.Async.run` built-in
- Tests: async sleep, async TCP echo, concurrent tasks

### Phase D — Async Networking

Convert existing blocking TCP/HTTP operations to non-blocking
variants inside `async` blocks. Add `Ashes.Async.all`,
`Ashes.Async.race`, `Ashes.Async.sleep`.

Deliverables:
- Non-blocking TCP connect/send/receive
- Async HTTP get/post atop async TCP
- `Ashes.Async.all`, `Ashes.Async.race`, `Ashes.Async.sleep`
- Tests: concurrent HTTP requests, parallel TCP connections,
  async resource cleanup, error propagation across awaits

------------------------------------------------------------------------

## 7. Design Constraints

These constraints are non-negotiable and come from the language's
core principles:

1. **Purity.** Async does not introduce mutation. Task values are
   immutable. The scheduler is invisible to user code.

2. **Deterministic cleanup.** Owned values in a coroutine state
   struct are dropped when the coroutine completes or is cancelled.
   Resource types retain ASH006/ASH007 compile-time safety.

3. **No GC.** The event loop, task queue, and state structs use the
   existing linear heap allocator. No reference counting or tracing.

4. **No coloured functions.** Functions do not have an `async`
   modifier. `async` is an expression, not a declaration. Any
   function can return a `Task` by wrapping its body in `async`.

5. **Backward compatibility.** Existing synchronous code is
   unaffected. Networking functions outside `async` blocks remain
   blocking. No existing tests break.

6. **Spec first.** Before implementing any phase, add the
   corresponding section to `LANGUAGE_SPEC.md`.
