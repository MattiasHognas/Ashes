# Ashes Standard Library

This document summarizes the current compiler-provided standard-library surface.
It is user-facing guidance for the shipped modules; the authoritative language
semantics remain in `docs/LANGUAGE_SPEC.md`.

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

## Built-in Modules

### `Ashes.IO`

- `print(value)`
- `panic(message)`
- `args`
- `write(text)`
- `writeLine(text)`
- `readLine()` returning `Maybe(Str)`
- `readExact(n)` returning `Result(Str, Str)` — read exactly `n` bytes from stdin

### `Ashes.File`

- `readText(path)` returning `Result(Str, Str)`
- `writeText(path, text)` returning `Result(Str, Unit)`
- `writeBytes(path, bytes)` returning `Result(Str, Unit)`
- `exists(path)` returning `Result(Str, Bool)`
- `open(path)` returning `Result(Str, FileHandle)` — open a file for reading; the handle is a
  resource type, automatically closed when it goes out of scope.
- `readChunk(handle)(maxBytes)` returning `Result(Str, Str)` — read up to `maxBytes` bytes;
  returns `Ok("")` at end of file. Lets a large file be streamed without loading it whole (cf.
  `readText`, which allocates the entire file at once).
- `close(handle)` returning `Result(Str, Unit)` — close explicitly (also automatic on scope exit).

### `Ashes.Bytes`

An immutable byte sequence with O(1) indexed access and O(1) length.

- `empty()` returning `Bytes` — empty byte sequence
- `singleton(byte)` returning `Bytes` — one-byte sequence
- `length(bytes)` returning `Int`
- `get(bytes, index)` returning `u8` — panics if index out of bounds
- `append(left, right)` returning `Bytes` — concatenate two sequences
- `appendByte(bytes, byte)` returning `Bytes` — append one byte
- `fromList(list)` returning `Bytes` — convert `List(u8)` to `Bytes`
- `fromText(text)` returning `Bytes` — expose a `Str`'s UTF-8 bytes (O(1); `Str` and `Bytes`
  share an in-memory layout). Byte order over the result equals Unicode codepoint order, so this is
  the basis for a correct string ordering (see `Ashes.String.compare`).
- `hash(bytes)` returning `Int` — 64-bit FNV-1a hash of the byte payload. With `fromText` this
  gives string hashing; it underlies `Ashes.HashMap`.
- `u16Le(value)` returning `Bytes` — encode `u16` little-endian (2 bytes)
- `u32Le(value)` returning `Bytes` — encode `u32` little-endian (4 bytes)
- `u64Le(value)` returning `Bytes` — encode `u64` little-endian (8 bytes)
- `getU16Le(bytes, offset)` returning `u16` — decode little-endian `u16` at offset
- `getU32Le(bytes, offset)` returning `u32` — decode little-endian `u32` at offset
- `getU64Le(bytes, offset)` returning `u64` — decode little-endian `u64` at offset

### `Ashes.Text`

- `uncons(text)` returning `Maybe((Str, Str))`
- `parseInt(text)` returning `Result(Str, Int)`
- `parseFloat(text)` returning `Result(Str, Float)`
- `fromInt(value)` returning `Str`
- `fromFloat(value)` returning `Str`
- `toHex(value)` returning `Str`
- `byteLength(text)` returning `Int` — UTF-8 byte length of a string
 
### `Ashes.Process`

Synchronous subprocess control with piped stdin/stdout/stderr.
`Process` is a resource type; it is automatically closed when it goes out of scope.

- `spawn(exe)(args)` returning `Result(Str, Process)` — launch `exe` with argument list `args`
- `writeStdin(proc)(text)` returning `Unit` — write bytes to the process's stdin pipe
- `readStdoutLine(proc)` returning `Maybe(Str)` — read one line from stdout (`None` on EOF)
- `readStderrLine(proc)` returning `Maybe(Str)` — read one line from stderr (`None` on EOF)
- `waitForExit(proc)` returning `Int` — block until the process exits, return its exit code
- `kill(proc)` returning `Unit` — send SIGTERM (Linux) or `TerminateProcess` (Windows)

Supported on Linux x64, Linux arm64, and Windows x64.

### `Ashes.Http`

- `get(url)` returning `Task(Str, Str)`
- `post(url, body)` returning `Task(Str, Str)`

All networking APIs return `Task(...)` and are consumed with `await`.

Current HTTP support is intentionally small:

- `http://` and `https://` URLs are supported.
- `https://` defaults to port 443 and currently ships on the Linux x64,
  Linux arm64, and Windows x64 backends via the hermetic `rustls`
  runtime embedded into TLS-using executables.
- Other backends may still return a runtime error for `https://` until their
  TLS runtime support lands.
- Responses are expected to be plain HTTP/1.1 responses terminated by connection close.
- `Transfer-Encoding: chunked` currently returns `Error("unsupported transfer encoding")`.

### `Ashes.Net.Tcp`

- `connect(host)(port)` returning `Task(Str, Socket)`
- `send(socket)(text)` returning `Task(Str, Int)`
- `receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `close(socket)` returning `Task(Str, Unit)`

### `Ashes.Net.Tls`

- `connect(host)(port)` returning `Task(Str, TlsSocket)`
- `send(socket)(text)` returning `Task(Str, Int)`
- `receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `close(socket)` returning `Task(Str, Unit)`

`Ashes.Net.Tls` uses the same TLS runtime path as `https://` in `Ashes.Http`.
On Linux x64, Linux arm64, and Windows x64 that currently means the
hermetic `rustls` runtime embedded per TLS-using executable. No
external OpenSSL installation is required. Hostname verification and
system-trust validation are mandatory for successful TLS connections.

## Shipped Helper Modules

These modules are compiler-shipped and live under the reserved `Ashes.*`
namespace. They are not overridable by project-local modules.

### `Ashes.List`

- `append`
- `filter`
- `fold`
- `foldLeft`
- `head`
- `isEmpty`
- `length`
- `map`
- `reverse`
- `tail`

### `Ashes.Array`

Immutable indexed array backed by a persistent balanced tree.

- `empty` — empty immutable array
- `isEmpty(array)` returning `Bool`
- `length(array)` returning `Int`
- `get(index)(array)` returning `Maybe(T)` — `None` for out-of-bounds indices
- `set(index)(value)(array)` returning a new array (out-of-bounds indices leave the array unchanged)
- `append(value)(array)` returning a new array with value appended at the end
- `toList(array)` returning `List(T)` in index order
- `fromList(list)` returning a new array preserving input order

### `Ashes.Map`

- `empty` — empty immutable map
- `isEmpty`
- `get(compare)(key)(map)` returning `Maybe(V)`
- `contains(compare)(key)(map)` returning `Bool`
- `set(compare)(key)(value)(map)` returning a new map value
- `insert(compare)(key)(value)(map)` returning a new map value
- `size(map)` returning `Int`
- `foldLeft(folder)(state)(map)` returning the folded state in key order
- `toList(map)` returning `List((K, V))` in key order
- `fromList(compare)(entries)` returning a new map value

`Ashes.Map` is implemented as a persistent AVL tree. Because Ashes does not yet
have a built-in ordering abstraction, callers supply a total ordering function
`(K -> K -> Int)` to lookup and update helpers.

### `Ashes.Parallel`

Structured, deterministic parallelism over **pure** functions (design:
`docs/future/STRUCTURED_PARALLELISM.md`). Every result is identical to the sequential
equivalent. **Execution is currently sequential** — `both` is the fork/join primitive that
the threading runtime (per-thread arenas + clone/futex + deep-copy-on-join) will make
genuinely parallel; `map`/`reduce` then parallelize with no source changes.

- `both(left)(right)` returning `(A, B)` — fork/join two pure thunks `(Unit -> A)`, `(Unit -> B)`
- `map(f)(list)` returning `List(B)` — order-preserving map, split-and-fork shaped
- `reduce(combine)(identity)(f)(list)` returning `B` — parallel map-then-fold for associative
  `combine` (the shard-and-merge shape for data-parallel aggregation)

### `Ashes.Internal`

Compiler-foundation primitives (not intended for everyday use).

- `deepCopy(value)` returning the same type — an independent deep copy of any value (strings,
  tuples, lists, closures, and recursive ADTs such as `Map`/`HashMap`). Semantically the identity
  for immutable values; it underlies arena reclamation (FLAWS #2) and parallel result copy-out (#5).

### `Ashes.HashMap`

A persistent map keyed by `Str` that needs **no caller-supplied ordering**. Internally an
AVL tree ordered by the composite key `(FNV-1a hash, key)`, so navigation is dominated by
cheap 64-bit integer comparisons and only falls back to string comparison on a hash
collision. Same persistent-structure cost model as `Ashes.Map` (O(log K) nodes per update).

- `empty` — empty hash map
- `get(key)(map)` returning `Maybe(V)`
- `contains(key)(map)` returning `Bool`
- `set(key)(value)(map)` returning a new map
- `insert` — alias of `set`
- `size(map)` returning `Int`
- `foldLeft(folder)(state)(map)` returning the folded state (key order is by hash, not lexical)

### `Ashes.Maybe`

- `default`
- `flatMap`
- `getOrElse`
- `isNone`
- `isSome`
- `map`
- `unwrapOr`

### `Ashes.Result`

- `default`
- `bind`
- `map`
- `flatMap`
- `getOrElse`
- `isOk`
- `isError`
- `mapError`

### `Ashes.String`

- `substring`
- `length`
- `indexOf`
- `startsWith`
- `contains`
- `split`
- `trim`
- `isLetter`
- `isDigit`
- `isWhiteSpace`
- `compare` — `Str -> Str -> Int` total order returning `-1`/`0`/`1`. Compares by UTF-8 bytes (via
  `Ashes.Bytes.fromText`), which equals Unicode codepoint order, so it is a correct total order over
  all strings — suitable directly as the ordering function for `Ashes.Map`/`Ashes.Array`.

### `Ashes.Json`

Full JSON value type and recursive-descent parser/serializer.

- **Type**: `Json` — ADT with constructors `JsonNull`, `JsonBool(Bool)`, `JsonNumber(Float)`, `JsonString(Str)`, `JsonArray(Json, Json)` (head/tail list), `JsonObject(Str, Json, Json)` (key/value/rest list)
- `parse(text)` returning `Result(Str, Json)` — parse a JSON string
- `stringify(value)` returning `Str` — serialize a JSON value to a string
- Accessor helpers: `getBool`, `getNumber`, `getString`, `getArray`, `getField`, `isNull`, `isBool`, `isNumber`, `isString`, `isArray`, `isObject`

### `Ashes.Rpc`

Stdio JSON-RPC 2.0 Content-Length framing for LSP/DAP transports.

- `readMessage()` returning `Result(Str, Str)` — read one framed message from stdin (reads the `Content-Length:` header, then exactly that many bytes via `Ashes.IO.readExact`)
- `writeMessage(msg)` returning `Unit` — write a framed message to stdout with a `Content-Length:` header

### `Ashes.Regex`

Backtracking regular-expression engine with a combinator API.

- **Type**: `Regex` — opaque pattern value
- Pattern builders: `literal(s)`, `anyChar`, `anyOf(chars)`, `noneOf(chars)`, `digit`, `letter`, `whitespace`, `seq(a)(b)`, `alt(a)(b)`, `star(r)`, `plus(r)`, `optional(r)`, `capture(r)`
- `matches(pattern)(text)` returning `Bool` — true if the pattern matches anywhere in `text`
- `find(pattern)(text)` returning `Maybe(Str)` — return the first matching substring
- `findAll(pattern)(text)` returning `List(Str)` — return all non-overlapping matches
- `replace(pattern)(replacement)(text)` returning `Str` — replace all matches

### `Ashes.Test`

- `assertEqual(expected, actual)`
- `fail(message)`

`assertEqual(expected, actual)` is the preferred surface form. Like other
multi-argument calls in Ashes, it is syntax sugar for curried application.

Canonical example:

```ash
import Ashes.Test

let checked = assertEqual(3, 3)
in Ashes.IO.print("ok")
```

`Ashes.Test` is ordinary shipped library code, not a special compiler intrinsic.
