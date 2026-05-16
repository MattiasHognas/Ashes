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

## Built-in Modules

### `Ashes.IO`

- `print(value)`
- `panic(message)`
- `args`
- `write(text)`
- `writeLine(text)`
- `readLine()` returning `Maybe(Str)`

### `Ashes.File`

- `readText(path)` returning `Result(Str, Str)`
- `writeText(path, text)` returning `Result(Str, Unit)`
- `exists(path)` returning `Result(Str, Bool)`

### `Ashes.Text`

- `uncons(text)` returning `Maybe((Str, Str))`
- `parseInt(text)` returning `Result(Str, Int)`
- `parseFloat(text)` returning `Result(Str, Float)`
 
### `Ashes.Http`

- `get(url)` returning `Task(Str, Str)`
- `post(url, body)` returning `Task(Str, Str)`

All networking APIs are async-only and must be awaited inside `async` blocks.

Current HTTP support is intentionally small:

- `http://` and `https://` URLs are supported.
- `https://` defaults to port 443 and currently ships on the Linux x64,
	Linux arm64, and Windows x64 backends via OpenSSL 3 loaded at runtime.
- Other backends may still return a runtime error for `https://` until their
	TLS runtime support lands.
- Responses are expected to be plain HTTP/1.1 responses terminated by connection close.
- `Transfer-Encoding: chunked` currently returns `Error("unsupported transfer encoding")`.

### `Ashes.Net.Tcp`

- `connect(host)(port)` returning `Task(Str, Socket)`
- `send(socket)(text)` returning `Task(Str, Int)`
- `receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `close(socket)` returning `Task(Str, Unit)`

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
