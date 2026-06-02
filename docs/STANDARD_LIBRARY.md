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
- `writeBytes(path, bytes)` returning `Result(Str, Unit)`
- `exists(path)` returning `Result(Str, Bool)`

### `Ashes.Bytes`

An immutable byte sequence with O(1) indexed access and O(1) length.

- `empty()` returning `Bytes` — empty byte sequence
- `singleton(byte)` returning `Bytes` — one-byte sequence
- `length(bytes)` returning `Int`
- `get(bytes, index)` returning `u8` — panics if index out of bounds
- `append(left, right)` returning `Bytes` — concatenate two sequences
- `appendByte(bytes, byte)` returning `Bytes` — append one byte
- `fromList(list)` returning `Bytes` — convert `List(u8)` to `Bytes`
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
 
### `Ashes.Http`

- `get(url)` returning `Task(Str, Str)`
- `post(url, body)` returning `Task(Str, Str)`

All networking APIs are async-only and must be awaited inside `async` blocks.

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
