# Ashes Standard Library

This document summarizes the current compiler-provided standard-library surface.
It is user-facing guidance for the shipped modules; the authoritative language
semantics remain in the [Language Reference](language.md).

## Built-in Runtime Types

These types are always available without imports:

- `Unit`
- `Maybe(T)` with constructors `None` and `Some(T)`
- `Result(E, A)` with constructors `Error(E)` and `Ok(A)`
- `List<T>` as the type shown for list syntax and list values
- `Socket`
- `TlsSocket`
- `Process`
- `FileHandle`

## Module Overview

The standard library is organized under ten top-level namespaces: `Ashes.IO` (console, file, and
process I/O), `Ashes.Net` (networking and wire protocols), `Ashes.Number` (numeric helpers),
`Ashes.Collection` (containers), `Ashes.Text` (strings and text formats), `Ashes.Byte` (binary
data), `Ashes.Task` (concurrency), `Ashes.Core` (core value helpers), `Ashes.Test` (assertions),
and the internal-only `Ashes.Internal`. Some modules are compiler intrinsics, some are shipped
Ashes code from `lib/Ashes/`, and some are both; that distinction is an implementation detail —
imports work identically for all of them.

## `Ashes.IO` — console, file, and process I/O

### `Ashes.IO`

- `print(value)` returning `Unit` — write a printable scalar (`Int`, `Str`, or `Bool`) to stdout, followed by a newline
- `panic(message)` returning `a` — print `message` and abort the program; never returns, so it is usable at any type
- `args` returning `List(Str)` — the command-line arguments passed to the program
- `write(text)` returning `Unit` — write `text` to stdout with no trailing newline
- `writeBytes(bytes)` returning `Unit` — write a raw `Bytes` buffer to stdout verbatim, with no trailing
  newline and no UTF-8 constraint (unlike `write`, which takes a `Str`). Use this for binary output such
  as a packed image or any non-text byte stream
- `writeLine(text)` returning `Unit` — write `text` to stdout followed by a newline
- `readLine()` returning `Maybe(Str)`
- `readExact(n)` returning `Result(Str, Str)` — read exactly `n` bytes from stdin

### `Ashes.IO.Console`

Interactive terminal input for real-time programs (games, TUIs): raw keyboard/mouse byte
streams and a monotonic clock for frame pacing. Rendering needs no dedicated support —
ANSI escape output through `Ashes.IO.write` already works; this module is about *input*,
which is line-buffered and blocking without it.

- `enableRawInput()` returning `Bool` — switch stdin to raw mode: no line buffering, no echo,
  no signal keys (press-by-press delivery; `Ctrl-C` arrives as byte `0x03` for the program to
  handle). On Linux this saves and rewrites the termios state; on Windows it saves and rewrites
  the console input mode and enables VT input sequences (arrow keys and mouse arrive as the same
  escape bytes as on Linux), and enables VT processing on stdout so ANSI rendering works in the
  classic console host. Returns `false` (and changes nothing) when stdin is not a terminal —
  for example when input is piped, as in tests and CI — so programs can fall back or exit
  cleanly.
- `restoreInput()` returning `Unit` — restore the mode saved by `enableRawInput`. A no-op if
  raw mode was never enabled. Programs must call this before exiting; the runtime does not
  restore the terminal on its own.
- `pollInput(timeoutMs)` returning `Maybe(Str)` — wait up to `timeoutMs` milliseconds for
  input and return whatever bytes are pending: `Some(bytes)` when input arrived (possibly
  several keys, or a partial escape sequence — accumulate and decode in the program),
  `Some("")` when the timeout elapsed with no input, `None` when stdin is closed (end of
  input on a pipe). A `timeoutMs` of `0` is a non-blocking check. In raw mode each keypress
  arrives immediately; arrow keys arrive as `ESC [ A` … `ESC [ D`, and mouse reporting
  (opted into by writing the standard `ESC [ ? 1003 h` / `ESC [ ? 1006 h` sequences) arrives
  as SGR `ESC [ < b ; x ; y M/m` sequences. Decoding is ordinary string processing. On
  Windows, when stdin is a pipe rather than a console, the timeout is not honored: the call
  reads directly and blocks until bytes or end of input arrive (pipes are not waitable
  objects; on Linux `ppoll` covers pipes and ttys alike).
- `monotonicMillis()` returning `Int` — milliseconds on a monotonic clock (unaffected by wall
  clock changes; `CLOCK_MONOTONIC` on Linux, `GetTickCount64` on Windows). The zero point is
  arbitrary; use differences for frame pacing and timeouts.

Supported on Linux x64, Linux arm64, and Windows x64.

### `Ashes.IO.File`

- `readText(path)` returning `Result(Str, Str)` — UTF-8-validated; caps at 1 MiB.
- `readAllBytes(path)` returning `Result(Str, Bytes)` — read a whole file into a `Bytes` with no UTF-8
  validation. Uncapped on Linux (the buffer is a standalone `mmap`, so it can exceed one arena chunk);
  on Windows it currently shares the `readText` buffer and so caps at the same 1 MiB. The buffer is
  read-only and program-lifetime; fields sliced from it (`Bytes.subText`) are copied into the arena.
- `mmap(path)` returning `Result(Str, Bytes)` — memory-map a file read-only and return a **zero-copy**
  `Bytes` **view** over the mapping (no read, no copy). On Linux the pages fault in on access, so a
  data-parallel fold that touches different chunks faults them in **in parallel**, and the mapping is
  shared read-only across worker threads. The mapping is program-lifetime, so slices/views into it stay
  valid. On Windows this falls back to the capped `readAllBytes` read. Preferred over `readAllBytes` for
  random-access / chunked processing (e.g. `challenges/1brc/brc.ash`).
- `writeText(path, text)` returning `Result(Str, Unit)`
- `writeBytes(path, bytes)` returning `Result(Str, Unit)`
- `exists(path)` returning `Result(Str, Bool)`
- `open(path)` returning `Result(Str, FileHandle)` — open a file for reading; the handle is a
  resource type, automatically closed when it goes out of scope.
- `readChunk(fh)(maxBytes)` returning `Result(Str, Str)` — read up to `maxBytes` bytes;
  returns `Ok("")` at end of file. Lets a large file be streamed without loading it whole (cf.
  `readText`, which allocates the entire file at once).
- `readLine(fh)` returning `Maybe(Str)` — read one line (the trailing `\n`, and a preceding
  `\r`, are stripped) through a refillable module-global buffer; returns `None` at end of file. Unlike
  `readChunk` it threads no buffer state through the caller, so a whole-file fold can be a **single**
  loop carrying only its accumulator — which is what keeps such a fold constant-memory (a per-chunk
  re-entry structure re-copies a reuse accumulator each chunk). The buffer is guarded by the handle it
  holds: calling `readLine` on a different handle resets it (any read-ahead for the previous handle is
  discarded), so it is for reading one file to completion, not interleaving line-reads across handles.
- `close(fh)` returning `Result(Str, Unit)` — close explicitly (also automatic on scope exit).

### `Ashes.IO.Process`

Synchronous subprocess control with piped stdin/stdout/stderr.
`Process` is a resource type; it is automatically closed when it goes out of scope.

- `spawn(exe)(args)` returning `Result(Str, Process)` — launch `exe` with argument list `args`
- `writeStdin(proc)(text)` returning `Unit` — write bytes to the process's stdin pipe
- `readStdoutLine(proc)` returning `Maybe(Str)` — read one line from stdout (`None` on EOF)
- `readStderrLine(proc)` returning `Maybe(Str)` — read one line from stderr (`None` on EOF)
- `waitForExit(proc)` returning `Int` — block until the process exits, return its exit code
- `kill(proc)` returning `Unit` — send SIGTERM (Linux) or `TerminateProcess` (Windows)

Supported on Linux x64, Linux arm64, and Windows x64.

## `Ashes.Net` — networking and wire protocols

### `Ashes.Net.Http`

- `get(url)` returning `Task(Str, Str)`
- `post(url, body)` returning `Task(Str, Str)`

All networking APIs return `Task(...)` and are consumed with `await`.

Current HTTP support is intentionally small:

- `http://` and `https://` URLs are supported.
- `https://` defaults to port 443 and currently ships on the Linux x64,
  Linux arm64, and Windows x64 backends via the hermetic Mbed TLS
  runtime linked into TLS-using executables.
- Other backends may still return a runtime error for `https://` until their
  TLS runtime support lands.
- Responses are expected to be plain HTTP/1.1 responses terminated by connection close.
- `Transfer-Encoding: chunked` currently returns `Error("unsupported transfer encoding")`.

### `Ashes.Net.Http.Server`

A minimal HTTP/1.1 server layered over `Ashes.Net.Tcp.Server`. Pure Ashes over the TCP layer, so it
runs on every target the TCP server does (Linux x64, Linux arm64, Windows x64).

- `HttpRequest` — a parsed request (method, path, headers, body).
- `HttpResponse` — a response (status, headers, body).
- `method(req)` — `HttpRequest -> Str`, the request method (e.g. `"GET"`).
- `target(req)` — `HttpRequest -> Str`, the raw request target (`"/users?id=42"`, path plus query).
- `path(req)` — `HttpRequest -> Str`, the path with any `?query` stripped (for routing).
- `query(req)` — `HttpRequest -> Str`, the raw query string (after `?`), or `""`.
- `queryParam(req)(name)` — `HttpRequest -> Str -> Maybe(Str)`, a query parameter's **percent-decoded**
  value looked up by (decoded) name; a bare key yields `Some("")`, absent yields `None`.
- `percentDecode(s)` — `Str -> Str`, decode `%XX` and `+` (byte-accurate); also usable on the path.
- `body(req)` — `HttpRequest -> Str`, the request body (`Content-Length`- or chunked-framed).
- `header(req)(name)` — `HttpRequest -> Str -> Maybe(Str)`, the value of a request header, matched
  **case-insensitively** by name, or `None`.
- `rawHeaders(req)` — `HttpRequest -> Str`, the raw `Name: value`-per-line header block, for callers
  that want to scan it directly.
- `text(status)(body)` — a response with `Content-Type: text/plain; charset=utf-8`.
- `json(status)(body)` — a response with `Content-Type: application/json`.
- `respond(status)(headerBlock)(body)` — a response with an explicit header block (`"Name: value\r\n"`
  per line, or `""` for none).
- `withHeader(name)(value)(response)` — add a response header. `Content-Length` and `Connection` are
  always set by the server and must not be added here.
- `streamed(status)(headerBlock)(seed)(step)` — a **streaming** response. The body is produced
  incrementally: `step : Str -> Task(E, StreamStep)` is pulled starting from `seed`, and each
  `StreamChunk(bytes, nextAcc)` is written as one `Transfer-Encoding: chunked` frame (the producer
  runs async, so a chunk may come from a file, a socket, or a computation); `StreamDone` ends the
  body. The response carries `Transfer-Encoding: chunked` instead of `Content-Length`. `StreamStep`
  is `| StreamChunk(Str, Str) | StreamDone`.
- `serve(port)(handler)` — `Int -> (HttpRequest -> Task(E, HttpResponse)) -> Task(Str, Unit)`. Binds
  the port and, per request, reads it, parses request line + headers + body, runs the handler (which
  may `await` async work), and writes the response. The connection is kept alive (HTTP/1.1 default),
  closing on `Connection: close`, on handler failure, or when the peer disconnects. A handler that
  completes with `Error` yields a plain `500`. Consumed with `Ashes.Task.run`; serves connections
  concurrently like the plaintext TCP server.

Reads are **buffered** until a full request has arrived — the header block plus the body (framed by
`Content-Length` or `Transfer-Encoding: chunked`) — so requests larger than one read and slow/split
requests are handled. A request is capped at **8 MiB**: a declared `Content-Length` over the cap is
rejected with `413 Payload Too Large` on the header (before the body is buffered), and an unbounded
chunked/no-length stream is capped by the buffered size. Streaming a request body *into* the handler
(rather than buffering it) is the one deferred piece, waiting on the resource-safety/ownership work
that would check the reader's non-escape guarantee.

```ash
import Ashes.IO
import Ashes.Net.Http.Server
import Ashes.Task
let route req =
    async(match Ashes.Net.Http.Server.path(req) with
        | "/health" -> Ashes.Net.Http.Server.text(200)("ok")
        | "/echo" -> Ashes.Net.Http.Server.text(200)(Ashes.Net.Http.Server.body(req))
        | "/data" -> Ashes.Net.Http.Server.json(200)("{\"ok\":true}")
        | _p -> Ashes.Net.Http.Server.text(404)("not found"))
in match Ashes.Task.run(Ashes.Net.Http.Server.serve(8080)(route)) with
    | Ok(_u) -> Ashes.IO.print("stopped")
    | Error(e) -> Ashes.IO.print(e)
```

### `Ashes.Net.Tcp`

- `connect(host)(port)` returning `Task(Str, Socket)`
- `send(socket)(text)` returning `Task(Str, Int)`
- `receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `close(socket)` returning `Task(Str, Unit)`

### `Ashes.Net.Tcp.Server`

TCP server support. `listen`/`accept` are the primitives; `serve` is the combinator built on them.

- `listen(port)` returning `Task(Str, Socket)` — bind `INADDR_ANY:port`, `listen`, and return the
  listening socket (non-blocking).
- `accept(listener)` returning `Task(Str, Socket)` — accept one connection, returning the client
  socket. Suspends (parks on the listener) until a connection is ready, so it is used inside `async`.
- `serve(port)(handler)` returning `Task(Str, Unit)` — the server lifecycle. Binds the port, then
  loops accepting connections, spawning `handler` on each (`Ashes.Task.spawn`), so connections are
  served concurrently: a slow handler never blocks the accept loop. `handler : Socket -> Task(E, A)`
  owns its connection and runs detached — it must close the socket itself, its result is dropped, and
  its failure is isolated to its connection, never stopping the loop. A bind/listener failure ends the
  server with `Error`. Consumed with `await` inside `Ashes.Task.run(async ...)`. **`serve` is
  parallel by default**: it runs one independent reactor per online CPU (see below), so an endpoint
  scales across cores without the program choosing a worker count.
- `serveParallel(port)(workers)(handler)` — the same as `serve` with an explicit worker count
  (`serve` is `serveParallel(port)(0)(handler)`; a count `<= 0` means one worker per online CPU).
- `serveWithDrainTimeout(port)(drainMs)(handler)` — the same as `serve` with an explicit
  graceful-shutdown drain bound. On the first `SIGINT`/`SIGTERM` (console-ctrl on Windows) the
  server stops accepting and lets in-flight handlers finish for up to `drainMs` milliseconds
  (default 10000 for `serve`), then returns `Ok(())`; a second signal exits immediately. A
  multi-reactor parent forwards the signal to its workers and reaps them before returning.
- `setDrainTimeout(ms)` returning `Unit` — sets the drain bound for this process (the primitive
  under `serveWithDrainTimeout`; call before `serve` so forked workers inherit it).

Programmatic shutdown is the built-in `Stop.stop(Unit)` capability operation (not a
`Ashes.Net.Tcp.Server` function): performing it from inside a handler requests graceful shutdown
of the whole server through the same drain path as a signal, and the server's `serve` lifecycle
completes with `Ok(())`. See the language reference, section 20.8.

`serve` is a **fork-based multi-reactor**: it forks one reactor process per online CPU up front, each
binding the port with `SO_REUSEPORT` so the kernel load-balances new connections across the workers,
and each worker serves its connections concurrently on its own thread (cooperative scheduling via
`Ashes.Task.spawn`; each spawned handler gets a private arena, freed when it completes, so memory
stays bounded under sustained load). Because the workers are separate processes and Ashes is pure, the
connections are genuinely independent — there is no shared mutable state, and equally no cross-worker
aggregation. The worker count defaults to the online-CPU count and honors the `--parallel-workers`
compile cap. Multi-core on all three targets: Linux (x64/arm64) forks the workers with a
`SO_REUSEPORT` listener each; Windows relaunches itself with `CreateProcessA` sharing one inherited
listener. `send` / `receive` / `close` from `Ashes.Net.Tcp` operate on the accepted client socket.
Supported on Linux x64, Linux arm64, and
Windows x64 (the accept path uses `WSAPoll` on Windows, matching the client).

```ash
import Ashes.Net.Tcp
import Ashes.Net.Tcp.Server
let onClient client =
    async(match await Ashes.Net.Tcp.receive(client)(4096) with
        | Error(e) -> Error(e)
        | Ok(msg) ->
            match await Ashes.Net.Tcp.send(client)(msg) with
                | Error(e2) -> Error(e2)
                | Ok(_n) -> await Ashes.Net.Tcp.close(client))
in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve(8080)(onClient)) with
    | Ok(_u) -> Ashes.IO.print("server stopped")
    | Error(e) -> Ashes.IO.print(e)
```

### `Ashes.Net.Tls`

- `connect(host)(port)` returning `Task(Str, TlsSocket)`
- `send(socket)(text)` returning `Task(Str, Int)`
- `receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `close(socket)` returning `Task(Str, Unit)`

`Ashes.Net.Tls` uses the same TLS runtime path as `https://` in `Ashes.Net.Http`.
On Linux x64, Linux arm64, and Windows x64 that currently means the
hermetic Mbed TLS runtime linked into each TLS-using executable. No
external OpenSSL installation is required. Hostname verification and
system-trust validation are mandatory for successful TLS connections.

### `Ashes.Net.Tls.Server`

Server-side TLS termination, layered on `Ashes.Net.Tcp.Server`.

- `handshake(socket)(certPem)(keyPem)` returning `Task(Str, TlsSocket)` — runs the server half of a
  TLS handshake over an accepted TCP socket. `certPem` / `keyPem` are the certificate-chain and
  private-key PEM **contents** (not paths). The TLS server config is built once from these and
  cached for the process; the accepted socket is consumed into the returned `TlsSocket`, on which the
  ordinary `Ashes.Net.Tls.send` / `receive` / `close` operate.
- `serveTls(port)(certPath)(keyPath)(handler)` returning `Task(Str, Unit)` — the TLS server
  lifecycle. Reads the certificate and key PEM files up front (a read failure ends the server with a
  readable `Error`), then serves like `Ashes.Net.Tcp.Server.serve` — concurrently, one spawned
  handler per connection — running the handshake in front of each handler. `handler : TlsSocket ->
  Task(E, A)` owns its connection and must close it (the socket auto-drops otherwise). Consumed with
  `Ashes.Task.run`.

The handshake reuses the scheduler's `WaitTlsWantRead` / `WaitTlsWantWrite` parking, so a TLS server
serves concurrently on a single thread exactly as the plaintext server does. Same three targets and
hermetic Mbed TLS runtime as the TLS client.

```ash
import Ashes.IO
import Ashes.Net.Tls
import Ashes.Net.Tls.Server
import Ashes.Task
let onClient tls =
    async(match await Ashes.Net.Tls.receive(tls)(4096) with
        | Error(e) -> Error(e)
        | Ok(msg) ->
            match await Ashes.Net.Tls.send(tls)("echo: " + msg) with
                | Error(e2) -> Error(e2)
                | Ok(_n) -> await Ashes.Net.Tls.close(tls))
in match Ashes.Task.run(Ashes.Net.Tls.Server.serveTls(8443)("cert.pem")("key.pem")(onClient)) with
    | Ok(_u) -> Ashes.IO.print("stopped")
    | Error(e) -> Ashes.IO.print(e)
```

### `Ashes.Net.Rpc`

Stdio JSON-RPC 2.0 Content-Length framing for LSP/DAP transports.

- `readMessage()` returning `Result(Str, Str)` — read one framed message from stdin (reads the `Content-Length:` header, then exactly that many bytes via `Ashes.IO.readExact`)
- `writeMessage(msg)` returning `Unit` — write a framed message to stdout with a `Content-Length:` header

## `Ashes.Number` — numbers

### `Ashes.Number.Math`

All functions are curried. Layer 1 is hermetic (no native payload). Layer 2
transcendentals are backed by a vendored openlibm compiled to LLVM bitcode and
linked into the program only when a transcendental is used, so hermetic-only
programs carry no math payload and there is never a runtime dependency. See the
*Math runtime model* in [Architecture](../internals/architecture.md) for the mechanism.

Integer:

- `abs(n)` returning `Int` — absolute value
- `signum(n)` returning `Int` — `-1`, `0`, or `1`
- `min(a)(b)` / `max(a)(b)` returning `Int`
- `clamp(lo)(hi)(n)` returning `Int` — `n` confined to `[lo, hi]`
- `gcd(a)(b)` returning `Int` — greatest common divisor (non-negative)
- `lcm(a)(b)` returning `Int` — least common multiple (non-negative; `0` if either is `0`)
- `divMod(a)(b)` returning `(Int, Int)` — Euclidean quotient and remainder (`0 <= r < |b|`)
- `pow(base)(exp)` returning `Int` — exponentiation by squaring (`exp >= 0`)
- `isqrt(n)` returning `Int` — integer (floor) square root (`n >= 0`)

Float:

- `absF(x)` / `signumF(x)` returning `Float`
- `minF(a)(b)` / `maxF(a)(b)` returning `Float`
- `clampF(lo)(hi)(x)` returning `Float`
- `sqrt(x)` returning `Float` — hardware square root (`llvm.sqrt`), no library
- `floor(x)` / `ceil(x)` / `round(x)` / `trunc(x)` returning `Float`
- `pi` / `e` / `tau` — `Float` constants

Conversions:

- `toFloat(n)` returning `Float` — widen an `Int`
- `floorToInt(x)` / `roundToInt(x)` / `truncToInt(x)` returning `Int` — narrow a `Float`

Transcendentals (Layer 2, openlibm-backed):

- Trigonometric: `sin(x)`, `cos(x)`, `tan(x)`, `asin(x)`, `acos(x)`, `atan(x)`, `atan2(y)(x)`
- Hyperbolic: `sinh(x)`, `cosh(x)`, `tanh(x)`
- Exponential / logarithmic: `exp(x)`, `expm1(x)`, `ln(x)`, `log2(x)`, `log10(x)`, `log1p(x)`
- Powers / roots: `powF(base)(exp)`, `cbrt(x)`, `hypot(x)(y)`
- Remainder: `fmod(x)(y)`

Domain errors follow IEEE-754 (`sqrt(-1.0)` is `NaN`), so the Float functions
stay total.

### `Ashes.Number.BigInt`

Native **arbitrary-precision signed integers** (`BigInt`). Values are immutable and
arena-allocated; each operation returns a fresh, normalized value. The arithmetic is emitted
directly as LLVM-IR runtime helpers by the backend (no runtime dependency, no external library);
see the [architecture notes](../internals/architecture.md#bigint-arbitrary-precision-integers).

- `fromInt(n)` returning `BigInt` — widen a 64-bit `Int`
- `toInt(a)` returning `Result(Str, Int)` — `Ok` when it fits an `Int`, else `Error`
- `add(a)(b)`, `sub(a)(b)`, `mul(a)(b)` returning `BigInt`
- `div(a)(b)` returning `BigInt` — quotient, truncated toward zero (division by zero yields `0`)
- `mod(a)(b)` returning `BigInt` — remainder; its sign follows the dividend
- `compare(a)(b)` returning `Int` — `-1`, `0`, or `1`

`BigInt` is a distinct primitive with no implicit conversion to/from `Int`. It supports **`N`
literals** (`123N`) and the **operators** `+ - * / %` plus the comparisons `== != < <= > >=`, so
the named functions above are rarely needed directly. Decimal string conversions live in
`Ashes.Text`: `fromBigInt(a)` (→ `Str`) and `parseBigInt(s)` (→ `Result(Str, BigInt)`), matching
`fromInt`/`parseInt`.

```
import Ashes.Number.BigInt as big
let squared = 1000000000000N * 1000000000000N
Ashes.IO.print(Ashes.Text.fromBigInt(squared))
// 1000000000000000000000000
```

### `Ashes.Number.UInt`

- `toInt(value)` returning `Int` — widen an unsigned integer (`u8`/`u16`/`u32`/`u64`) to a signed
  `Int`. Value-preserving for `u8`/`u16`/`u32` (and a bit-reinterpret for `u64`); it is the bridge that
  lets a byte from `Ashes.Byte.get` be used in `Int` arithmetic, enabling byte-level integer parsing
  without routing through strings.
- `fromInt(value)` returning `u8` — narrow an `Int` to an unsigned byte, wrapping modulo 256 (the low
  8 bits). The inverse of `toInt`; lets a computed byte value be written with `Ashes.Byte.appendByte`
  / `Ashes.Byte.singleton` (e.g. building a percent-decoded string byte by byte).

## `Ashes.Collection` — containers

### `Ashes.Collection.List`

- `append` — `List(a) -> List(a) -> List(a)`, the elements of `left` followed by those of `right`
- `filter` — `(a -> Bool) -> List(a) -> List(a)`, the elements satisfying `predicate`, in order
- `foldLeft` — `(b -> a -> b) -> b -> List(a) -> b`, left fold from `initial` over the list
- `fold` — alias for `foldLeft`
- `head` — `List(a) -> Maybe(a)`, the first element, or `None` if empty
- `isEmpty` — `List(a) -> Bool`, whether the list has no elements
- `length` — `List(a) -> Int`, number of elements
- `map` — `(a -> b) -> List(a) -> List(b)`, apply `f` to each element
- `reverse` — `List(a) -> List(a)`, the elements in reverse order
- `sortBy` — `(a -> a -> Bool) -> List(a) -> List(a)`, a stable `O(n log n)` merge sort ordered by the
  comparator `before`: `before(x)(y)` is `true` when `x` should not come after `y` (e.g. `given (a) ->
  given (b) -> a <= b` for ascending). Provide your own comparator since the language has no built-in
  ordering typeclass
- `tail` — `List(a) -> Maybe(List(a))`, all but the first element, or `None` if empty

### `Ashes.Collection.Array`

Immutable indexed array backed by a persistent balanced tree.

- `empty` — empty immutable array
- `isEmpty(array)` returning `Bool`
- `length(array)` returning `Int`
- `get(index)(array)` returning `Maybe(T)` — `None` for out-of-bounds indices
- `set(index)(value)(array)` returning a new array (out-of-bounds indices leave the array unchanged)
- `append(value)(array)` returning a new array with value appended at the end
- `toList(array)` returning `List(T)` in index order
- `fromList(list)` returning a new array preserving input order

### `Ashes.Collection.Map`

- `empty` — empty immutable map
- `isEmpty(map)` returning `Bool` — whether the map has no entries
- `get(compare)(key)(map)` returning `Maybe(V)`
- `getStr(key)(map)` returning `Maybe(V)` — `Str`-keyed lookup ordered by UTF-8 byte order
  (`Ashes.Byte.compare` inline; no comparator closure, so it is markedly faster than
  `get(Ashes.Text.compare)`)
- `contains(compare)(key)(map)` returning `Bool`
- `set(compare)(key)(value)(map)` returning a new map value
- `setStr(key)(value)(map)` returning a new map value — `Str`-keyed `set`, same ordering and
  performance rationale as `getStr`
- `upsertStr(key)(missValue)(onHit)(map)` returning a new map value — single-traversal
  insert-or-update: inserts `missValue` when `key` is absent, else replaces the stored value with
  `onHit(oldValue)`. Halves the tree work of a `getStr`-then-`setStr` pair in accumulation loops.
- `insert(compare)(key)(value)(map)` returning a new map value
- `size(map)` returning `Int`
- `foldLeft(folder)(state)(map)` returning the folded state in key order
- `toList(map)` returning `List((K, V))` in key order
- `fromList(compare)(entries)` returning a new map value

`Ashes.Collection.Map` is implemented as a persistent AVL tree. Because Ashes does not yet
have a built-in ordering abstraction, callers supply a total ordering function
`(K -> K -> Int)` to lookup and update helpers.

### `Ashes.Collection.HashMap`

A persistent map keyed by `Str` that needs **no caller-supplied ordering**. Internally an
AVL tree ordered by the composite key `(FNV-1a hash, key)`, so navigation is dominated by
cheap 64-bit integer comparisons and only falls back to string comparison on a hash
collision. Same persistent-structure cost model as `Ashes.Collection.Map` (O(log K) nodes per update).

- `empty` — empty hash map
- `get(key)(map)` returning `Maybe(V)`
- `contains(key)(map)` returning `Bool`
- `set(key)(value)(map)` returning a new map
- `insert` — alias of `set`
- `size(map)` returning `Int`
- `foldLeft(folder)(state)(map)` returning the folded state (key order is by hash, not lexical)

### `Ashes.Collection.HashTrie`

A persistent 16-ary hash trie keyed by `Str` — the constant-factor alternative to `Ashes.Collection.Map`
for large keyed accumulations. Each internal node carries its own nibble shift, so a lookup or
upsert costs ~4-5 dependent node loads at tens of thousands of keys (vs ~17 for the AVL
`Ashes.Collection.Map`), at the price of hash iteration order (re-sort at the end when ordered output is
needed). Keys compare by UTF-8 bytes at the leaf; equal-hash collisions chain through the leaf.
Update loops get the same in-place reuse specialization as `Map.set`, so hot folds are
constant-memory.

- `empty` — empty trie
- `hashText(text)` returning `Int` — the key hash (`Ashes.Byte.hash` of the UTF-8 bytes);
  compute once per key and pass to the operations below
- `upsertHashed(hash)(key)(missValue)(onHit)(trie)` returning a new trie — single-traversal
  insert-or-update: inserts `missValue` when absent, else replaces the stored value with
  `onHit(oldValue)`
- `getHashed(hash)(key)(trie)` returning `Maybe(V)`
- `foldLeft(folder)(state)(trie)` returning the folded state (hash order, not key order)
- `toList(trie)` returning `List((K, V))` in hash order
- `size(trie)` returning `Int`

## `Ashes.Text` — strings and text formats

### `Ashes.Text`

The single string module: character/codepoint helpers, search and slicing, parsing and
formatting. The conversion and case functions are compiler intrinsics; the search/slice/split
layer is shipped Ashes code built on `Ashes.Byte`.

- `uncons(text)` returning `Maybe((Str, Str))`
- `parseInt(text)` returning `Result(Str, Int)`
- `parseFloat(text)` returning `Result(Str, Float)`
- `parseBigInt(text)` returning `Result(Str, BigInt)` — decimal parse into a [`BigInt`](#ashesnumberbigint)
- `fromInt(value)` returning `Str`
- `fromFloat(value)` returning `Str`
- `fromBigInt(value)` returning `Str` — decimal rendering of a [`BigInt`](#ashesnumberbigint)
- `formatFloat(value)(decimals)` returning `Str` — fixed-precision decimal formatting: exactly
  `decimals` fractional digits, trailing zeros kept (`formatFloat(1.5)(9)` is `1.500000000`,
  `formatFloat(2.5)(0)` is `3`). `decimals` is clamped to the range 0–18. Rounding is
  half-away-from-zero on the magnitude. Magnitudes too large for the fixed path fall back to the
  same scientific notation as `fromFloat`.
- `toHex(value)` returning `Str`
- `byteLength(text)` returning `Int` — UTF-8 byte length of a string
- `asciiUpper(text)` returning `Str` — ASCII-only uppercase: `a`–`z` map to `A`–`Z` in a single
  O(N) byte pass; every byte of a multibyte UTF-8 sequence is `>= 0x80` and passes through
  byte-identical, so non-ASCII text is untouched (no Unicode case folding — the ASCII scope is in
  the name, following OCaml's `uppercase_ascii` / Rust's `to_ascii_uppercase`)
- `asciiLower(text)` returning `Str` — ASCII-only lowercase, the inverse of `asciiUpper`
 
- `length` — `Str -> Int`, number of characters
- `substring` — `Str -> Int -> Int -> Str`, `count` characters starting at index `start`. Codepoint-
  indexed, so it walks the string to the `start`-th codepoint — `O(start + count)`. For repeated
  indexed slicing of the same buffer (e.g. a sliding k-mer window), materialize it once with
  `Ashes.Byte.fromText` and use the byte-indexed `Ashes.Byte.subText` (`O(count)` per slice)
- `take` — `Str -> Int -> Str`, the first `count` characters
- `drop` — `Str -> Int -> Str`, all but the first `count` characters
- `indexOf` — `Str -> Str -> Int`, index of the first occurrence of `needle`, or `-1` if absent
- `startsWith` — `Str -> Str -> Bool`, whether `text` begins with `prefix`
- `contains` — `Str -> Str -> Bool`, whether `needle` occurs anywhere in `text`
- `split` — `Str -> Str -> List(Str)`, split `text` on each occurrence of `separator`
- `join` — `Str -> List(Str) -> Str`, concatenate `parts` with `separator` between them
- `trim` — `Str -> Str`, strip leading and trailing whitespace
- `trimStart` — `Str -> Str`, strip leading whitespace
- `trimEnd` — `Str -> Str`, strip trailing whitespace
- `isLetter` — `Str -> Bool`, whether the single character `text` is an ASCII letter (`a`–`z`, `A`–`Z`)
- `isDigit` — `Str -> Bool`, whether the single character `text` is a decimal digit (`0`–`9`)
- `isWhiteSpace` — `Str -> Bool`, whether the single character `text` is space, tab, newline, or carriage return
- `compare` — `Str -> Str -> Int` total order returning `-1`/`0`/`1`. Compares by UTF-8 bytes (via
  `Ashes.Byte.fromText`), which equals Unicode codepoint order, so it is a correct total order over
  all strings — suitable directly as the ordering function for `Ashes.Collection.Map`/`Ashes.Collection.Array`.

### `Ashes.Text.Regex`

Regular expressions backed by [PCRE2](https://www.pcre.org/) (Perl-compatible syntax). The 8-bit
PCRE2 library is compiled to LLVM bitcode and linked directly into the executable when a program
uses this module — everything unreachable from the exposed API is stripped, and there is no runtime
dependency, exactly like the rest of Ashes. Pattern and subject are treated as **UTF-8 with Unicode
property support** (`\d`, `\w`, `\p{L}`, Unicode-aware case handling). Offsets are **byte offsets**
into the subject.

- **Type**: `Regex` — an opaque compiled pattern.
- `compile(pattern)` returning `Result(Str, Regex)` — compile a pattern once. `Error` carries the
  PCRE2 diagnostic for an invalid pattern; `Ok` wraps a reusable compiled `Regex`.
- `isMatch(regex)(text)` returning `Bool` — true if the pattern matches anywhere in `text`.
- `find(regex)(text)` returning `Maybe((Int, Int))` — the first match as `Some((start, end))` byte
  offsets, or `None`.
- `findAll(regex)(text)` returning `List((Int, Int))` — every non-overlapping match as `(start, end)`
  byte offsets.
- `captures(regex)(text)` returning `Maybe(List(Maybe(Str)))` — the first match's capture groups.
  Group 0 is the whole match; each group is `Some(text)` or `None` if it did not participate.
- `replace(regex)(text)(replacement)` returning `Str` — replace every match. The replacement string
  uses PCRE2 substitution syntax (`$1`, `${name}` group references).

```ash
import Ashes.Text.Regex

match Ashes.Text.Regex.compile("([a-z]+)=([0-9]+)") with
    | Error(message) -> Ashes.IO.print("bad pattern: " + message)
    | Ok(re) ->
        match Ashes.Text.Regex.captures(re)("port=8080") with
            | None -> Ashes.IO.print("no match")
            | Some(_groups) -> Ashes.IO.print("matched")
```

Compile a pattern once and reuse the `Regex` across many subjects; compilation is the expensive
step. Matching is memory-bounded even in a tight recursion over many subjects.

### `Ashes.Text.Json`

Full JSON value type with a recursive-descent parser and serializer. Objects and arrays are
represented as their own cons-style ADT (no dependency on `Ashes.Collection.Map`).

- **Type**: `Json` — ADT with constructors `JsonNull`, `JsonBool(Bool)`, `JsonInt(Int)`,
  `JsonFloat(Float)`, `JsonStr(Str)`, `JsonArray(Json, Json)` / `JsonArrayEnd` (head/tail list),
  `JsonObject(Str, Json, Json)` / `JsonObjectEnd` (key/value/rest list)
- `parse(text)` returning `Result(Str, Json)` — parse a JSON document
- `stringify(value)` returning `Str` — serialize a JSON value
- `get(key)(json)` returning `Json` — object field lookup (`JsonNull` when absent)
- `index(i)(json)` returning `Json` — array element lookup (`JsonNull` when out of range)
- `asStr` / `asInt` / `asFloat` / `asBool` — extract a scalar with a sensible default
- `isNull(json)` returning `Bool`

## `Ashes.Byte` — binary data

### `Ashes.Byte`

An immutable byte sequence with O(1) indexed access and O(1) length.

- `empty()` returning `Bytes` — empty byte sequence
- `singleton(byte)` returning `Bytes` — one-byte sequence
- `length(bytes)` returning `Int`
- `get(bytes, index)` returning `u8` — panics if index out of bounds
- `indexOf(bytes)(needle)(from)` returning `Int` — index of the first byte equal to `needle` (an
  `Int` byte value) at or after `from`, or `-1` if none. O(len − from), no allocation — a memchr for
  scanning a buffer by integer position without materializing views.
- `compare(left)(right)` returning `Int` — three-way lexicographic byte order, normalized to
  `-1`/`0`/`1`. One `memcmp` over the common prefix plus a length tie-break — far faster than a
  byte-at-a-time loop. With `fromText` this underlies `Ashes.Text.compare`.
- `subText(bytes)(start)(len)` returning `Str` — copy `len` bytes starting at `start` into a fresh
  `Str`. O(len); the range is clamped into the source so it never reads out of bounds. The caller
  must ensure the range lies on valid UTF-8 boundaries (slicing at ASCII delimiters like `;`/`\n`
  always does). With `indexOf` this lets a buffer be scanned by integer index instead of a shrinking
  `Str` view.
- `subView(bytes)(start)(len)` returning `Str` — a zero-copy VIEW over the same range `subText`
  would copy (O(1), no byte copy; same clamping and UTF-8 caveat). The backing bytes must outlive
  the view: a view over an `Ashes.IO.File.mmap` mapping is valid for the program's lifetime, and a
  view stored into a structure (e.g. a `Map` key) is materialized by the copy-out/blob paths.
  Prefer it for transient per-record slices in hot scan loops.
- `append(left, right)` returning `Bytes` — concatenate two sequences
- `appendByte(bytes, byte)` returning `Bytes` — append one byte
- `fromList(list)` returning `Bytes` — convert `List(u8)` to `Bytes`
- `fromText(text)` returning `Bytes` — expose a `Str`'s UTF-8 bytes (O(1); `Str` and `Bytes`
  share an in-memory layout). Byte order over the result equals Unicode codepoint order, so this is
  the basis for a correct string ordering (see `Ashes.Text.compare`).
- `hash(bytes)` returning `Int` — 64-bit FNV-1a hash of the byte payload. With `fromText` this
  gives string hashing; it underlies `Ashes.Collection.HashMap`.
- `u16Le(value)` returning `Bytes` — encode `u16` little-endian (2 bytes)
- `u32Le(value)` returning `Bytes` — encode `u32` little-endian (4 bytes)
- `u64Le(value)` returning `Bytes` — encode `u64` little-endian (8 bytes)
- `getU16Le(bytes, offset)` returning `u16` — decode little-endian `u16` at offset
- `getU32Le(bytes, offset)` returning `u32` — decode little-endian `u32` at offset
- `getU64Le(bytes, offset)` returning `u64` — decode little-endian `u64` at offset

## `Ashes.Task` — concurrency

### `Ashes.Task`

Asynchronous tasks: the `Task(E, A)` values consumed with `await` inside `async(...)` blocks
(or the `let!` sugar). All networking APIs return tasks; this module creates and drives them.

- `run(task)` returning `Result(E, A)` — drive a task to completion on the scheduler (program
  entry point for async code)
- `task(value)` returning `Task(E, A)` — wrap a pure value as an immediately-completed task
- `fromResult(result)` returning `Task(E, A)` — lift a `Result` into a task
- `sleep(ms)` returning `Task(Str, Int)` — complete after `ms` milliseconds
- `all(tasks)` returning `Task(E, List(A))` — run all tasks, collect results in order
- `race(tasks)` returning `Task(E, A)` — first task to complete wins
- `spawn(task)` returning `Unit` — fire-and-forget a task on the scheduler (used by the
  socket servers to run one handler per connection)

Supported on Linux x64, Linux arm64, and Windows x64 (run-queue scheduler on all three).

### `Ashes.Task.Parallel`

Structured, deterministic parallelism over **pure** functions (see
the [compiler changelog](../internals/changelog.md)). Every result is identical to the sequential
equivalent. `both` is a **genuinely parallel** fork/join primitive on all three targets
(per-thread arenas + worker threads + deep-copy-on-join), forking at concrete result types
and running sequentially for abstract ones. `map`/`reduce` fork at concrete element types via
call-site monomorphization, degrading to a (correct) sequential evaluation when used polymorphically
or partially applied.

- `both(left)(right)` returning `(A, B)` — fork/join two pure thunks `(Unit -> A)`, `(Unit -> B)`
- `map(f)(list)` returning `List(B)` — order-preserving map, split-and-fork shaped
- `reduce(combine)(identity)(f)(list)` returning `B` — parallel map-then-fold for associative
  `combine` (the shard-and-merge shape for data-parallel aggregation)
- `mapGrained(grain)(f)(list)` / `reduceGrained(grain)(combine)(identity)(f)(list)` — the same
  operations with an explicit **grain size**: shards of `grain` elements or fewer are processed
  sequentially instead of split further, trading split overhead against parallelism. `map`/`reduce`
  are exactly `mapGrained(1)` / `reduceGrained(1)`. The result is always identical to the sequential
  equivalent, whatever the grain (grains `< 1` behave as `1`).

#### Chunking a byte buffer

- `splitChunks(bytes)(sep)(n)` returning `List((Bytes, Int, Int))` — split `bytes` into up to `n`
  contiguous `(bytes, lo, hi)` sub-ranges, each ending just after an occurrence of the record
  separator byte `sep`, so no record straddles a chunk boundary (the last chunk runs to the buffer
  end). It is the record-boundary chunker for the data-parallel byte-scan pattern: feed the result
  straight to `reduce` — `reduce(merge)(identity)(foldChunk)(splitChunks(bytes)(sep)(n))` — so the
  `reduce` call stays at the caller's concrete result type and genuinely forks. `splitChunks` itself
  is pure and does the boundary-aligned splitting only (no parallel work).

#### Scoped worker overrides

`--parallel-workers` sets the executable's **compiled maximum** (its hard ceiling, or the detected
core count when unset). A program can request **fewer** workers for a specific computation with a
dynamically-scoped override; the effective count is `min(override, compiledMax)`, so a request
above the ceiling still clamps and one below it reduces the local limit. The override is restored
when the scope returns.

- `withWorkers(count)(action)` returning `A` — run the pure thunk `action : Unit -> A` with the
  worker cap scoped to `count` (clamped to the compiled maximum). `count` must be positive (a
  non-positive count panics). Nested `withWorkers` scopes apply the inner value inside and restore
  the outer on return. The result is identical to running `action` without the override — only the
  parallelism used to compute it changes.
- `bothWithWorkers(count)(left)(right)`, `mapWithWorkers(count)(f)(list)`,
  `mapGrainedWithWorkers(count)(grain)(f)(list)`, `reduceWithWorkers(count)(combine)(identity)(f)(list)`,
  `reduceGrainedWithWorkers(count)(grain)(combine)(identity)(f)(list)` — convenience wrappers, each
  equal to `withWorkers(count)` around the corresponding operation.

## `Ashes.Core` — core value helpers

### `Ashes.Core.Maybe`

- `map` — `(a -> b) -> Maybe(a) -> Maybe(b)`, apply `f` to the contained value if `Some`
- `flatMap` — `(a -> Maybe(b)) -> Maybe(a) -> Maybe(b)`, apply `f` to the contained value, flattening
- `getOrElse` — `a -> Maybe(a) -> a`, the contained value, or `fallback` if `None`
- `default` — alias for `getOrElse`
- `unwrapOr` — alias for `getOrElse`
- `isSome` — `Maybe(a) -> Bool`, whether the value is `Some`
- `isNone` — `Maybe(a) -> Bool`, whether the value is `None`

### `Ashes.Core.Result`

- `map` — `(a -> b) -> Result(e, a) -> Result(e, b)`, apply `f` to the `Ok` value
- `flatMap` — `(a -> Result(e, b)) -> Result(e, a) -> Result(e, b)`, apply `f` to the `Ok` value, flattening
- `bind` — alias for `flatMap`
- `mapError` — `(e -> f) -> Result(e, a) -> Result(f, a)`, apply `f` to the `Error` value
- `getOrElse` — `a -> Result(e, a) -> a`, the `Ok` value, or `fallback` if `Error`
- `default` — alias for `getOrElse`
- `isOk` — `Result(e, a) -> Bool`, whether the value is `Ok`
- `isError` — `Result(e, a) -> Bool`, whether the value is `Error`

## Testing and internals

### `Ashes.Test`

- `assertEqual(expected, actual)` returning `Unit` — panic with an assertion failure unless `expected == actual`. Works at `Str`, `Int`, `Float`, and `Bool`, and different types may be asserted within the same program.
- `fail(message)` returning `a` — abort with `message`; never returns, so it is usable at any type

`assertEqual(expected, actual)` is the preferred surface form. Like other
multi-argument calls in Ashes, it is syntax sugar for curried application.

Canonical example:

```ash
import Ashes.Test

let checked = assertEqual(3, 3)
in Ashes.IO.print("ok")
```

`Ashes.Test` is ordinary shipped library code, not a special compiler intrinsic.

### `Ashes.Internal`

Compiler-foundation primitives (not intended for everyday use).

- `deepCopy(value)` returning the same type — an independent deep copy of any value (strings,
  tuples, lists, closures, and recursive ADTs such as `Map`/`HashMap`). Semantically the identity
  for immutable values; it underlies arena reclamation and parallel result copy-out.
