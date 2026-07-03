# Future: Server Support

## Status: Design exploration

This document captures the current direction for adding long-running server support to Ashes. It is intentionally high-level and focuses on language design rather than implementation details.

The implementation should be derived by studying the existing compiler architecture (parser, semantic analysis, async lowering, IR, state machine transformation, runtime and backend) rather than following any assumptions in this document.

---

## Goal

Ashes is intentionally built around programs that have:

* a clear beginning,
* a final expression,
* and eventual termination.

Server support should preserve this philosophy.

A server should not become a fundamentally different kind of Ashes program.

Instead, a server should simply be a long-running expression whose result represents the lifecycle of the listener.

The program still terminates.

It just terminates when the server stops.

---

## General Idea

The important distinction is between two completely different kinds of results.

Request / connection result

Each incoming request (HTTP) or connection/session (TCP) has its own result.

For example:

```ash
Ok(response)
```
```ash
Error(NotFound)
```

These are local to that request.

The server consumes these values to generate an appropriate response.

They do not terminate the server.

---

## Server lifecycle result

The server itself eventually completes with its own result.

For example:

```ash
Ok(())
```

meaning

* graceful shutdown
* explicit stop
* cancellation

or

```ash
Error(...)
```

meaning

* bind failure
* listener failure
* unrecoverable runtime failure
* etc.

This is the value produced by the final program expression.

---

## HTTP Example

```ash
import Ashes.Http.Server as http
import Ashes.IO as io
type AppError =
    | NotFound
let handle req =
    match http.path(req) with
        | "/health" ->
            Ok(http.text(200)("ok"))
        | _ ->
            Error(NotFound)
let render err =
    match err with
        | NotFound ->
            http.text(404)("not found")
in
    match await http.serve(8080)(handle)(render) with
        | Ok(()) ->
            io.print("server stopped")
        | Error(e) ->
            io.print(e)
```

Conceptually:

Request
    ↓
handle
    ↓
Result<AppError, Response>
    ↓
render (only on Error)
    ↓
Response

The outer match is not about requests.

It is only about the server lifecycle.

---

## HTTP Handler With Async Work

The handler should naturally support await just like any other Ashes function.

Example:

```ash
let handle req =
    match await users.find(42) with
        | Ok(user) ->
            Ok(http.json(user))
        | Error(_) ->
            Error(NotFound)
```

The exact lowered/inferred type should follow the compiler’s existing async model.

There should ideally not be a separate serveAsync API unless the implementation genuinely requires one.

---

TCP Example

```ash
import Ashes.Net.Tcp.Server as tcp
import Ashes.IO as io
type ClientError =
    | ReceiveFailed(Str)
    | SendFailed(Str)
let handle client =
    match await tcp.receive(client)(4096) with
        | Ok(message) ->
            match await tcp.send(client)("echo: " + message) with
                | Ok(()) ->
                    Ok(())
                | Error(e) ->
                    Error(SendFailed(e))
        | Error(e) ->
            Error(ReceiveFailed(e))
let render err =
    match err with
        | ReceiveFailed(msg) ->
            io.print("receive failed: " + msg)
        | SendFailed(msg) ->
            io.print("send failed: " + msg)
in
    match await tcp.serve(9000)(handle)(render) with
        | Ok(()) ->
            io.print("server stopped")
        | Error(e) ->
            io.print(e)
```

For persistent protocols the handler would naturally become recursive.

No explicit loop construct should be required.

---

## Library Before Syntax

The first implementation should preferably avoid introducing new language syntax.

Instead, expose server support through libraries.

Examples:

http.serve(8080)(handle)(render)
tcp.serve(9000)(handle)(render)

Once the feature has matured, syntax sugar can always be introduced later if it genuinely improves readability.

---

## High-Level Compiler Direction

This document intentionally avoids describing implementation details.

However, the overall direction is expected to be:

* extend networking support with server-side primitives;
* reuse the existing async/state-machine infrastructure;
* represent a server as a long-running task;
* let request handlers execute using the existing async model;
* keep the outer program model unchanged.

The implementation should be derived from the existing compiler architecture rather than introducing a separate execution model.

---

## Desired Mental Model

A normal program looks like:

input
    ↓
processing
    ↓
output

A server instead becomes:

start listener
      ↓
accept work
      ↓
process work
      ↓
produce response
      ↓
repeat
     (or)
shutdown

Only the final shutdown produces the value of the overall program.

---

## Open Questions

These are intentionally left unresolved.

Error rendering

Should handlers return

Result<AppError, Response>

with a separate renderer,

or should handlers always construct a concrete response themselves?

---

## Handler failures

Should a handler error only affect that request,

or should certain failures stop the server?

---

## Graceful shutdown

How should shutdown be initiated?

Possibilities include:

* OS signals
* cancellation
* explicit stop handle
* all of the above

---

## Memory lifetime

Long-running processes require careful management of allocation lifetimes.

The implementation should ensure request processing does not cause unbounded memory growth.

---

## Networking layers

Should the implementation begin with

* TCP server support

and build HTTP on top,

or introduce HTTP directly?

---

## Future syntax

If server support proves successful as a library API,

would dedicated language syntax provide meaningful value,

or simply duplicate functionality already expressed naturally through existing Ashes constructs?

One thing I would add after the implementation exists is a short rationale explaining why server support was designed as a long-running expression rather than introducing a new top-level language construct. That philosophy seems consistent with the rest of Ashes and would help future contributors understand the decision.
