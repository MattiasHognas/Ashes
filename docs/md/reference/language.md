# Ashes Language Specification

Ashes is a pure, statically typed, expression-based functional programming language
compiled directly to native code.

Values are immutable and freely shared; the compiler handles ownership
and memory safely behind the scenes.

This document describes the **surface syntax** and **semantic behavior** of the
currently supported language features.

---

## 1. Program Structure

Ashes is an **expression-based** language.

There are:

- no statements
- no semicolons
- no variable reassignment
- no loops

Every construct evaluates to a value.

Line comments are supported:

- `// ...` starts a comment that runs to the end of the current line.
- Comments are ignored by lexing/parsing and do not affect evaluation or typing.

The following words are **reserved keywords** and cannot be used as identifiers:

`let`, `recursive`, `and`, `in`, `if`, `then`, `else`, `match`, `with`, `when`, `given`,
`true`, `false`, `type`, `external`, `await`, `import`, `as`,
`capability`, `needs`, `perform`, `handle`, `trait`, `implement`, `requires`, `deriving`

Two principles govern the keyword set:

1. **Words for meaning, symbols for plumbing.** Keywords carry semantics and are full English
   words. Structural tokens — `->`, `=`, `|`, `(...)`, `{...}`, `::` — are plumbing and stay
   symbolic; no arrow is ever replaced with a word.
2. **No abbreviations.** A keyword is written out in full — `recursive`, `external`, `given` —
   never shortened.

Programs are composed using nested expressions such as:

let x = 10
in Ashes.IO.print(x + 1)

Built-in standard library members live under reserved `Ashes` modules.

Canonical built-ins available today include:

- `Ashes.IO.print(expr)` returning `Unit`
- `Ashes.IO.panic("message")` returning `Unit`
- `Ashes.IO.args` returning `List(Str)`
- `Ashes.IO.write(expr)` returning `Unit`
- `Ashes.IO.writeLine(expr)` returning `Unit`
- `Ashes.IO.readLine()` returning `Maybe(Str)`
- `Ashes.IO.File.readText(path)` returning `Result(Str, Str)`
- `Ashes.IO.File.writeText(path, text)` returning `Result(Str, Unit)`
- `Ashes.IO.File.exists(path)` returning `Result(Str, Bool)`
- `Ashes.Text.uncons(text)` returning `Maybe((Str, Str))`
- `Ashes.Text.parseInt(text)` returning `Result(Str, Int)`
- `Ashes.Text.parseFloat(text)` returning `Result(Str, Float)`
- `Ashes.Text.fromInt(value)` returning `Str`
- `Ashes.Text.fromFloat(value)` returning `Str`
- `Ashes.Text.toHex(value)` returning `Str`
- `Ashes.Net.Http.get(url)` returning `Task(Str, Str)`
- `Ashes.Net.Http.post(url, body)` returning `Task(Str, Str)`
- `Ashes.Net.Tcp.connect(host)(port)` returning `Task(Str, Socket)`
- `Ashes.Net.Tcp.send(socket)(text)` returning `Task(Str, Int)`
- `Ashes.Net.Tcp.receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `Ashes.Net.Tcp.close(socket)` returning `Task(Str, Unit)`
- `Ashes.Net.Tls.connect(host)(port)` returning `Task(Str, TlsSocket)`
- `Ashes.Net.Tls.send(socket)(text)` returning `Task(Str, Int)`
- `Ashes.Net.Tls.receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `Ashes.Net.Tls.close(socket)` returning `Task(Str, Unit)`
- `Ashes.Task.run(task)` returning `Result(E, A)`
- `Ashes.Task.task(value)` returning `Task(Str, A)`
- `Ashes.Task.fromResult(result)` returning `Task(E, A)`
- `Ashes.Task.sleep(ms)` returning `Task(Str, Int)`
- `Ashes.Task.all(tasks)` returning `Task(E, List(A))`
- `Ashes.Task.race(tasks)` returning `Task(E, A)`
- `Ashes.Task.spawn(task)` returning `Unit`

Shipped standard-library modules under the reserved `Ashes` namespace also include:

- `Ashes.Collection.List`
- `Ashes.Core.Maybe`
- `Ashes.Core.Result`
- `Ashes.Text`
- `Ashes.Test`

Built-in runtime types available without import include:

- `Unit`
- `Maybe(T)`
- `Result(E, A)`
- `Socket`
- `Task(E, A)`
- `TlsSocket`

`Ashes` is reserved for compiler-provided modules and cannot be redefined by user code.
The reserved `Ashes` namespace is a module root, not a direct alias surface for
`print`, `panic`, or `args`; those live under `Ashes.IO` only.

### 1.1 File Structure and Top-Level Declarations

A source file is a flat sequence of imports, an optional export declaration, then top-level declarations, then an
optional trailing expression:

```text
file        ::= import* export-declaration? declaration* expr?
export-declaration ::= "export" "(" export-item ("," export-item)* ","? ")"
export-item ::= "value" ident
              | "type" upper-ident
              | "type" upper-ident "(" ".." ")"
              | "type" upper-ident "(" upper-ident ("," upper-ident)* ")"
              | "module" upper-ident
declaration ::= let | letrec | type | external | capability | provide | trait | implement
letrec      ::= "let" "recursive" binding ("and" binding)*
```

- `import` lines come first (see §13.1), followed by at most one `export` declaration.
- `declaration` is a top-level `let`, a `let recursive ... and ...` group, a `type`
  declaration, an `external` declaration, a capability/provider declaration, or a trait/implementation
  declaration. Top-level `let`/`let recursive` declarations
  do **not** take a trailing `in`; their scope is the rest of the file.
- The optional trailing `expr` is the program's entry point.

Example:

```ash
import Ashes.IO

type Color =
    | Red
    | Green

let name = "world"

let recursive even = given (n) -> if n == 0 then true else odd(n - 1)
and odd = given (n) -> if n == 0 then false else even(n - 1)

Ashes.IO.print("hello " + name)
```

#### Sequential scoping (Model A)

Top-level binding scope is **sequential**, following OCaml/F# ordering:

- Each top-level binding is visible to all **subsequent** declarations and to the
  trailing expression.
- A binding is **not** visible to declarations that appear before it. Referring to a
  later binding from an earlier one is a forward-reference error
  (diagnostic `ASH014`).
- Two top-level declarations may not bind the same name (diagnostic `ASH013`).

A plain top-level `let f = ...` cannot refer to itself; self-recursion requires
`let recursive` exactly as in nested `let` bindings (see §6).

#### Mutual recursion (`let recursive ... and ...`)

`let recursive` may be followed by one or more `and` clauses to declare a group of
mutually recursive bindings:

```ash
let recursive even = given (n) -> if n == 0 then true else odd(n - 1)
and odd = given (n) -> if n == 0 then false else even(n - 1)
```

- Every binding in a `let recursive ... and ...` group is visible to **every other**
  binding in the same group (and to all subsequent declarations).
- `and` is only valid as a continuation of a `let recursive`. An `and` clause that does
  not follow a `let recursive` is an error (diagnostic `ASH015`).
- Like nested `let recursive`, each binding in the group is monomorphic within the group
  (no polymorphic recursion; see §6 and §14.2).

#### Trailing expression

- The trailing expression is **optional**. A file containing only declarations is
  legal and, when compiled as a program, produces no output.
- When present, the trailing expression is the program's entry point in a
  single-file program.
- When a file is imported as a module, its trailing expression is **ignored
  entirely** — only its top-level declarations contribute exports (see §13.1).

#### Backward compatibility

Both existing styles remain fully valid:

- A file that is a single expression (today's bare-expression style).
- The nested `let ... in` pyramid style. Nested `let ... in` expressions are
  ordinary expressions and may still appear anywhere an expression is allowed,
  including as the trailing expression of a file with top-level declarations.

---

## 2. Values

### 2.1 Integers

10
42

Integer literals are non-negative decimal values.
Negative integers are written with unary negation, for example `-1`.

Ashes also provides unsigned integer primitive types: `u8`, `u16`, `u32`,
and `u64`.

Unsigned literals use an explicit suffix:

255u8
65535u16
4294967295u32
18446744073709551615u64

Unsigned arithmetic and bitwise operations wrap at the declared bit width.

### 2.1.1 Floats

Ashes has a built-in primitive type:

Float

`Float` represents a 64-bit IEEE-754 floating-point value.

Float literals are decimal values containing a `.`:

0.0
1.0
3.14
0.5

Lexing and parsing rules:

- A numeric literal containing a `.` is a `Float`.
- A numeric literal without a `.` remains an `Int`.
- Exponent notation such as `1e3` is not supported.
- Float suffixes are not supported.
- Negative floats use unary negation, for example `-2.25`.

Float arithmetic and comparisons are introduced in later milestones.

### 2.1.2 Arbitrary-precision integers

Ashes has a native arbitrary-precision signed integer primitive type:

BigInt

`BigInt` is a distinct type from `Int` — there is no implicit conversion. `BigInt` literals use an
`N` suffix and may be any size:

123N
999999999999999999999999999999N

The arithmetic operators `+`, `-`, `*`, `/` (truncated toward zero), `%`, and the comparisons
`==`, `!=`, `<`, `<=`, `>`, `>=` all work on `BigInt` operands (both sides must be `BigInt`):

let squared = 1000000000000N * 1000000000000N

As with `Float`, negation uses subtraction from zero (`0N - x`), not a `-` prefix. `Int↔BigInt`
conversions are `Ashes.Number.BigInt.fromInt` / `Ashes.Number.BigInt.toInt`, and string conversions are
`Ashes.Text.fromBigInt` / `Ashes.Text.parseBigInt` (see
[STANDARD_LIBRARY](standard-library.md#ashesnumberbigint)). Like every value, a `BigInt` is immutable;
each operation allocates a fresh, normalized result.

The `%` (remainder) operator also applies to `Int` and the unsigned types; the result's sign
follows the dividend, matching C `%`.

### 2.2 Strings

“hello”
“world”
“a” + “b”

Strings support concatenation using `+`.

Ashes strings represent UTF-8 text.

Filesystem text APIs operate on UTF-8 encoded files:

- `Ashes.IO.File.readText(path)` returning `Result(Str, Str)`.
- `Ashes.IO.File.writeText(path, text)` returning `Result(Str, Unit)`.
- `Ashes.IO.File.exists(path)` returning `Result(Str, Bool)`.
- Filesystem text is interpreted and written as UTF-8.
- Invalid UTF-8 passed through `Ashes.IO.File.readText` returns `Error(...)`.
- Binary file APIs are not part of the current language surface.

Networking APIs live under `Ashes.Net.Tcp` and `Ashes.Net.Tls`:

- `Ashes.Net.Tcp.connect(host)(port)` returning `Task(Str, Socket)`.
- `Ashes.Net.Tcp.send(socket)(text)` returning `Task(Str, Int)`.
- `Ashes.Net.Tcp.receive(socket)(maxBytes)` returning `Task(Str, Str)`.
- `Ashes.Net.Tcp.close(socket)` returning `Task(Str, Unit)`.
- `Ashes.Net.Tls.connect(host)(port)` returning `Task(Str, TlsSocket)`.
- `Ashes.Net.Tls.send(socket)(text)` returning `Task(Str, Int)`.
- `Ashes.Net.Tls.receive(socket)(maxBytes)` returning `Task(Str, Str)`.
- `Ashes.Net.Tls.close(socket)` returning `Task(Str, Unit)`.
- All networking APIs return `Task(...)` and are consumed via `await`.

Networking rules:

- `connect` supports IPv4 address literals such as `"127.0.0.1"`.
- `connect` may also resolve hostnames through the runtime host-resolution path
    (for example `localhost` and other names available through system host
    configuration).
- Unresolvable hostnames return `Error(...)`.
- `send` attempts to write the full UTF-8 buffer before returning `Ok(bytesWritten)`.
- `receive` reads at most `maxBytes` bytes and returns `Ok("")` on EOF.
- Invalid UTF-8 received from the network returns `Error(...)`.
- `Ashes.Net.Tls.connect` performs a TCP connect followed by a TLS client handshake.
- TLS connections require SNI, hostname verification, and system-trust validation.
- On the current Linux x64, Linux arm64, and Windows x64 backends, `Ashes.Net.Tls`
  uses the same hermetic Mbed TLS runtime path as `https://` in `Ashes.Net.Http`.
- `close` is explicit and deterministic; using a closed `Socket` or `TlsSocket`
  returns `Error(...)`.
- **Automatic cleanup**: `Socket` and `TlsSocket` are **resource types**. The compiler
  automatically releases unclosed networking resources when their binding goes out
  of scope. If a resource is closed explicitly via `Ashes.Net.Tcp.close` or
  `Ashes.Net.Tls.close`, the automatic cleanup is skipped.
- **Use-after-close**: using a networking resource after it has been closed (via
  `send`, `receive`, or a second `close`) is a compile-time error.
- **Double-close**: calling `close` on an already-closed networking resource is a
  compile-time error.

Basic HTTP client APIs live under `Ashes.Net.Http`:

- `Ashes.Net.Http.get(url)` returning `Task(Str, Str)`.
- `Ashes.Net.Http.post(url, body)` returning `Task(Str, Str)`.

Example:

```ash
match Ashes.Task.run(async
  let response = await Ashes.Net.Http.get("http://example.com")
  in response) with
  | Ok(text) -> Ashes.IO.print(text)
  | Error(err) -> Ashes.IO.print(err)
```

Current HTTP rules:

- `http://` and `https://` URLs are supported.
- `https://` defaults to port 443 and, on the current Linux x64,
  Linux arm64, and Windows x64 backends, uses the hermetic Mbed TLS
  runtime linked into TLS-using executables.
- Other backends may still return a runtime error for `https://` until
  their TLS runtime support lands.
- Non-2xx responses return `Error("HTTP <status>")`.
- Chunked transfer encoding is not supported and returns `Error(...)`.
- The successful payload is the response body text after the HTTP header separator.

### 2.3 Booleans

true
false

### 2.4 Tuples

Tuple literals:

(1, "x")
(true, 42, "ok")

Rules:

- Tuples have arity 2 or more.
- `(expr)` is grouping, not a tuple.
- Tuple elements may have different types.

---

## 3. Operators

### 3.1 Arithmetic

`+` is overloaded:

- Integer addition: `1 + 2` evaluates to `3`.
- Float addition: `1.5 + 2.0` evaluates to `3.5`.
- String concatenation: `"a" + "b"` evaluates to `"ab"`.

`-`, `*`, `/` support both `Int` and `Float` when both operands have the same type:

- Integer subtraction: `7 - 2` evaluates to `5`.
- Integer multiplication: `3 * 4` evaluates to `12`.
- Integer division: `7 / 3` evaluates to `2` (integer division). Division by zero (`x / 0`) is a runtime error and aborts evaluation; it does not produce a value.
- Float subtraction: `7.5 - 2.0` evaluates to `5.5`.
- Float multiplication: `3.0 * 4.0` evaluates to `12.0`.
- Float division: `5.0 / 2.0` evaluates to `2.5`.

Unsigned arithmetic operators (`+`, `-`, `*`, `/`) support `u8`, `u16`,
`u32`, and `u64` when both operands have the same unsigned type.

Mixed numeric operators are not allowed:

- `Int op Float` is a type error.
- `Float op Int` is a type error.
- `Int op uN` and `uN op Int` are type errors.
- `uN op uM` with different widths is a type error.

Unary negation is supported for integers:

- `-x` evaluates to the negated integer value of `x`.
- `-expr` binds tighter than `*` and `/`.

### 3.2 Comparison

Comparison operators evaluate to `Bool`.

| Operator | Types               | Description                        |
|----------|---------------------|------------------------------------|
| `>=`     | `Int >= Int`        | Greater than or equal              |
| `>=`     | `Float >= Float`    | Greater than or equal              |
| `>=`     | `uN >= uN`          | Greater than or equal (unsigned)   |
| `<=`     | `Int <= Int`        | Less than or equal                 |
| `<=`     | `Float <= Float`    | Less than or equal                 |
| `<=`     | `uN <= uN`          | Less than or equal (unsigned)      |
| `==`     | `Int == Int`        | Equal (integers)                   |
| `==`     | `Float == Float`    | Equal (floats)                     |
| `==`     | `uN == uN`          | Equal (unsigned integers)          |
| `==`     | `Str == Str`        | Equal (strings, byte-for-byte)     |
| `==`     | `Bool == Bool`      | Equal (booleans)                   |
| `!=`     | `Int != Int`        | Not equal (integers)               |
| `!=`     | `Float != Float`    | Not equal (floats)                 |
| `!=`     | `uN != uN`          | Not equal (unsigned integers)      |
| `!=`     | `Str != Str`        | Not equal (strings, byte-for-byte) |
| `!=`     | `Bool != Bool`      | Not equal (booleans)               |

Examples:

10 >= 5         // => true
3 <= 3          // => true
1 == 1          // => true
1 != 2          // => true
"hi" == "hi"    // => true
"hi" != "bye"   // => true
true == false   // => false

Both operands of `==` and `!=` must have the same type. Mixing `Int` and `Str` is a type error.

### 3.3 Logical negation

Unary `!` performs strict logical negation:

```ash
!true        // => false
!false       // => true
!!true       // => true
```

The operand must have type `Bool`, and the result has type `Bool`. Ashes has no implicit truthy or
falsey conversion for other values. Unary `!` is right-associative and has the same precedence as
unary `-` and bitwise `~`. The lexer treats `!=` as one inequality operator, not as unary `!`
followed by `=`.

### 3.4 Trait-backed operators across types

Operators select their standard trait implementation from the operand type. A function using an
operator at an abstract type therefore infers the corresponding ordinary trait constraint and may
be used at every type with a coherent implementation:

```ash
let eq : a -> a -> Bool requires {Eq(a)} = given (a) -> given (b) -> a == b
let x = eq(3)(3)          // Int
let y = eq("hi")("hi")    // Str
let z = eq(true)(true)    // Bool
```

Primitive implementations keep their specialized IR instructions. Nominal implementations use the
same statically selected dictionary evidence as an explicit trait-method call. See
[Traits and Implementations](#21-traits-and-implementations) for the complete operator mapping.

### 3.5 Bitwise

Bitwise operators operate on integer values (`Int`, `u8`, `u16`, `u32`,
`u64`) and return the same type as the left operand.

| Operator | Types        | Description           |
|----------|--------------|-----------------------|
| `&`      | `T & T`      | Bitwise AND           |
| `\|`     | `T \| T`     | Bitwise OR            |
| `^`      | `T ^ T`      | Bitwise XOR           |
| `<<`     | `T << T`     | Shift left            |
| `>>`     | `T >> T`     | Logical shift right   |

Where `T` is `Int` or one unsigned integer type (`u8`, `u16`, `u32`, `u64`).

Shift counts are masked to the low 6 bits for the 64-bit `Int`
representation.

### 3.6 Cons

`::` constructs a new list by prepending a head value to a tail list.

Example:

```text
1 :: [2,3]  // => [1,2,3]
```

### 3.7 Pipes

Ashes supports three left-to-right pipeline operators.

`|>` forwards the value on the left as the first argument to the function on the right.

Examples:

x |> f          // => f(x)
x |> f |> g     // => g(f(x))

`|?>` is the Result-success pipeline operator.

If the left side evaluates to `Ok(v)`, the function on the right is applied to `v`.
If the left side evaluates to `Error(e)`, the error is propagated unchanged.

`|?>` supports both of these forms:

- `Result(E, A) |?> (A -> B)` produces `Result(E, B)` by wrapping the mapped value in `Ok`.
- `Result(E, A) |?> (A -> Result(E, B))` produces `Result(E, B)` by flattening the returned `Result`.

`|!>` is the Result-error mapping pipeline operator.

If the left side evaluates to `Ok(v)`, the success value is preserved.
If the left side evaluates to `Error(e)`, the function on the right is applied to `e` and the result is wrapped back in `Error`.

`let?` is Result-binding syntax.

It evaluates a `Result(E, A)` expression, binds the `Ok` payload inside the body, and propagates `Error(e)` unchanged.

Example:

```ash
let bumpIfOk result =
    let? n = result
    in
    Ok(n)
```

This is equivalent to:

```ash
let bumpIfOk result =
    match result with
        | Ok(n) -> Ok(n)
        | Error(e) -> Error(e)
```

Use `|?>` when a pipeline style is clearer and intermediate names are not needed.
Use `let?` when sequential named Result values improve readability.
Use `match` when success and error branches must be handled explicitly.

Both Result workflows are valid:

let bumpIfOk1 result =
    result
    |?> (given (n) -> n + 1)

let bumpIfOk2 result =
    let? n = result
    in
    Ok(n + 1)

### 3.8 Precedence and Associativity

From lowest precedence to highest:

| Level | Operators                      | Associativity |
|-------|--------------------------------|---------------|
| 1     | `|>`, `|?>`, `|!>`             | left          |
| 2     | `>=`, `<=`, `==`, `!=`         | left          |
| 3     | `\|`                           | left          |
| 4     | `^`                            | left          |
| 5     | `&`                            | left          |
| 6     | `::`                           | right         |
| 7     | `<<`, `>>`                     | left          |
| 8     | `+`, `-`                       | left          |
| 9     | `*`, `/`                       | left          |
| 10    | unary `-`, `!`, `~`            | right         |
| 11    | function application           | left          |

`>=`, `<=`, `==`, and `!=` share the same precedence level in the current grammar.
Function application (both `f(x)` and `f x` whitespace syntax) binds tighter than
all operators above.

---

## 4. Type Declarations

Algebraic data types are declared with `type`.

Syntax:

type TypeName =
    | Constructor1
    | Constructor2(Param1)
    | Constructor3(Param1, Param2)

Constructors may also be written on a single line:

type TypeName = | Constructor1 | Constructor2(Param1)

Examples:

type Color =
    | Red
    | Green

type Result(E, A) =
    | Ok(A)
    | Error(E)

`Result` is a built-in runtime type. User code may use `Ok(...)` and `Error(...)`
directly, and helper functions are available from the shipped `Ashes.Core.Result` module.

A constructor payload is a full **type expression**, not just a simple name:

type Reply =
    | Full(HttpResponse)
    | Streamed(Str -> Task(Str, Str), Str)   // a function field and a parameterized field

Payload types may be simple names (`Int`, `a`), the declaring type applied to its own
parameters written bare (`Node(Tree, Int, Tree)` inside `type Tree(a)`), other user or
built-in types applied to their arguments (`List(Int)`, `Maybe(a)`, `Task(E, A)`), function
types (`Int -> Str`, including a `needs` row), and tuples (`(Int, Str)`).

Generic ADTs declare their type parameters explicitly after the type name.
For migration compatibility, code may omit explicit type parameters and rely on
constructor payload names, but canonical Ashes source should declare them explicitly.
When type parameters are omitted, a payload name is an **implicit type parameter only when
it denotes no known type**. A payload naming the declaring type itself (a self-recursive
field, e.g. `Node(Tree, Int, Tree)`), a primitive (`Int`, `Bool`, `Str`, `Bytes`, `Float`),
or any other declared user/built-in type is a concrete field type. This lets a self-recursive
ADT be built by a recursive function (`let recursive build n = … Node(build(n - 1)) …`)
without over-generalizing the self-referential field, and lets one ADT wrap another
concretely (`type AppError = | Json(JsonError)` refers to the `JsonError` type, not a fresh
parameter). Referencing a parameterized type without its arguments (a bare `List` or bare
`JsonError` where `JsonError` takes a parameter) is an arity error — apply the arguments
(`List(Int)`), except for the declaring type's own name, which is shorthand for itself applied
to its parameters.

Rules:

- The type name should begin with an uppercase letter by convention.
- Each constructor is introduced by `|`.
- Constructors may have zero or more payload parameters in parentheses; each payload is a type
  expression (a name, a parameterized type, a function type, or a tuple).
- Type declarations appear before the expression body of the program.

### 4.1 Transparent Type Aliases

A transparent alias gives a reusable name to a type expression without introducing a new type:

```ash
type alias Identifier(a) = a
type alias Handler(E, A) = E -> Task(E, A)
```

`alias` is contextual after `type`, not a reserved keyword; it remains available as an ordinary
identifier everywhere else.

Alias parameters are in scope only in the right-hand type expression. An application must supply
exactly the declared number of arguments. During type checking an alias application expands to its
right-hand side with those arguments substituted, so `Identifier(Int)` and `Int` are the same type.
An alias introduces no constructor, value, runtime symbol, allocation, layout, or ABI distinction.
Diagnostics and editor hovers may show both the written alias and its expansion when that makes an
error clearer.

Aliases may refer to aliases declared later in the same compilation unit, but the expansion graph
must be acyclic. A direct cycle (`type alias Loop = Loop`) or indirect cycle is rejected with
`ASH039`, including the complete cycle path. Alias expansion is shared by annotations, constructor
fields, trait constraints, external signatures, and imported signatures.

### 4.2 Zero-Cost Nominal Types

A zero-cost nominal type uses `type` without an alternative marker and introduces exactly one
constructor with exactly one payload:

```ash
type UserId = UserId(Int)
type Tagged(a) = Tagged(a) deriving {Eq, Show}
```

This is syntactically distinct from an ordinary algebraic type, whose constructors each begin with
`|`: `type Box(a) = | Box(a)` remains a one-constructor ADT with a tag and wrapper allocation, while
`type Box(a) = Box(a)` is a zero-cost nominal type.

`UserId`, `Int`, and any other zero-cost type over `Int` remain distinct during type checking. Conversion is
explicit: construction wraps (`UserId(42)`) and a single-constructor pattern unwraps
(`match id with | UserId(value) -> value`). Zero-cost types support explicit `deriving`, trait
implementations, and the same constructor export controls as ordinary algebraic types. An invalid
shape is rejected with `ASH040`.

After semantic checking, a zero-cost type is represented exactly as its payload: construction and matching
emit no tag, wrapper allocation, or field access. Its size, alignment, calling convention, ownership
capability, and FFI representation are the payload's. Consequently a zero-cost type whose payload is or
contains a resource is affine and follows the payload's deterministic cleanup rules. Nominality is
still enforced at every source-level boundary; representation erasure never permits an implicit
coercion.

### 4.3 Record Types

Record types are single-constructor ADTs with named fields. Records use a
brace-free syntax that mirrors ADT declarations and ordinary constructor calls;
Ashes source never uses curly braces.

Syntax:

```text
type TypeName =  
  | field1: Type1
  | field2: Type2
```

Example:

```ash
type Point =
    | x: Int
    | y: Int
```

A record declaration is a `type ... = | ...` declaration whose alternatives are
`| name: Type` field branches instead of `| Constructor(...)` constructor
branches. A single declaration is either all field branches (a record) or all
constructor branches (an ordinary ADT); the two forms cannot be mixed.

Record values are created with constructor-call syntax using named arguments:

let p = Point(x = 1, y = 2)

Field access uses dot notation (same as module member access):

let px = p.x

Record update creates a new record with one or more fields replaced, using a
brace-free `with` expression:

let p2 = p with x = 5

Multiple fields may be updated in one expression:

let p3 = p with x = 5, y = 6

A parenthesized form remains valid wherever an expression is expected:

let p4 = (p with x = 5)

The base expression is evaluated once; unchanged fields are copied from it.

Rules:

- Record type declarations use `| field: Type` alternatives. Field and
  constructor branches cannot be mixed in a single declaration.
- The single constructor has the same name as the type and cannot be written
  separately.
- Named-argument call syntax (`Name(field = value, ...)`) is only valid for
  record construction. All fields must be provided; field order does not matter.
- Named arguments are not accepted for ordinary (non-record) function calls.
- Field access (`recursive.field`) works on bindings of record types.
- Record update (`base with field = value`) produces a fresh value; the original
  is unchanged. `with` binds looser than function application and the binary
  operators, so `f p with x = a + b` parses as `(f p) with x = (a + b)`.
- Chained updates `base with x = 1 with y = 2` are left-associative and
  equivalent to `(base with x = 1) with y = 2`.
- Records may be used in pattern matching like any ADT: `| Point(x, y) -> ...`.

> Curly braces are not record syntax. A `{` where a record declaration
> (`type T = { f: T }`), literal (`T { f = e }`), or update (`{ base with f = e }`)
> might be written is rejected with a diagnostic pointing to the brace-free forms above.

## 5. Let Bindings

Syntax:

```text
let name = value
in body
```

Example:

```ash
let z = 20
in Ashes.IO.print(z)
```

Bindings are:

- immutable
- scoped to the `in` expression
- expression-based

### 5.1 External Declarations

Top-level `external` declarations expose C ABI functions to Ashes code.

Syntax:

external strlen(Str) -> Int
external getpid() -> Int = "getpid"
external type LLVMModuleRef
external LLVMModuleCreateWithName(Str) -> LLVMModuleRef
external type Database resource destructor databaseClose
external databaseClose(consume Database) -> void = "db_close@libdb"
external databaseVersion(borrow Database) -> Int = "db_version@libdb"
external readConfig(Str) -> Str needs {FileRead} = "read_config@libconfig"

Rules:

- `external` declarations appear before the program body, alongside `type`
  declarations.
- Supported external parameter and return types are `Int`, `u8`, `u16`, `u32`,
  `u64`, `Float`, `f32`, `Bool`, `Str`, opaque external types declared with
  `external type`, and pointers to supported external types using `*T`.
  Unsigned external parameters and returns use Ashes unsigned values at call
  sites and are converted to the requested C ABI width at the external boundary.
- `Float` maps to a C ABI `double`; `f32` maps to a C ABI `float` while using
  Ashes `Float` values at call sites.
- `void` is supported for external return types and produces Ashes `Unit`.
- `Str` arguments are passed to C as null-terminated UTF-8 byte pointers.
- Opaque external types are represented as native pointer-sized words and are
  intended for handles such as LLVM-C references.
- `external type Name resource destructor closeName` classifies an opaque handle as an affine
  resource. `closeName` must name an external function declared in the same file with exactly one
  `consume Name` parameter and a `void` return. The same native symbol is used for explicit close and
  compiler-inserted cleanup.
- A direct resource parameter in an external function must be prefixed with `borrow` or `consume`.
  `borrow` preserves caller ownership; `consume` transfers ownership and rejects later caller use.
  These words are contextual within external parameter lists and are invalid on non-resource types,
  pointers, or return types. Ordinary Ashes functions retain inferred borrowing and have no written
  ownership syntax.
- External resource return values are owned by the caller. Borrowed return views are not part of this
  syntax.
- An external function may end its signature with a closed runtime capability row, for example
  `needs {FileRead}` or `needs {}`. Without an explicit row, calling it requires `UnsafeFfi`.
  External rows may contain only the built-in runtime capabilities from §20.8; user-declared
  capabilities cannot classify a native call. A declared resource destructor is implicitly
  possession-only and therefore has an empty row unless one is written explicitly.
- Pointer external types are represented as native pointers and may be nested for
  C buffer and out-parameter APIs such as `*u8` and `**LLVMModuleRef`.
- The optional string after `=` overrides the C symbol name. A symbol override
  may use `symbol@library` to request a dynamic import from that shared library
  or DLL. Windows external imports require an explicit DLL name.
- Ordinary external functions may be used as first-class function values. An external function that
  borrows, consumes, or returns a declared resource must be called directly so its ownership contract
  remains visible at the call site.

### 5.2 Result Binding

Syntax:

let? name = value
in body

`let?` is syntax sugar for Result propagation.

If `value : Result(E, A)`, then inside `body`, `name : A`.
The overall `let?` expression must itself produce `Result(E, B)`.

Conceptually:

let? x = expr
in body

desugars to:

match expr with
    | Ok(x) ->
        body
    | Error(e) ->
        Error(e)

Rules:

- `value` must have type `Result(E, A)`.
- `body` must have type `Result(E, B)`.
- The propagated error type `E` is preserved unchanged.
- The bound name is only in scope inside `body`.
- In this milestone, the binder target must be an identifier.

### 5.3 Type Annotations

Let bindings may carry an optional type annotation between the name and `=`.

Syntax:

```text
let name : TypeExpr = value
in body
```

Example:

```ash
let x : Int = 42
in Ashes.IO.print(x)
```

The annotation is checked against the inferred type of `value`. A mismatch is a
compile error. Annotations do not alter inference; they serve as documentation and
early error-reporting.

Type expression syntax:

- Primitive names: `Int`, `Float`, `Str`, `Bool`, `Unit`
- Named user types: `Color`, `Point`
- Generic applications: `List(Int)`, `Maybe(Str)`, `Result(Str, Int)`
- Function types: `Int -> Bool`
- Tuple types: `(Int, Str)`

Type annotations are also accepted on `let recursive` bindings.

---

## 6. Recursive Bindings

Recursive bindings must be declared with `recursive`.

Syntax:

```text
let recursive name = value
in body
```

Example:

```ash
let recursive loop i =
    if i >= 10
    then i
    else loop(i + 1)
in Ashes.IO.print(loop(0))
```

Without `recursive`, a binding cannot reference itself.

`let recursive` bindings are **monomorphic**: during inference, the recursive name is bound to a
single monotype (a non-generalized type, which may still contain type variables). This
means the function may not be used at multiple distinct types within its own definition
(no polymorphic recursion). Non-recursive `let` bindings are generalized and may be used
polymorphically.

Self-recursive calls in tail position are guaranteed not to consume additional stack
frames. Tail-position arguments are still evaluated strictly before the recursive jump is
performed. Cross-member tail calls in a `let recursive ... and ...` group are also
constant-stack when the group members share a common parameter shape; non-tail recursive
calls consume one stack frame per active call. See §18.3 for the exact conditions and
stack-depth limits.

---

## 7. Functions

Anonymous functions are declared using `given`.

Syntax:

```text
given (param1, param2, ...) -> expr
given param -> expr
```

Example:

```ash
let add = given (x, y) -> x + y
in Ashes.IO.print(add(10, 5))
```

For a single parameter the parentheses may be omitted: `given x -> x + 1`. The
parenthesized form is canonical (the formatter re-parenthesizes the bare form).

Each parenthesized parameter may carry an inline type annotation:

given (x: Int) -> x + 1
given (b: Body, dt: Float) -> advance(b)(dt)

The annotation unifies with the parameter's inferred type — it pins the
parameter to that type exactly like a whole-binding annotation would, so it can
resolve otherwise-ambiguous code (record field access on the parameter, Float
operator selection) as well as document intent. A mismatch between the
annotation and how the parameter is used is a compile error. The bare
(unparenthesized) single-parameter form does not take an annotation.

Functions:

- are first-class values
- may be passed as arguments
- may return functions
- may capture outer variables (closures)

### 7.1 Function Application

Ashes supports two equivalent syntaxes for calling functions.

#### Parenthesized Application

The traditional syntax wraps arguments in parentheses:

```ash
f(x)
f(x, y)
f(x)(y)
f()
Ashes.IO.print(42)
```

Multiple parenthesized arguments are syntax sugar for curried application:

```ash
f(x, y)
```

is equivalent to:

```ash
f(x)(y)
```

This also extends to longer calls:

```ash
f(a, b, c)
```

is equivalent to:

```ash
f(a)(b)(c)
```

An empty argument list is sugar for passing the built-in `Unit` value:

```ash
f()
```

is equivalent to:

```ash
f(Unit)
```

#### ML-style (Whitespace) Application

Arguments may also be passed using whitespace, without parentheses:

```ash
f x
f x y
Ashes.IO.print 42
Ashes.IO.print "hello"
Ashes.IO.print "hello"
```

This is pure syntax sugar. `f x y` is parsed identically to `f(x)(y)` — both
produce the same call structure and semantics. The only AST difference is a
formatting-only flag that records whether whitespace application was used.

#### Left Associativity

Function application is left-associative:

```ash
f x y
```

parses as:

```ash
((f x) y)
```

which is equivalent to `f(x)(y)`.

#### Precedence

Function application binds tighter than all binary operators:

```ash
f x + y
```

parses as:

```ash
(f x) + y
```

not `f (x + y)`.

#### Valid Whitespace Arguments

The following tokens may appear as whitespace arguments (without parentheses):

- identifiers: `f x`
- integer literals: `f 42`
- string literals: `f "hello"`
- boolean literals: `f true`, `f false`
- list literals: `f [1, 2, 3]`

Complex expressions must be parenthesized:

```ash
f (1 + 2)
Ashes.IO.print (add 3 4)
```

Keywords such as `then`, `else`, `in`, `with`, `|` are never treated as
whitespace arguments.

#### Examples

```ash
let id x = x
in Ashes.IO.print (id 42)
let add x y = x + y
in Ashes.IO.print (add 3 4)
let recursive loop x y =
    if x >= 100000
    then y
    else loop (x + 1) (y + 1)
in Ashes.IO.print (loop 0 0)
```

### 7.2 Parameter Sugar

A binding may list its parameters directly to the left of `=`. This is pure
syntax sugar for a binding whose value is a chain of nested `given` lambdas — each
parameter desugars to exactly one `given` layer:

let f a b = body

is exactly equivalent to:

let f = given (a) -> given (b) -> body

So `let id x = x` is `let id = given (x) -> x`, and a curried two-argument binding
spells the nested-lambda form more compactly:

let recursive sum lst acc =
    match lst with
        | [] -> acc
        | x :: rest -> sum(rest)(acc + x)
in Ashes.IO.print(sum([1, 2, 3])(0))

The sugar applies to plain `let`, `let recursive`, and nested `let ... in` bindings
alike. Parameter sugar is the idiomatic way to write a named function; the
explicit `given` form remains valid and is what the sugar expands to.

A sugar parameter may carry an inline type annotation by parenthesizing it:

let energy (b: Body) = b.mass * speedSquared(b)
let scale (v: Float) n = v * Ashes.Number.Math.toFloat(n)

An annotated sugar parameter desugars to a `given` layer carrying the same
annotation (`let energy = given (b: Body) -> ...`); unannotated parameters stay
bare. The parentheses are required exactly when an annotation is present.

---

## 8. If Expressions

Syntax:

```text
if condition
then whenTrue
else whenFalse
```

Example:

```ash
if 10 >= 10
then "true"
else "false"
```

All branches must return compatible types.

---

## 9. Lists

Lists are immutable linked lists. All list operations — cons (`::`),
`Ashes.Collection.List.append`, `Ashes.Collection.List.map`, `Ashes.Collection.List.filter`, etc. —
return a **new** list. The original list is never modified.

### 9.1 Empty List

```ash
[]
```

### 9.2 List Literals

```ash
[1,2,3]
[“a”,“b”]
```

All elements must have the same type.

Mixed-type lists are invalid.

---

## 10. Cons Operator

Syntax:

```text
x :: xs
```

Meaning:

Construct a new list by placing `x` at the front of `xs`.

Example:

```text
1 :: [2,3]      // => [1,2,3]
```

Type rule:

If:

```text
x : T
xs : List<T>
```

Then:

```text
x :: xs : List<T>
```

---

## 11. Pattern Matching

Pattern matching is performed using `match`.

Syntax:

```text
match value with
| pattern1 -> expr1
| pattern2 -> expr2
```

Each arm may include an optional guard:

```text
| pattern when condition -> expr
```

---

### 11.1 List Patterns

Supported patterns:

```text
[]
x :: xs
```

Meaning:

- `[]` matches the empty list
- `x :: xs` matches non-empty lists
  - `x` binds the first element
  - `xs` binds the remainder

Example:

```ash
match xs with
  | [] -> 0
  | x :: rest -> x
```

Exhaustiveness:

- List matches must be exhaustive.
- A match over a list must cover both structural shapes:
  - `[]`
  - `x :: xs`
- A catch-all arm (such as `_` or a non-constructor variable pattern) also
  satisfies exhaustiveness.

---

### 11.2 Tuple Patterns

Tuple patterns destructure tuples by position.

Syntax:

```text
| (p1, p2, …) -> expr
```

Rules:

- Tuple pattern arity must match tuple value arity.
- Subpatterns are type-checked recursively.

Example:

```ash
match p with
  | (a, b) -> a
```

---

### 11.3 Wildcard Pattern

`_` matches any value and binds nothing.

Example:

```ash
match xs with
  | [] -> 0
  | _ -> 1
```

The wildcard can appear in any pattern position, including inside constructor patterns:

```ash
match opt with
| None -> 0
| Some(_) -> 1
```

If a wildcard (or other catch-all pattern such as a non-constructor variable pattern)
appears in a `match`, all later arms are unreachable and are rejected.

---

### 11.4 Constructor Patterns

Constructor patterns destructure algebraic data type values.

Syntax:

| ConstructorName -> expr
| ConstructorName(p1, p2, …) -> expr

Rules:

- A nullary constructor name (e.g. `None`) matches values produced by that constructor.
- A constructor with payload (e.g. `Some(x)`) matches values produced by that constructor
  and binds its payload to the pattern variable(s).
- The argument count in the pattern must match the constructor arity.
- Pattern variables introduced in the payload are scoped to the branch body only.

Example:

```ash
import Ashes.Core.Maybe

let unwrapOr opt def =
    match opt with
        | None -> def
        | Some(x) -> x
in Ashes.IO.print(unwrapOr(Some(10))(0))
```

Errors:

- Unknown constructor name in pattern.
- Wrong number of pattern arguments (arity mismatch).
- Redundant constructor arm: if an earlier arm already matches a constructor, a later arm
  for the same constructor is unreachable and rejected.

> **Note**
>
> In the current implementation, values of user-declared algebraic data types (and
> the generic builtin ADTs `Maybe`, `Result`, and `Unit`) are tagged cells. Each cell
> stores a constructor tag (its 0-based index in the declaring type) at offset 0,
> followed by any payloads at subsequent 8-byte offsets.
>
> - Nullary constructors: 8-byte cell `[tag]`.
> - Arity-`n` constructors: `8 * (1 + n)` byte cell `[tag, payload0, ..., payload(n-1)]`.
>
> Pattern matching reads the tag from the cell and compares it against the expected
> tag for the matched constructor, so different constructors are always distinguishable
> and payload values such as `0` or negative integers are safely represented.
>
> Cells use the same payload layout whether they are RC-managed, scoped-region,
> stack-allocated, or a reused dead unique cell. An RC allocation adds its common
> header before the value pointer; the tag and field offsets shown above do not move.
>
> A few builtin types use dedicated unboxed representations rather than the tagged-cell
> scheme: `Bool` is an immediate integer (`0`/`1`); the empty list is the immediate
> integer `0` and a cons cell is an untagged 16-byte `[head, tail]` block; and tuples
> are untagged, storing their elements at consecutive 8-byte offsets. Matching on these
> uses value or null tests rather than a tag comparison.
>

### 11.5 Integer Literal Patterns

Integer literal patterns match values by equality.

Syntax:

```text
| 0 -> expr0
| 1 -> expr1
| n -> exprDefault
```

Rules:

- An integer literal pattern matches when the matched value equals the literal.
- Integer literal patterns may be mixed with variable and wildcard patterns.
- Integer patterns alone are never exhaustive (integers are unbounded); a catch-all
  arm (`_` or a variable) is required.

Example:

```ash
match n with
  | 0 -> "zero"
  | 1 -> "one"
  | _ -> "other"
```

Negative integers are supported:

```ash
match n with
  | -1 -> "negative one"
  | 0 -> "zero"
  | _ -> "positive"
```

---

### 11.6 String Literal Patterns

String literal patterns match values by equality.

Syntax:

```text
| "hello" -> expr1
| "world" -> expr2
| s -> exprDefault
```

Rules:

- A string literal pattern matches when the matched value equals the literal.
- String patterns alone are never exhaustive; a catch-all arm is required.

Example:

```ash
match greeting with
| "hello" -> "English"
| "hola" -> "Spanish"
| _ -> "unknown"
```

---

### 11.7 Boolean Literal Patterns

Boolean literal patterns match `true` or `false`.

Syntax:

```text
| true -> expr1
| false -> expr2
```

Rules:

- Boolean literal patterns match when the value equals the literal.
- A match covering both `true` and `false` is exhaustive.

Example:

```ash
match flag with
| true -> "yes"
| false -> "no"
```

---

### 11.8 Named Record Patterns

Named record patterns match a record by nominal type and field name. They use the same braces and
`field = value` spelling as record construction:

```ash
match point with
    | Point { x = horizontal, y = vertical } -> horizontal + vertical
```

Fields may be written in any order and may be omitted. An omitted field is ignored; there is no
implicit binding shorthand in this milestone. The name before `{` must resolve to the record's
nominal type, every written field must exist, and a field may appear at most once. Named record
patterns compose recursively with tuple, list, constructor, literal, as-, and or-patterns.

### 11.9 As-Patterns

An as-pattern binds the complete matched value in addition to the bindings introduced by its inner
pattern:

```ash
match values with
    | head :: tail as whole -> (head, tail, whole)
```

`as` binds less tightly than `::`, so the example means `(head :: tail) as whole`. The name after
`as` must be a lower-case identifier other than `_`, and it must not duplicate another binder in the
same pattern.

For ordinary immutable values the alias is shared according to the usual inferred ownership rules.
For affine values, an as-pattern may not introduce two independently consuming bindings for the same
resource: if the alias owns a resource-bearing aggregate, nested resource-bearing fields must be
ignored rather than bound. Violations use the existing affine-use diagnostic instead of permitting a
double drop.

### 11.10 Or-Patterns

An or-pattern shares one guard and body between alternatives:

```ash
match option with
    | Some(value) | Fallback(value) when value > 0 -> value
    | _ -> 0
```

`|` has the lowest pattern precedence. `as` binds more tightly than `|`, while `::` binds more tightly
than `as`. Parentheses may make any grouping explicit. Every alternative must bind exactly the same
set of names, and each same-named binder must infer to the same type. A mismatch is a compile-time
error. Duplicate binders inside one alternative remain errors.

Alternatives are tried from left to right without re-evaluating the scrutinee. Once an alternative
matches, its bindings are established and the arm guard is evaluated exactly once. A false guard
continues with the next match arm; it does not try another alternative from the same or-pattern.
Or-patterns contribute the union of their alternatives to exhaustiveness and redundancy analysis.
An alternative already covered by an earlier alternative or arm is redundant, while later arms are
unreachable only when the accumulated unguarded alternatives cover them.

The complete precedence grammar is:

```text
pattern         ::= or-pattern
or-pattern      ::= as-pattern ("|" as-pattern)*
as-pattern      ::= cons-pattern ("as" LOWER_IDENT)?
cons-pattern    ::= primary-pattern ("::" cons-pattern)?
primary-pattern ::= "_" | LOWER_IDENT | literal-pattern | "[]"
                  | UPPER_IDENT ("(" pattern ("," pattern)* ")")?
                  | UPPER_IDENT "{" record-pattern-field
                      ("," record-pattern-field)* ","? "}"
                  | "(" pattern ("," pattern)+ ")"
                  | "(" pattern ")"
record-pattern-field ::= LOWER_IDENT "=" pattern
```

View or active patterns are not part of the language; matching never invokes an arbitrary function.

### 11.11 Pattern Guards

Match arms can include an optional `when` guard clause that adds a boolean
condition to the pattern.

Syntax:

```text
| pattern when condition -> expr
```

Semantics:

1. The pattern is matched first.
2. If the pattern matches, the `when` condition is evaluated.
3. If the condition is `true`, the branch expression is executed.
4. If the condition is `false`, matching continues with the next arm.

The guard expression has access to all bindings introduced by the pattern.

Example:

```ash
match x with
| n when n >= 10 -> "big"
| _ -> "small"
```

Example with constructor patterns:

```ash
type Outcome =
    | Good(Int)
    | Bad(String)

match outcome with
  | Good(n) when n >= 100 -> "excellent"
  | Good(n) -> "ok"
  | Bad(e) -> e
```

Exhaustiveness:

- A guarded arm does not count toward exhaustiveness, because the guard
  may be `false`. A match must still have unguarded arms that cover all
  patterns.
- A guarded catch-all pattern (e.g. `_ when cond`) does not make
  subsequent arms unreachable.

Desugaring model:

`| p when cond -> expr` behaves like:

```text
| p -> if cond then expr else <continue to next arm>
```

Pattern guards are syntax sugar — no new evaluation rules are introduced.

---

### 11.12 Let Pattern Bindings

Let expressions support destructuring patterns on the left side of `=`.

Syntax:

```text
let (a, b) = expr in body
let x :: xs = expr in body
```

Rules:

- Only irrefutable patterns (patterns that always match) are allowed.
- Tuple patterns are irrefutable when the arity matches.
- Variable patterns and wildcard patterns are irrefutable.
- Constructor patterns (e.g. `Some(x)`) are not allowed in let bindings
  because they are refutable — use `match` instead.
- Named record patterns are irrefutable when their nominal record type is known and every nested
  field pattern is irrefutable. As-patterns are irrefutable when their inner pattern is irrefutable.
- Or-patterns are not allowed in let bindings; use `match` when alternatives are required.
- Integer, string, and boolean literal patterns are not allowed in let
  bindings because they are refutable.
- List cons patterns (`x :: xs`) are allowed but will fail at runtime
  if the list is empty.

Example:

```ash
let (x, y) = (1, 2)
in x + y

let first :: rest = [1, 2, 3]
in first
```

---

## 12. Recursion over Lists

Example:

```ash
let recursive sum lst acc =
    match lst with
        | [] -> acc
        | x :: rest -> sum(rest)(acc + x)
in Ashes.IO.print(sum([1, 2, 3])(1))
```

Default-value list utilities are written as regular user code, for example:

```ash
let recursive lastOr xs default =
    let recursive loop ys =
        match ys with
            | [] -> default
            | x :: rest ->
                match rest with
                    | [] -> x
                    | _ -> loop(rest)
    in loop(xs)
in Ashes.IO.print(lastOr([1, 2, 3])(0))
```

---

## 13. Standard Library Modules

Ashes has **no implicit prelude**. All standard library functions live under the
`Ashes` namespace and must be accessed explicitly.

### 13.1 Accessing Standard Library

Ashes has no implicit standard-library open. Unqualified `print`, `panic`, and
`args` are available only after `import Ashes.IO`, while the qualified
`Ashes.IO.*` forms are always valid.

`write` and `writeLine` remain qualified-only and must be called as
`Ashes.IO.write(...)` and `Ashes.IO.writeLine(...)`.

There are two common ways to use standard library functions:

#### Qualified access (no import required)

```ash
Ashes.IO.print "hello"
Ashes.IO.panic "boom"
Ashes.IO.args
```

#### Import and use unqualified names

```ash
import Ashes.IO
print "hello"
panic "boom"
```

`import Module` brings the module's exported names into local scope. The import
must appear at the top of the source file, before any expressions.

#### Import Aliasing

An import may include an alias using `as`:

```ash
import Ashes.IO as IO
IO.print "hello"
```

The alias is a short name that can be used in place of the full module path for
qualified access.  Unqualified access to the module's exported names is still
available (e.g. `print "hello"` still works after `import Ashes.IO as IO`).

The alias must be a valid identifier (letter followed by alphanumerics/underscores).
Aliases may be lowercase (e.g. `import Ashes.Collection.List as list`).

For multi-segment module imports, both full and short qualification are supported:

- `import Foo.Bar` allows `Foo.Bar.value`.
- `import Foo.Bar` also allows `Bar.value` when `Bar` is the unique imported leaf
    module qualifier.
- `import Foo.Bar as FB` also allows `FB.value`.
- If two imported modules share the same exported name, unqualified access is a
    compile-time error.
- If two imported modules share the same leaf qualifier, short qualification is a
    compile-time error and full qualification must be used.

Both styles may be mixed freely.

#### Import Selectors

In addition to whole-module imports (`import M` and `import M as X`), an import may
select an individual binding or type from a module. A selector brings the selected
name into scope **unqualified**:

| Form | Brings into scope |
|------|-------------------|
| `import M` | module `M` (qualified access; exported names also unqualified) |
| `import M as X` | module `M` under alias `X` |
| `import M.binding` | `binding` (unqualified) |
| `import M.binding as x` | `binding` under unqualified name `x` |
| `import M.Type` | `Type` (unqualified) |
| `import M.Type as T` | `Type` under unqualified name `T` |

Examples:

```ash
import Ashes.IO.print
import Ashes.Collection.List.map as listMap
import Ashes.Core.Result.Result as R
```

Rules:

- `import M.name` makes `name` (a binding or type exported by `M`) available
  unqualified. `import M.name as alias` makes it available as `alias` instead.
- The selected name must be an export of `M` (see “Module Exports” below).
- If two unqualified selectors bring the **same** unqualified name into scope, it is
  a compile-time error (diagnostic `ASH016`). Resolve the conflict with `as`.
- Selector imports compose with whole-module imports; the same module may be imported
  both wholesale and via selectors.

Built-in standard-library modules (`Ashes.IO`, `Ashes.Collection.List`, `Ashes.Text`,
`Ashes.Core.Result`, `Ashes.Core.Maybe`, etc.) resolve through the **identical** path as
user modules: each module has a known set of exported bindings and types, and
selectors are checked against that set. For example `import Ashes.IO.print` and
`import Ashes.IO.print as p` behave exactly like selectors against a user module.

#### Module Exports

An optional export declaration defines a module's complete public interface. It follows imports and
precedes every other declaration:

```ash
export (
    value empty,
    value insert,
    type Map,
    type Color(Red, Green),
    type Maybe(..),
    module InternalApi,
)
```

- `value name` exports one top-level `let` or member of a top-level recursive group.
- `type Name` exports a type abstractly. Importers may use the nominal type in annotations,
  signatures, constraints, and implementations, but cannot construct or pattern-match it.
- `type Name(..)` exports a type and every constructor. `type Name(C1, C2)` exports only the listed
  constructors. A record constructor is governed by the same rule as an ordinary ADT constructor.
- `module Name` exports a direct nested inline module. Nested members are then governed by that
  module's own export declaration (or its compatibility export-all interface).

An export declaration may contain a trailing comma. Each value, type, module, and constructor may be
listed at most once. Every entry must name a declaration in that module; constructors must belong to
the named type. Duplicate and unknown entries are compile-time errors (`ASH037` and `ASH038`). The
declaration is an interface only: code inside the module retains access to every local declaration and
constructor. Hidden names are reported to importers with the same unknown-export diagnostic as names
that do not exist, so the interface does not reveal private implementation details.

When a file has no export declaration, its compatibility interface exports:

- all top-level `let` bindings,
- all bindings of top-level `let recursive ... and ...` groups, and
- all top-level `type` declarations (and their constructors).

The following are **never** exported:

- `external` declarations, and
- the trailing expression.

There is **no implicit re-export**: names a module itself imported from other
modules are not re-exported to its importers. Each importer must import what it
needs directly.

#### Inline Modules

A file may declare **inline modules** — nested, named namespaces — directly in
its body, so related `let` / `type` declarations can be grouped under a
qualifier without spawning a new file. This is purely a compile-time namespacing
feature: an inline module has no runtime representation, is not a value, and is
erased during lowering exactly as a file module is.

```ash
module Geometry =
    let pi = 3.14159
    let area = given (r) -> pi * r * r

Ashes.IO.print(Ashes.Text.fromFloat(Geometry.area(2.0)))
```

- **Introducer.** `module Name =` (a capitalized `UpperCamel` name), followed by
  a **layout block**: the run of lines indented past the `module` keyword. The
  block ends at the first line dedented back to (or past) the `module` column —
  the same column rule the parser uses to find the next top-level item. A trailing
  line comment (`// …`) after the `=` is permitted. `module` is recognized only in
  this declaration position; it remains an ordinary identifier elsewhere.
- **Members.** An optional leading `export` declaration, followed by `let`, `let recursive ... and ...`, `type`, and nested `module`
  declarations — the same forms a file may contain. A `module` block may **not**
  contain a trailing expression or an `external` declaration.
- **Identity.** An inline module is an **exported submodule** of its file:
  `File.Inner.member` is path-addressable from other files, so promoting an
  inline module to its own file (`File/Inner.ash`) leaves every `import` and call
  site unchanged. A separate inline module and a file that resolve to the *same*
  path are a compile-time ambiguity error.
- **Exports.** Identical to file modules (above): an explicit interface is honored when present;
  otherwise all top-level bindings and types with their constructors, plus nested modules, are
  exported. No implicit re-export.
- **Scoping (Model A).** Inside a block the same sequential rule as the top level
  holds: a declaration sees earlier declarations in the block, never later ones;
  self-recursion needs `let recursive`, mutual recursion `let recursive ... and`.
  Like a file module, an inline module does not implicitly capture the enclosing
  file's unqualified bindings — it reaches other namespaces the same way any
  module does, by qualified access or `import`. This is what keeps inline ↔ file
  promotion transparent.
- **Access.** By qualified path (`Geometry.area`), or by bringing names in with
  the ordinary `import` machinery — whole-module (`import Geometry`), alias
  (`import Geometry as G`), or selector (`import Geometry.area as a`, including
  through nesting: `import Json.Parse.value as pv`).
- **Reserved.** An inline module may not be named `Ashes` or shadow any `Ashes.*`
  path.

Diagnostics `ASH021`–`ASH024` cover the inline-module surface (see
[Diagnostics Reference](diagnostics.md)); unknown-member, unknown-selector, and
import-collision cases reuse `ASH013`–`ASH016`, since inline modules resolve
through the same path as file modules.

### 13.2 Ashes.IO Module

The built-in `Ashes.IO` module exports:

- `print(expr)` - prints the evaluated expression to standard output.
- `panic("message")` - prints the message and aborts with a non-zero exit code.
  Has Never/Bottom behavior, so it typechecks in any expression context.
- `args` - a `List(Str)` containing command-line arguments passed to the
  compiled program (excluding the executable path/name at `argv[0]`).
- `write("text")` - writes a string to standard output without adding a newline.
- `writeLine("text")` - writes a string to standard output and then writes `\n`.
- `readLine()` - reads one line from standard input and returns `Some(line)` or `None` on EOF.

Other built-in runtime modules are also always available through qualified access:

- `Ashes.IO.File.readText(path)` returning `Result(Str, Str)` - UTF-8 file read.
- `Ashes.IO.File.writeText(path, text)` returning `Result(Str, Unit)` - UTF-8 file write.
- `Ashes.IO.File.exists(path)` returning `Result(Str, Bool)` - filesystem existence check.
- `Ashes.Text.uncons(text)` returning `Maybe((Str, Str))` - split one Unicode scalar from the front of a string.
- `Ashes.Text.parseInt(text)` returning `Result(Str, Int)` - parse a decimal integer with optional leading `-`.
- `Ashes.Text.parseFloat(text)` returning `Result(Str, Float)` - parse a decimal float with optional fraction and exponent.
- `Ashes.Text.fromInt(value)` returning `Str` - format an integer as decimal text.
- `Ashes.Text.fromFloat(value)` returning `Str` - format a float as decimal text.
- `Ashes.Text.toHex(value)` returning `Str` - format an integer as lowercase hexadecimal text with a `0x` prefix.
- `Ashes.Net.Tcp.connect(host)(port)` returning `Task(Str, Socket)` - async TCP connect.
- `Ashes.Net.Tcp.send(socket)(text)` returning `Task(Str, Int)` - async TCP send.
- `Ashes.Net.Tcp.receive(socket)(maxBytes)` returning `Task(Str, Str)` - async TCP receive.
- `Ashes.Net.Tcp.close(socket)` returning `Task(Str, Unit)` - explicit async socket close.
- `Ashes.Net.Tls.connect(host)(port)` returning `Task(Str, TlsSocket)` - async TLS connect.
- `Ashes.Net.Tls.send(socket)(text)` returning `Task(Str, Int)` - async TLS send.
- `Ashes.Net.Tls.receive(socket)(maxBytes)` returning `Task(Str, Str)` - async TLS receive.
- `Ashes.Net.Tls.close(socket)` returning `Task(Str, Unit)` - explicit async TLS close.
- `Ashes.Net.Http.get(url)` returning `Task(Str, Str)` - async HTTP GET for `http://` and `https://` URLs.
- `Ashes.Net.Http.post(url, body)` returning `Task(Str, Str)` - async HTTP POST for `http://` and `https://` URLs.

### 13.3 Built-in Runtime Types

The compiler also provides built-in runtime ADTs:

```ash
type Unit =
    | Unit
type Maybe(T) =
    | None
    | Some(T)
type Result(E, A) =
    | Ok(A)
    | Error(E)
```

`Maybe` and `Result` behave like any other algebraic data type during type checking
and pattern matching.

Examples:
```ash
let value = Some("hello")
in
  match value with
    | None -> Ashes.IO.print("empty")
    | Some(text) -> Ashes.IO.print(text)
```

Rules:

- `Unit` is always available; no import is required.
- `Maybe` is always available; no import is required.
- `Result` is always available; no import is required.
- `type Unit = ...` is reserved and rejected in user code.
- `type Maybe = ...` is reserved and rejected in user code.
- `type Result = ...` is reserved and rejected in user code.
- `None` and `Some` participate in normal constructor resolution rules.
- `Ok` and `Error` participate in normal constructor resolution rules.

`Ashes.IO.write` and `Ashes.IO.writeLine` return `Unit`.
`Ashes.IO.print` has type `a -> Unit`.
`Ashes.IO.readLine` has type `Unit -> Maybe(Str)` and `Ashes.IO.readLine()` is
equivalent to `Ashes.IO.readLine(Unit)`.

`Ashes.Text.uncons` has type `Str -> Maybe((Str, Str))` and returns `None` for
the empty string. For non-empty strings it returns `Some((head, tail))`, where
`head` is one Unicode scalar value encoded as a `Str` and `tail` is the
remaining suffix.

`Ashes.Text.parseInt` has type `Str -> Result(Str, Int)`. It accepts an
optional leading `-` followed by decimal digits. Malformed input and overflow
return `Error(message)`.

`Ashes.Text.parseFloat` has type `Str -> Result(Str, Float)`. It accepts a
decimal integer part with an optional fractional part and optional exponent
using `e` or `E`. Malformed input and out-of-range values return
`Error(message)`.

`Ashes.Text.fromInt` has type `Int -> Str` and formats decimal integers,
including negative values. `Ashes.Text.fromFloat` has type `Float -> Str` and
formats finite values as decimal text with up to six fractional digits, trimming
trailing zeroes while preserving at least one digit after the decimal point.
`Ashes.Text.toHex` has type `Int -> Str` and formats lowercase hexadecimal with
a `0x` prefix; negative values are formatted with a leading `-`.

`Ashes.IO.readLine` removes a trailing `\n` from the returned line and also
normalizes Windows `\r\n` input so the returned string never includes the trailing
newline bytes.

### 13.4 Error Handling

Ashes uses explicit `Result(E, A)` values for recoverable failures.

Idiomatic Result handling patterns are:

- `match value with | Ok(x) -> ... | Error(e) -> ...` when both branches must be handled explicitly.
- `let? name = value in body` when a sequence of Result-returning operations should short-circuit on `Error`.
- `|?>` when a Result-success pipeline reads more clearly than nested matches.
- `|!>` when only the error payload should be transformed.

Example:

```ash
let describe result =
    match result with
        | Ok(value) -> Ashes.IO.writeLine("ok")
        | Error(message) -> Ashes.IO.writeLine(message)
in describe(Ok(1))
```

`Ashes.IO.panic(message)` is reserved for unrecoverable failures.
It prints the provided message and terminates execution with a non-zero exit code.

### 13.5 Shipped Standard Library Modules

Ashes also ships pure library modules implemented in Ashes source.
Every function in these modules is pure: it returns a new value and
never mutates its arguments.

Current shipped modules include:

- `Ashes.Collection.List` — helper functions for the built-in list type.
- `Ashes.Collection.Map` — persistent immutable map helpers and the shipped `MapTree(K, V)` value type.
- `Ashes.Core.Maybe` — helper functions for the built-in `Maybe(T)` runtime type.
- `Ashes.Core.Result` — helper functions for the built-in `Result(E, A)` runtime type.
- `Ashes.Text` — pure string helpers built on `Ashes.Text`.
- `Ashes.Test` — assertion helpers for tests and small programs.

Stable helper surfaces:

#### `Ashes.Collection.List`

- `Ashes.Collection.List.append : List<a> -> List<a> -> List<a>`
- `Ashes.Collection.List.length : List<a> -> Int`
- `Ashes.Collection.List.head : List<a> -> Maybe(a)`
- `Ashes.Collection.List.tail : List<a> -> Maybe(List<a>)`
- `Ashes.Collection.List.map : (a -> b) -> List<a> -> List<b>`
- `Ashes.Collection.List.filter : (a -> Bool) -> List<a> -> List<a>`
- `Ashes.Collection.List.foldLeft : (b -> a -> b) -> b -> List<a> -> b`
- `Ashes.Collection.List.fold : (a -> b -> b) -> b -> List<a> -> b`
- `Ashes.Collection.List.isEmpty : List<a> -> Bool`
- `Ashes.Collection.List.reverse : List<a> -> List<a>`

#### `Ashes.Collection.Map`

- `Ashes.Collection.Map.empty : MapTree(k, v)`
- `Ashes.Collection.Map.isEmpty : MapTree(k, v) -> Bool`
- `Ashes.Collection.Map.get : k -> MapTree(k, v) -> Maybe(v) requires {Ord(k)}`
- `Ashes.Collection.Map.getWith : (k -> k -> Int) -> k -> MapTree(k, v) -> Maybe(v)`
- `Ashes.Collection.Map.contains : k -> MapTree(k, v) -> Bool requires {Ord(k)}`
- `Ashes.Collection.Map.containsWith : (k -> k -> Int) -> k -> MapTree(k, v) -> Bool`
- `Ashes.Collection.Map.set : k -> v -> MapTree(k, v) -> MapTree(k, v) requires {Ord(k)}`
- `Ashes.Collection.Map.setWith : (k -> k -> Int) -> k -> v -> MapTree(k, v) -> MapTree(k, v)`
- `Ashes.Collection.Map.insert : k -> v -> MapTree(k, v) -> MapTree(k, v) requires {Ord(k)}`
- `Ashes.Collection.Map.insertWith : (k -> k -> Int) -> k -> v -> MapTree(k, v) -> MapTree(k, v)`
- `Ashes.Collection.Map.size : MapTree(k, v) -> Int`
- `Ashes.Collection.Map.foldLeft : (s -> k -> v -> s) -> s -> MapTree(k, v) -> s`
- `Ashes.Collection.Map.toList : MapTree(k, v) -> List<(k, v)>`
- `Ashes.Collection.Map.fromList : List<(k, v)> -> MapTree(k, v) requires {Ord(k)}`
- `Ashes.Collection.Map.fromListWith : (k -> k -> Int) -> List<(k, v)> -> MapTree(k, v)`

`Ashes.Collection.Map` is a persistent AVL tree. Its canonical operations use `Ord(k)`; the `With`
variants preserve explicit custom orderings and take a comparator returning a negative integer,
zero, or a positive integer.

#### `Ashes.Core.Maybe`

- `Ashes.Core.Maybe.default : a -> Maybe(a) -> a`
- `Ashes.Core.Maybe.flatMap : (a -> Maybe(b)) -> Maybe(a) -> Maybe(b)`
- `Ashes.Core.Maybe.getOrElse : a -> Maybe(a) -> a`
- `Ashes.Core.Maybe.isNone : Maybe(a) -> Bool`
- `Ashes.Core.Maybe.isSome : Maybe(a) -> Bool`
- `Ashes.Core.Maybe.map : (a -> b) -> Maybe(a) -> Maybe(b)`
- `Ashes.Core.Maybe.unwrapOr : Maybe(a) -> a -> a`

#### `Ashes.Core.Result`

- `Ashes.Core.Result.default : a -> Result(E, a) -> a`
- `Ashes.Core.Result.bind : (a -> Result(E, b)) -> Result(E, a) -> Result(E, b)`
- `Ashes.Core.Result.map : (a -> b) -> Result(E, a) -> Result(E, b)`
- `Ashes.Core.Result.flatMap : (a -> Result(E, b)) -> Result(E, a) -> Result(E, b)`
- `Ashes.Core.Result.getOrElse : a -> Result(E, a) -> a`
- `Ashes.Core.Result.isOk : Result(E, a) -> Bool`
- `Ashes.Core.Result.isError : Result(E, a) -> Bool`
- `Ashes.Core.Result.mapError : (E -> F) -> Result(E, a) -> Result(F, a)`

#### `Ashes.Text`

- `Ashes.Text.substring : Str -> Int -> Int -> Str`
- `Ashes.Text.length : Str -> Int`
- `Ashes.Text.indexOf : Str -> Str -> Int`
- `Ashes.Text.startsWith : Str -> Str -> Bool`
- `Ashes.Text.contains : Str -> Str -> Bool`
- `Ashes.Text.split : Str -> Str -> List<Str>`
- `Ashes.Text.trim : Str -> Str`
- `Ashes.Text.isLetter : Str -> Bool`
- `Ashes.Text.isDigit : Str -> Bool`
- `Ashes.Text.isWhiteSpace : Str -> Bool`

`Ashes.Test` currently exports:

- `assertEqual : a -> a -> Unit requires {Eq(a)}` — succeeds when the two values are equal and
    aborts via `Ashes.IO.panic` when they are not.
- `fail(message)` — always aborts via `Ashes.IO.panic`.

Example:

```ash
import Ashes.Test
let checked = assertEqual(3, 3)
in Ashes.IO.print("ok")
```

Like other multi-argument calls in Ashes, `assertEqual(expected, actual)` is
surface sugar for curried application.

These helper modules are compiler-shipped and live under the reserved `Ashes.*`
namespace. User projects cannot override them with project-local modules.

### 13.6 Future Standard Library Modules

The module system supports nested module paths. Future modules are tracked in
[future/FUTURE_FEATURES.md](../future/FUTURE_FEATURES.md).

The `Ashes` namespace is reserved and cannot be used for user-defined modules.
This applies to `Ashes` itself and to any `Ashes.*` module path.

### 13.7 Core Language vs Library

Language-level syntax constructs remain built into the compiler:

- `Int`, `Bool`, `String` types
- `Maybe(T)` and `Result(E, A)` runtime ADTs
- list literals `[1, 2, 3]`
- list type semantics (`List<T>` in type displays)
- tuple syntax `(a, b)`
- function types
- ADT declarations (`type`)

Standard library functionality lives under modules.

Example usage:

    import Ashes.IO

    let xs = [1, 2, 3]
    in
    match xs with
        | [] -> Ashes.IO.print("empty")
        | x :: _ -> Ashes.IO.print(x)

---

## 14. Type Inference

Ashes uses static Hindley-Milner style type inference with let-polymorphism.

### 14.1 Let-Polymorphism

For non-recursive `let` bindings, inferred types are generalized into type schemes.
Conceptually, this is `forall` quantification (for example `forall a. a -> a`), even
though users do not write `forall` syntax in source code.

At each use site of a generalized identifier, the compiler instantiates the scheme
with fresh type variables. This allows one binding to be reused at multiple types.

Example:

```ash
let id x = x
let _a = id(1)
let _b = id("x")
```

`id` is inferred once, generalized, then instantiated separately for integer and string uses.

### 14.2 Recursion and Polymorphism

`let recursive` bindings are monomorphic during inference. The recursive name is checked
using a single monotype inside its own definition, so polymorphic recursion is not inferred.

### 14.3 Infinite Types (Occurs Check)

Inference rejects infinite/recursive types via an occurs check. Self-application patterns
such as `x(x)` are invalid, because they would require a type variable to contain itself.

### 14.4 What to Expect

Common patterns that typecheck:

- polymorphic helper functions (for example `id`, `const`, `map`) defined with non-recursive `let`
- the same helper used at different concrete types in different call sites

Common patterns that do not typecheck:

- polymorphic recursion in a `let recursive` definition
- self-application that would create an infinite type
- list elements differ in type
- match branches differ in return type
- cons tail is not a list
- recursive binding lacks `recursive`

---

## 15. Evaluation Strategy

Ashes is:

- strictly evaluated
- immutable
- recursion-based
- pure — all functions return new values; no function mutates its arguments

Iteration is expressed using recursion and pattern matching.

**Purity contract.** Every standard library and user-defined function
is pure in the following sense:

- Calling a function never changes the value of any existing binding.
- Operations that conceptually "add" or "remove" (e.g. `Ashes.Collection.List.append`,
  cons `::`, `Ashes.Collection.List.filter`) always return a **new** value; the
  original is unmodified.
- There are no in-place updates. If a program needs a modified version of
  a value, it builds one via expression — the original remains available
  until it goes out of scope.

The compiler and runtime may optimize representation internally (structure
sharing, in-place reuse when safe), but these optimizations are invisible
to user code. From the programmer's perspective, every value is immutable
once created.

---

## 16. Resource Types and Deterministic Cleanup

Certain types represent external system resources whose release is observable. These are called
**resource types**. They include compiler-provided handles and opaque FFI handles explicitly declared
as resources.

Currently classified resource types:

- `FileHandle` — file handles from `Ashes.IO.File.open`
- `Socket` — TCP socket handles from `Ashes.Net.Tcp.connect`
- `TlsSocket` — TLS session handles from `Ashes.Net.Tls.connect` or a server-side
  `Ashes.Net.Tls.Server.handshake`
- `Process` — child-process handles from `Ashes.Process.spawn`

### 16.1 Declared External Resources

An FFI binding opts an opaque handle into the same affine ownership system with a destructor
declaration:

```ash
external type Database resource destructor databaseClose
external databaseClose(consume Database) -> void = "db_close@libdb"
external databaseQuery(borrow Database, Str) -> Int = "db_query@libdb"
external databaseTransfer(consume Database) -> void = "db_transfer@libdb"
```

The destructor name is an Ashes-visible external function declared in the same file. It must take
exactly one `consume` parameter of the declared resource type and return `void`; missing, duplicate,
or mismatched destructors are rejected with `ASH041`. Every other direct resource parameter must
also say `borrow` or `consume`; missing markers and markers on non-resource types are rejected with
`ASH042`.

Calling the destructor explicitly closes the binding and suppresses its automatic cleanup. Calling
another consuming external transfers ownership and marks the binding moved. A borrow neither closes
nor transfers it. Resource values returned from externals begin owned, and a zero-cost nominal type
over a declared resource retains this classification.

An external function that borrows, consumes, or returns a declared resource is not a first-class
function value and must be called directly. This preserves the explicit native ownership boundary;
ordinary external functions remain first-class.

`resource`, `destructor`, `borrow`, and `consume` are contextual in external declarations and remain
available as identifiers elsewhere. Declared resources do not add finalizers to ordinary values.

### 16.2 Automatic Cleanup

Resource bindings are automatically cleaned up when they go out of scope.
The compiler inserts cleanup calls at the end of every scope that contains
a live resource binding. This includes:

- `let` binding scopes
- `match` case branches
- The program's top-level scope

Users do not write cleanup calls manually unless they want explicit control
over when a resource is released.

### 16.3 Explicit Close

Resources may be closed explicitly using the appropriate API:

- `Ashes.Net.Tcp.close(socket)` — closes a socket
- `Ashes.Net.Tls.close(socket)` — closes a TLS session

When a resource is closed explicitly, the automatic cleanup for that
resource is skipped (no double close).

### 16.3.1 Move on Transfer

Ownership of a resource **moves** out of a scope when the resource is handed off to something that
takes responsibility for it — so the original scope no longer cleans it up. Ownership moves when a
resource binding is:

- stored into an aggregate (constructor field, tuple element, list cell),
- passed to a function or handler that **consumes** it (see borrowing below), or
- passed to `Ashes.Task.spawn`.

Passing a resource to a function that consumes it transfers ownership: the callee now owns the
resource (and is responsible for closing it), and the caller must not use or close it afterward. This
is what lets a combinator hand an accepted socket to an opaque handler that closes it, without the
combinator's own scope closing it a second time (for example `Ashes.Net.Tcp.Server.serve` and
`Ashes.Net.Tls.Server.serveTls`).

**Borrowing.** Passing a resource to a function that only *reads* it — never closing, storing,
returning, or capturing it — is a **borrow**, not a move: the caller keeps ownership and closes it
once, and may keep using the resource afterward. Borrowing for ordinary Ashes functions is inferred
automatically; a parameter is treated as borrowed only when the compiler can prove every use in the
callee is a read. External functions instead use the explicit markers in §16.1 because native code
cannot be inspected. Anything the compiler cannot prove is a pure read is conservatively a move.

Using or closing a resource *after* its ownership has moved is a compile-time error (`ASH008`),
distinct from use-after-close (`ASH006`): the resource was not closed here, its ownership was
transferred. Storing a resource into an aggregate that then escapes and is never referenced again by
the original binding is the intended pattern and stays valid — the error only fires when the moved-out
binding is used again.

### 16.4 Compile-Time Safety

The compiler enforces resource safety with three rules:

1. **No use-after-close.** Using a resource after it has been closed
   (passing it to `send` or `receive`) is a compile-time error
   (diagnostic `ASH006`).

2. **No double-close.** Calling `close` on an already-closed resource
   is a compile-time error (diagnostic `ASH007`).

3. **No use-after-move.** Using or closing a resource after its ownership
   has moved (see §16.3.1) is a compile-time error (diagnostic `ASH008`).

These checks are performed at compile time during semantic analysis.

### 16.5 What Is Not Affected

Resource safety rules (use-after-close, double-close, use-after-move) apply only to resource
types. Ordinary immutable heap values (`Str`, `Bytes`, `List`, tuples, ADTs,
records, `BigInt`, and closures) use compiler-inferred ownership and RC/region
lowering but introduce no use-after-move restriction in source code — see §17.

### 16.6 No Garbage Collection

All resource cleanup is deterministic and compile-time verified. It does not
depend on ordinary reference counts or a garbage collector. The compiler
guarantees exactly-once cleanup for every resource ownership path.

---

## 17. Ownership Model

Ashes uses an **implicit sharing** model for memory management. Ordinary
immutable values follow an internal RC Perceus ownership discipline, while
resource types retain the affine rules in §16. Users never write ordinary
move, borrow, `dup`, `drop`, reference-count, or lifetime annotations.

### 17.1 Copy vs Owned Types

| Category | Types | Behaviour |
|----------|-------|-----------|
| **Inline copy** | `Unit`, `Bool`, `Int`, `Float`, fixed-width unsigned integers | Represented directly and trivially duplicated; no heap ownership. |
| **Ordinary managed** | `Str`, `Bytes`, `BigInt`, `List`, tuples, functions, records, and ADTs | Immutable heap graphs. Ownership, sharing, precise drop, reuse, and scoped allocation are compiler-internal. |
| **Resource** | File, socket, TLS, process, and resource-bearing aggregate values | Affine ownership with observable deterministic cleanup and diagnostics; see §16. |

Copy types may be used any number of times without restriction.

Ordinary managed values may also be used any number of times. The compiler
creates an additional internal ownership reference only when the program
retains more than one reference.

### 17.2 Implicit Sharing

Values in Ashes are **implicitly shared**. When a binding is used —
passed to a function, stored in a data structure, or returned from a
scope — the compiler borrows, moves, or shares it automatically. There is no
explicit borrow syntax (`&x`), no move keyword, and no ordinary-value
use-after-move error.

Within one thread, immutable subgraphs may share RC cells. Across a structured
parallel worker boundary, captures and results are independently copied instead
of sharing a non-atomic reference count. Both representations have identical
source semantics.

### 17.3 Deterministic Drop

For ordinary managed values, the compiler inserts `RcDrop` after the last use
or at the start of a branch where an owner is dead. A shared cell is
decremented; the last reference recursively releases its owned children and
then its allocation. Pattern matching transfers or duplicates live payload
ownership before consuming the matched parent.

This timing is not observable in Ashes source because ordinary values have no
finalizers. Resource cleanup uses the separate rules in §16 and remains
observable and exactly once.

### 17.4 Moves as Optimisation

"Move" in Ashes is a **compiler optimisation**, not a user-visible
operation. When the compiler can prove a value is used for the last
time, it transfers its ownership reference rather than incrementing the count.
When a dead unique constructor is rebuilt with a compatible constructor, the
compiler may also reuse the cell in place. Both are invisible to user code —
the program behaves identically whether a value is moved, shared, freshly
allocated, or reused.

### 17.5 Borrowing Is Inferred

Borrowing is **compiler-inferred**, not user-annotated. There is no
`&x` syntax, no borrow operator, and no lifetime annotations.

Function ownership summaries infer which parameters are read-only borrows and
which consume an ownership reference. A borrowed reference carries no cleanup
responsibility. If a consuming use must coexist with another live use, the
compiler inserts the required ownership duplicate at the latest safe point.

Since Ashes values are immutable, inferred borrowing is always safe:

- Multiple borrows of the same value can coexist without conflict.
- An escaping graph receives independent ownership; a borrowed child is never
  left dangling under an RC parent.
- Non-atomic RC values never cross a worker thread boundary directly.
- There are no value data races because ordinary values are immutable.

Inline copy types are never borrowed — they are
trivially duplicated on the stack.

Internally `Borrow(target, source)` remains an identity-preserving pointer
pass-through. `RcDup` and `RcDrop` carry ownership changes, and
`DropReuse`/`AllocReusing` carry unique-cell reuse.

### 17.6 No Garbage Collection

There is no tracing garbage collector and no ordinary finalizer API. Escaping
ordinary graphs use compiler-inserted runtime reference counting. Values proven
not to escape may use scoped bump regions; task/capability state, mmap-backed
views, and specialized persistent collection storage have explicit region
owners.

Reference counting does not collect cycles. Ashes admits acyclic immutable
ordinary graphs to RC; scheduler linkage remains region-owned. A future mutable
reference or cyclic closure representation must add cycle handling or remain
outside RC admission.

The compiler does not claim that the complete mixed runtime state satisfies the
Perceus garbage-free theorem. It does preserve that ownership invariant for
admitted RC graphs and separately regression-tests every retained region for
bounded memory growth.

---

## 18. Optimization

The Ashes compiler performs multiple levels of optimization, all invisible
to user code. Observable behaviour is always preserved — optimizations
never change what a program prints, returns, or does.

### 18.1 IR-Level Optimizations

After semantic lowering and before backend code generation, the compiler
runs an IR optimization pass pipeline:

- **Constant folding** — Arithmetic on known constant operands is
  evaluated at compile time. `10 + 32` becomes `42` in the IR with no
  runtime addition.
- **Dead code elimination** — Instructions whose results are never used
  (e.g. constants left over after folding) are removed.
- **Drop elision** — Redundant `Drop` instructions for values that were
  never initialized are candidates for removal.
- **Borrow elision** — `Borrow` instructions on copy-type constants are
  candidates for removal since copy types have no ownership semantics.

### 18.2 Backend Optimizations

The LLVM backend applies instruction-level optimizations during code
generation via the target machine's optimization level (O0 through O3).
This includes register allocation, instruction scheduling, and
peephole optimizations performed by LLVM's code generator.

### 18.3 Tail-Call Optimization

Tail-recursive functions are optimized into constant-stack loops by the
compiler. When the compiler detects that a recursive call is in tail
position, it rewrites the call as a jump back to the function entry,
reusing the current stack frame. This means recursive functions like:

    let recursive sum n acc =
        if n == 0 then acc
        else sum(n - 1)(acc + n)
    in sum(1000000)(0)

run in constant stack space, without risk of stack overflow.

#### 18.3.1 Mutual Recursion

Cross-member tail calls in a `let recursive ... and ...` group are also compiled
to constant-stack loops when all of the following hold:

- every member of the group has the same number of parameters (arity >= 1),
- the parameter types are structurally identical position-by-position
  across all members, and
- at least one member makes a genuine cross-member call in tail position.

When these conditions hold, the compiler merges the group into a single
dispatch function whose in-group tail calls become back-edge jumps, so a
mutually tail-recursive pair such as `isEven`/`isOdd` runs in constant
stack space. When they do not hold, cross-member calls are ordinary
closure calls and each one consumes a stack frame. Non-tail in-group
calls always consume stack frames, exactly like non-tail self-calls.

#### 18.3.2 Stack Depth and Non-Tail Recursion

Only tail calls are rewritten into loops. A recursive call whose result
is still consumed by the caller (for example `n * factorial(n - 1)`, or
rebuilding a list around the recursive result) occupies one stack frame
per active call, so its maximum depth is bounded by the thread's stack:

- **Main thread.** The executable does not override the platform stack.
  On Linux targets the main thread gets the operating system's default
  stack limit (`RLIMIT_STACK`, commonly 8 MiB). On `win-x64` the image
  reserves 8 MiB by default.
- **Parallel workers.** `Ashes.Task.Parallel` workers default to a 1 MiB
  stack, configurable with the `--parallel-stack-size` compile flag (see
  the CLI specification).

Exhausting the stack is not a diagnosed error: the process faults
(segmentation fault on Linux, stack-overflow exception on Windows). For
unbounded input sizes, structure the recursion so the recursive call is
in tail position, typically by threading an accumulator as in the `sum`
example above.

### 18.4 Zero-Cost Abstraction Philosophy

Values are immutable and freely shared; the compiler handles ownership
and memory safely behind the scenes. Optimizations are never visible to
user code. The compiler is free to reorder, eliminate, or restructure
internal operations as long as the observable result is identical.

---

## 19. Async/Await

Ashes supports task-based concurrency via `Task` values and `await`.

### 19.1 Task Type

    Task(E, A)

`Task(E, A)` is a built-in parametric type representing an asynchronous
computation that may fail with error type `E` or succeed with value type `A`.

- `Task` is an **owned type** (like `String`, `List`, closures).
- `Task` values are **not resource types** — they do not have use-after-close
  or double-close restrictions.
- The compiler inserts `Drop` for `Task` at scope exit like any other owned type.

### 19.2 Await Expressions

`await <expr>` resolves a `Task(E, A)` to `Result(E, A)`:

    let result = await Ashes.Net.Http.get("http://example.com")
    in result

- `await <expr>` where `<expr> : Task(E, A)` produces `Result(E, A)`.
- `await` runs the task to completion.
- `Ashes.Task.run(task)` has equivalent return shape (`Result(E, A)`), so `await` can be used directly.

### 19.3 Async Let (let!)

`let!` is sugar for `await` in a binding position:

    let! response = Ashes.Net.Http.get("http://example.com")
    in response

This desugars to:

    let response = await Ashes.Net.Http.get("http://example.com")
    in response

`let!` flattens binding chains — no additional nesting per await point.
Multiple `let!` bindings chain sequentially:

    let! a = Ashes.Net.Http.get("http://a.com")
    let! b = Ashes.Net.Http.get("http://b.com")
    in a + b

Desugars to:

    let a = await Ashes.Net.Http.get("http://a.com")
    in
        let b = await Ashes.Net.Http.get("http://b.com")
        in a + b

### 19.4 Type Inference Rules

- `await <expr>` where `<expr> : Task(E, A)` produces `Result(E, A)`.
- Functions returning `Task` are regular functions.

### 19.5 Result Interop

`Task(E, A)` and `Result(E, A)` share the same error-propagation model:

- `Ashes.Task.fromResult(result)` — wraps a `Result(E, A)` into a
  `Task(E, A)` that completes immediately.
- `Ashes.Task.task(value)` — wraps a value into an already successful `Task(Str, A)`.
- `Ashes.Task.run(task)` — runs a task to completion and returns
  `Result(E, A)`.

### 19.6 Ashes.Task Module

| Function | Type |
|----------|------|
| `Ashes.Task.run(task)` | `Task(E, A) -> Result(E, A)` |
| `Ashes.Task.task(value)` | `A -> Task(Str, A)` |
| `Ashes.Task.fromResult(r)` | `Result(E, A) -> Task(E, A)` |
| `Ashes.Task.sleep(ms)` | `Int -> Task(Str, Int)` |
| `Ashes.Task.all(tasks)` | `List(Task(E, A)) -> Task(E, List(A))` |
| `Ashes.Task.race(tasks)` | `List(Task(E, A)) -> Task(E, A)` |
| `Ashes.Task.spawn(task)` | `Task(E, A) -> Unit` |

`Ashes.Task.spawn(task)` detaches a task for fire-and-forget execution: the task advances
cooperatively whenever any `Ashes.Task.run` drive is blocked waiting (on a socket or timer), and
its result is dropped when it completes. Ownership of any resources the task references (for
example an accepted socket) moves into the detached task — the spawning scope no longer closes
them, so the task must release its own resources. Detached tasks still in flight when the driving
`run` completes (and the program exits) are abandoned. This is the concurrency primitive behind
`Ashes.Net.Tcp.Server.serve`'s concurrent connection handling.

#### 19.6.1 Ashes.Task.sleep

`Ashes.Task.sleep(ms)` creates a task that suspends for the given
number of milliseconds, then completes with `0`:

    let _ = await Ashes.Task.sleep(100)
    in 42

- The argument is an `Int` representing milliseconds.
- The returned task has type `Task(Str, Int)`.
- On completion, the result is `0` (unit placeholder).
- `sleep` is consumed via `await`.
- On Linux, `sleep` uses the `nanosleep` syscall.
- On Windows, `sleep` uses the `Sleep` kernel32 function.

#### 19.6.2 Ashes.Task.all

`Ashes.Task.all(tasks)` takes a list of tasks and runs them all,
collecting results into a list in the original order:

    let results = await Ashes.Task.all([
        Ashes.Task.task(1),
        Ashes.Task.task(2),
        Ashes.Task.task(3)
    ])
    in results

- The argument is a `List(Task(E, A))`.
- The returned task has type `Task(E, List(A))`.
- All tasks are run sequentially (left to right).
- Results are collected in the same order as the input list.
- An empty input list produces an empty result list.

#### 19.6.3 Ashes.Task.race

`Ashes.Task.race(tasks)` takes a list of tasks and returns the result
of the first task to complete:

    let result = await Ashes.Task.race([
        Ashes.Task.task(42),
        Ashes.Task.task(99)
    ])
    in result

- The argument is a `List(Task(E, A))`.
- The returned task has type `Task(E, A)`.
- All tasks in the list are started concurrently and run until they
  either complete or park on a wait point (socket I/O, etc.).
- The first task that completes (whether with `Ok` or `Err`) provides
  the result of the race; its value is returned and remaining tasks
  are cancelled.
- Cancellation closes any OS socket a losing leaf task is parked on and
  recursively cancels awaited sub-tasks; cancelled task results are
  discarded. Cancellation is best-effort: TLS userspace session memory
  is released only when the process exits, and tasks that hold a socket
  but have not yet entered a wait are not closed by cancellation (in
  practice unreachable from `race` because the scheduler only surfaces
  tasks at wait points).
- An empty input list produces `0` (unit placeholder).

### 19.8 Diagnostics

Async/await has no dedicated diagnostic codes. `async` is a builtin
(`Ashes.Task.task`), not a block keyword, so there is no "outside `async`"
state to police; misuse (for example combining tasks with mismatched error
types, or consuming a `Task` without `await`/`Ashes.Task.run`) surfaces through
ordinary type-inference diagnostics. See [Diagnostics Reference](diagnostics.md) for the
full code table.

---

## 20. Capabilities and Handlers

A **capability** lets a function *declare* the operations it needs — `now`, `log`, `lookup` —
without deciding what they mean. The caller chooses the meaning by installing a **handler**. The
same code runs against a real handler in production and an injected handler in tests, with no
parameter threading and no mocking framework. Capabilities are not limited to IO: a handler can
interpret an operation as console IO, but equally as a frozen clock, a captured log buffer, a
fixed price table, a deterministic RNG, or a retry policy. The headline use is **deterministic
dependency injection**: capabilities like `Clock`, `Random`, `Env`, or `FileSystem` are real in
production and fixed in tests, with no `Clock`/`Logger` parameter polluting every signature.

A capability requirement is satisfied in one of two ways: by a **handler** (`handle ... with`) — a
scoped, dynamic implementation — or by a static **provider** (`provide Capability(args) = ...`, §20.6)
that supplies a fixed implementation for a concrete instance, resolved at compile time. Providers
resolve concrete instances (`Clock`, `Render(Str)`) directly, and generic requirements
(`needs {Render(a)}`) by monomorphization or dictionary passing (§20.6); providers are program-global
across modules. Traits (§21) use separate `requires` constraints and coherent instances; a capability
provider never satisfies a trait.

Implementation status: the full surface is implemented — capability declarations, `needs` rows,
capability typing, the unsatisfied-capability diagnostic, `handle`/`perform` with
**tail-resumptive and one-shot resumptive** arms, first-class operation values (for operations with
explicit signatures), static `provide` with concrete and generic (monomorphized / dictionary-passed)
resolution, and capabilities and providers declared in imported project modules. Aborting arms
(a path that never resumes) and multi-shot `resume` are rejected with a
clear diagnostic — see section 20.7 for why. Capabilities
interacting with `async`/`await` state machines or `Ashes.Task.Parallel` worker threads is not yet
defined; handler evidence is currently per-process, not per-task or per-thread. How handlers compile
(dynamically-scoped evidence globals, stack-allocated frames, the `resume` rewrites) is
documented in [Architecture](../internals/architecture.md).

### 20.1 Capability Declarations

A capability is a named set of operations, declared at the top level like a `type`:

```ash
capability Clock =
    | now : Unit -> Int          // explicit operation signature

capability Log =
    | log                        // implicit: signature inferred from uses + handler arms

capability State(a) =                // capability type parameter, for polymorphic operations
    | get : Unit -> a
    | set : a -> Unit
```

- `capability` is a keyword and a top-level declaration form; capabilities cannot be declared inside
  expressions.
- Capability names share the qualified-name namespace with modules: operations are always referenced
  qualified as `Capability.op`.
- Operation signatures are optional. A bare `| lookup` is valid within a compilation unit; the
  operation's type is inferred by unifying every perform-site and every handler arm, then
  generalized like a `let`. Explicit signatures are required when the capability is exported from a
  module, or when an operation is intentionally polymorphic (usually via a capability type parameter
  as in `State(a)`).
- Operation signatures are function types; `Unit -> T` declares a Unit-taking operation.

### 20.2 Performing an Operation

```ash
let t = perform Clock.now(Unit)  // explicit form
let t = Clock.now(Unit)          // implicit form — identical program
```

`perform` is an **optional** keyword: `perform Clock.now(x)` and `Clock.now(x)` are the same
program. The keyword is a greppability marker; the capability row in the type is the source of truth.
The formatter preserves whichever form was written. `perform` must be applied to a capability
operation call (`perform 42` is an error), and operations are always qualified by their capability
(`Clock.now`), so no ambiguity arises when two capabilities share an operation name.

### 20.3 Capability Rows (`needs`) in Type Annotations

A function type may carry a `needs` clause listing the capabilities the function performs:

```ash
let taxFor  : Int -> Int                          = ...  // pure: no row
let priceOf : Str -> Int needs {Prices}            = ...  // performs exactly one capability
let run     : Str -> Int needs {Prices, Clock | e} = ...  // open row: passes other capabilities through
let apply   : (Unit -> a needs e) -> a needs e      = ...  // bare row variable
```

- A function type with no `needs` clause is pure.
- A written `needs {A, B}` row is **closed**: the function performs at most `A` and `B`.
- A trailing row variable (`needs {A, B | e}`) makes the row **open**: at least `A` and `B`, plus
  whatever `e` instantiates to. `needs e` is an open row with no required capabilities.
- Type inference always produces the open form; a written closed row is a deliberate restriction.
- A parameterized capability is written applied: `needs {State(Int)}`. A row contains at most one
  instance of a given capability; mentioning the same capability twice unifies their type arguments.
- `needs` attaches to the **innermost** arrow whose result it follows:
  `A -> B -> C needs {E}` reads as `A -> (B -> C needs {E})` — the first application is pure, the
  second performs `E`. Parenthesize to scope it differently:
  `(A -> B needs {E}) -> C` puts the row on the parameter's type.

### 20.4 Capability Typing

Capability rows are part of the Hindley-Milner type system as a second kind of row, with
row-polymorphic unification:

- **Operations** are typed like functions; their type is inferred by unifying all perform-sites
  and handler arms, then generalized with let-polymorphism.
- **A function's row** is the union of the rows of the operations it performs and the rows of the
  functions it calls, minus any capabilities it handles internally. Rows are inferred open and
  generalized at `let`, so a pure function like `given (x) -> x` receives a row-polymorphic type
  usable in any context.
- **Calling** a function whose row includes capability `E` inside a function whose written (closed)
  row does not include `E` is a compile-time error (`ASH018`).
- **Unhandled capabilities:** if the program's residual capability row at the top level is non-empty after
  default built-in handlers are applied, that is a compile-time error (`ASH017`), not a runtime
  failure.
- Annotation boundaries mirror the rest of Ashes: infer locally, annotate at module exports and
  for intentionally-polymorphic operations.

### 20.5 Handlers

```ash
handle work(Unit) with
    | Clock.now(_)  -> resume(realClock(Unit))   // operation arm: args + one-shot resume
    | Log.log(msg)  -> let _ = emit(msg) in resume(Unit)
    | return(r)     -> r                         // runs on the computation's final value
```

A `handle body with | arms` expression installs an interpretation over the dynamic extent of
`body`. Each operation arm receives the operation's arguments and a one-shot continuation
`resume`; calling `resume(v)` returns `v` to the perform-site and continues the computation. The
optional `return` arm transforms the computation's final value; when absent, the final value is
returned unchanged. A handler discharges exactly the operations it lists and is transparent to
any other capabilities, so its inferred type is row-polymorphic. `resume` is an ordinary identifier
bound by each operation arm, not a keyword.

Continuation power is restricted by the memory model (no GC):

- **Tail-resumptive** arms (`resume` in tail position) compile to a direct call with no
  continuation capture.
- **One-shot resumptive** arms do work after `resume` returns; `resume` runs exactly once per
  path, and is supported in tail position, as the value of a `let`
  (`let r = resume(v) in ...`), or as the scrutinee of a `match`
  (`match resume(v) with ...`). Any other position is rejected with a hint to bind the result
  with `let`. The work after `resume` executes after the handled computation (and the `return`
  arm) completes, transforming the handle's result — when several performs are pending, the most
  recent one's continuation applies innermost.
- **Aborting** arms (a path that never calls `resume`) need unwinding and are rejected.
- **Multi-shot** (`resume` called more than once) is out of scope and rejected.

### 20.6 Static Providers (`provide`)

A `handle` satisfies a capability *dynamically* — for the extent of a scope. A **provider**
satisfies it *statically*: `provide` supplies a fixed implementation for a **concrete** capability
instance, resolved at compile time with no handler evidence.

```ash
capability Clock =
    | now : Unit -> Int

provide Clock =
    | now = given (_) -> Ashes.Time.unixSeconds(Unit)

let stamp = given (_) -> Clock.now(Unit)   // resolves to the provider — no handler needed
```

- A provider is a top-level declaration `provide Cap[(TypeArgs)] = | op = impl | op2 = impl`. It
  must supply **every** operation of the capability, exactly once; each `impl` is an ordinary
  expression whose type must match the operation's signature at the provided instance.
- **Resolution.** At a capability operation call the concrete instance is known after inference. If
  a matching provider exists and the capability is *not* handled by an enclosing `handle`, the call
  is a direct call to the provider's implementation. If it *is* handled, the handler wins
  dynamically. If **both** a provider and an enclosing handler could satisfy the same call, that is
  an ambiguity error (`ASH027`) — there is no hidden precedence.
- **Duplicates.** Two providers for the same concrete instance are an error (`ASH026`).
- **Generic resolution (monomorphization).** A provider resolves a call whose instance is concrete
  at the call site — `provide Clock`, or `Render(Str)` on `Str` values. It also resolves a call inside
  a **generic function** that is *specialized* per concrete use: a non-recursive `let`-bound
  function whose body performs `Cap.op` is **inlined at each concrete call site**, so the operation
  resolves against the caller's type. The same generic function used at two types is monomorphized
  to both:

  ```ash
  capability Render(a) =
      | render : a -> Str

  provide Render(Int) =
      | render = Ashes.Text.fromInt

  provide Render(Bool) =
      | render = given (value) -> if value then "true" else "false"

  let display = given (x) -> Render.render(x)
  display(42)      // resolves to provide Render(Int)
  display(true)    // resolves to provide Render(Bool) — same function, two instances
  ```

- **Generic resolution (dictionary passing).** A function that uses a capability operation at a
  generic type and is **annotated** with an explicit `needs {Cap(a)}` row is compiled by dictionary
  passing: each operation of each parameterized needed capability becomes a hidden parameter, the
  operation calls in the body reference it, and every call site supplies the implementation — from a
  provider (concrete instance) or by threading the caller's own hidden parameter (still-abstract
  instance). Because the operation is a runtime value, this covers the shapes inlining cannot:
  **recursive** and **higher-order** generics.

  ```ash
  capability Select(a) =
      | less : a -> a -> Bool

  let min : List(a) -> a needs {Select(a)} =
      given (items) ->
          match items with
              | [] -> io.panic("empty")
              | x :: xs ->
                  list.foldLeft(given (best) -> given (next) ->
                      if Select.less(next)(best) then next else best)(x)(xs)

  min([5, 3, 1])   // Select(Int) provider threaded in — no handler needed
  ```

  A `needs` row may mix dynamic and static capabilities (`needs {Clock, Select(a)}`): the
  unparameterized ones (`Clock`) are still satisfied by a handler or provider dynamically, while the
  parameterized ones (`Select(a)`) are dictionary-passed. A generic use with **no** `needs` annotation
  is not dictionary-passed; the compiler reports a diagnostic suggesting the annotation (or a
  concrete call site / handler).

- **Providers are program-global (coherence).** A `provide` is visible across the whole program, in
  every module, regardless of imports — like an instance in a coherent typeclass system. A capability
  declared in one module and a `provide` for it in another both satisfy a `needs` requirement anywhere,
  and a generic function annotated `needs {Cap(a)}` may be defined in one module and called from
  another; the provider is resolved (or the dictionary threaded) at the call site. Because providers
  are global, a duplicate `provide` for the same concrete instance is a program-wide error (`ASH026`),
  which is what keeps resolution coherent — the same instance always resolves the same way.

  Dictionary evidence is threaded through imported qualified calls as well as unqualified calls, so a
  generic wrapper may call `Other.f` at a still-generic type without an in-module adapter.

### 20.7 Worked Example

The same business code runs under any handler; only the interpretation changes:

```ash
capability Prices =
    | lookup : Str -> Int

capability Clock =
    | now : Unit -> Int

let priceOf : Str -> Int needs {Prices} = given (item) -> perform Prices.lookup(item)

let order = given (item) -> (priceOf(item), Clock.now(Unit))

let runTest = given (work) ->
    handle work(Unit) with
        | Prices.lookup(_) -> resume(200)
        | Clock.now(_) -> resume(1000)
        | return(r) -> r

runTest(given (_) -> order("widget"))
```

The optional-`perform` and optional-annotation decisions are backed by a paired conformance
test — a fully-explicit program (every `perform`, signature, and `needs` row written out) and
its fully-implicit twin must produce the same inferred types and the same output
(`tests/capability_conformance_explicit.ash` / `capability_conformance_implicit.ash`); a complete
production-shaped demo with a logging handler is `examples/capabilities_production.ash`.

### 20.8 Built-in Runtime Capabilities

The following capabilities are built into the compiler and require no declaration:

- **`ConsoleIO`** — reading stdin or a terminal and writing stdout/stderr. Carried by
  `Ashes.IO.print`, `panic`, `write`, `writeBytes`, `writeLine`, `readLine`, and `readExact`, plus
  `Ashes.IO.Console.enableRawInput`, `restoreInput`, and `pollInput`.
- **`FileRead`** — acquiring filesystem read authority. Carried by
  `Ashes.IO.File.readText`, `readAllBytes`, `mmap`, `exists`, and `open`.
- **`FileWrite`** — creating or replacing filesystem data. Carried by
  `Ashes.IO.File.writeText` and `writeBytes`.
- **`ProcessSpawn`** — creating a child process. Carried by `Ashes.IO.Process.spawn`.
- **`TimeRead`** — observing a clock. Carried by `Ashes.IO.Console.monotonicMillis`.
- **`Entropy`** — acquiring nondeterministic seed material. No compiler builtin currently produces
  entropy; this reserved marker is available to an explicitly classified external declaration.
- **`UnsafeFfi`** — an arbitrary user external call whose declaration has no explicit `needs` row.

- **`NetListen`** — creating a listening endpoint. Carried by `Ashes.Net.Tcp.Server.listen` and
  `forkWorkers`, and therefore (by row inference) by every `serve` combinator
  (`Ashes.Net.Tcp.Server.serve` / `serveParallel` / `serveWithDrainTimeout`,
  `Ashes.Net.Http.Server.serve` / `serveParallel`, `Ashes.Net.Tls.Server.serveTls`).
- **`NetConnect`** — dialing out. Carried by `Ashes.Net.Tcp.connect`, `Ashes.Net.Http.get` / `post`,
  and `Ashes.Net.Tls.connect`.
- **`Stop`** — requesting graceful shutdown of the running server. Unlike the two network
  capabilities it is **performable**: it has one operation, `Stop.stop : Unit -> Unit`.

All built-in runtime capabilities except `Stop` are **marker capabilities**: they declare no
operations, so there is nothing to `perform` and nothing a `handle` arm can intercept. The runtime
itself is their implicit provider, and they are excluded from the top-level unsatisfied-capability
check (`ASH017`). Their value is purely in typing:

- Every function that (transitively) creates a network endpoint carries the capability in its
  inferred row, so "this program is a server" (or "dials out") is visible in its type.
- A written **closed** row that omits them rejects such calls (`ASH018`), exactly like any other
  capability: `let f : Str -> Int needs {} = ...` cannot read a file or call
  `Ashes.Net.Http.get`.
- They can be named in `needs` rows like declared capabilities:
  `let opener : Int -> Task(Str, Socket) needs {NetListen} = given (p) -> tcp.listen(p)`.

`Stop.stop(Unit)` requests graceful shutdown from inside the program (an admin route, a health
check, a test): the server stops accepting, drains in-flight handlers, and completes its lifecycle
result with `Ok(())` — the same path as the first `SIGINT`/`SIGTERM`. On a multi-reactor server a
worker's `Stop.stop` shuts down the whole server (it signals the parent, which forwards to every
worker). It is idempotent (further calls are no-ops). Like the markers it is discharged by the
runtime (excluded from `ASH017`) and cannot be user-handled, but because it is performed inside
handler code its `needs {Stop}` row propagates through `serve` and is visible in the handler's
type, so stop authority is trackable:
`let handle : Request -> Task(E, Response) needs {Stop} = ...`.

Possession-only operations carry no ambient capability. This includes reading or closing an already
open `FileHandle`, operating on an existing `Process`, and `send`, `receive`, `close`, or `accept` on
an accepted or connected socket. Possession of the resource is the authority; the markers govern
acquiring ambient access, not using an acquired value.

External declarations use the same closed-row spelling:

```ash
external loadSecret(Str) -> Str needs {FileRead} = "load_secret@libsecret"
external secureSeed() -> Int needs {Entropy} = "secure_seed@libsecret"
external pureMath(Int) -> Int needs {} = "pure_math@libmath"
external unclassified(Int) -> Int = "unknown_native_call@libnative"
```

The first three calls require exactly the written runtime markers; `unclassified` requires
`UnsafeFfi`. An explicit empty row is a trusted FFI assertion that the native call acquires no
ambient authority. Only built-in runtime capability names are allowed in an external row, and its
row is closed: user capability variables and open tails are rejected. A declared resource
destructor defaults to the empty row because destruction uses authority already conveyed by
possession. Other borrow/consume externals still default to `UnsafeFfi` unless explicitly
classified.

The names `ConsoleIO`, `FileRead`, `FileWrite`, `ProcessSpawn`, `TimeRead`, `Entropy`, `UnsafeFfi`,
`NetListen`, `NetConnect`, and `Stop` are reserved. A user capability declaration with any of these
names is a compile-time error.

Because a handler's `needs {Stop}` (or `{NetConnect}`, for a handler that dials out) must thread
through `serve`, capability rows propagate correctly through higher-order library combinators and
recursive helpers: a recursive function that performs a capability — or that applies a
capability-performing parameter, as `Ashes.Collection.List.map` applies its mapping function — carries an open latent row, so
passing a capability-performing function to it is accepted.

### 20.9 Design Notes

Ashes uses **lexical handler injection** (the OCaml 5 / Koka / Eff / Frank / Unison family):
the nearest enclosing handler interprets an operation. Traits (§21) deliberately do not replace this
mechanism: trait evidence is selected statically and coherently by type, so it cannot provide a
different clock, logger, or environment for one lexical scope. Module functors are likewise a
different abstraction and are not part of Ashes. Relative to OCaml 5, Ashes adds what OCaml
deliberately omitted: capabilities are tracked in the type system, so an
unhandled capability is a *compile-time* error, not a runtime crash. Relative to Koka, Ashes
restricts continuations to one-shot/tail-resumptive: multi-shot `resume` would require copying a
captured slice of stack and heap — GC-style reachability — which collides with the no-GC memory
model and affine ownership (double-resume is double-use/double-drop of owned values).
Consequently capability-based generators, backtracking, and nondeterminism are out of scope —
documented limitation, not a TODO.

### 20.10 Diagnostics

`ASH017` (unsatisfied capability), `ASH018` (capability not permitted by a closed row, and the
generic-provider limitation), `ASH019` (unknown capability or operation), `ASH020` (invalid
handler), `ASH026` (duplicate/incomplete provider),
and `ASH027` (a capability satisfied by both a provider and a handler) cover this surface; see
[Diagnostics Reference](diagnostics.md).

---

## 21. Traits and Implementations

A **trait** is a named set of operations selected statically from types. An **implementation** supplies
those operations for one type shape. Trait requirements are compile-time evidence and are distinct
from capabilities (§20): traits use `requires`, capabilities use `needs`; implementations are selected
program-wide, while handlers interpret capabilities over a dynamic scope.

Traits support ordinary generic APIs as well as operator dispatch. They are not restricted to
operators and they are not runtime objects in the initial language.

### 21.1 Grammar

The grammar below extends the top-level declaration and annotated-type grammar. `UPPER_IDENT` and
`LOWER_IDENT` follow the existing type/value naming conventions.

```text
declaration       ::= trait-declaration
                    | implementation-declaration
                    | existing-declaration

type-declaration  ::= "type" UPPER_IDENT type-parameters? "=" type-branches
                      deriving-clause?
deriving-clause   ::= "deriving" "{" derivable-trait
                      ("," derivable-trait)* "}"
derivable-trait   ::= "Eq" | "Ord" | "Show" | "Hash"

trait-declaration ::= "trait" UPPER_IDENT type-parameters supertraits? "="
                      trait-method+
type-parameters   ::= "(" type-variable ("," type-variable)* ")"
supertraits       ::= "requires" constraint-set
trait-method      ::= "|" LOWER_IDENT ":" type ("=" expr)?

implementation-declaration
                  ::= "implement" trait-application implementation-requirements? "="
                      implementation-method+
implementation-requirements
                  ::= "requires" constraint-set
implementation-method   ::= "|" LOWER_IDENT "=" expr

type-scheme       ::= type ("requires" constraint-set)?
constraint-set    ::= "{" constraint ("," constraint)* "}"
constraint        ::= trait-application
trait-application ::= qualified-trait-name "(" type ("," type)* ")"
```

A trait or implementation is a top-level declaration. Neither may appear inside an expression. A trait
body contains at least one method, and an implementation body contains at least one method implementation or
override. Method signatures are mandatory. The formatter preserves declaration and method order;
semantic dictionary order is canonical and does not depend on source order.

`requires` after a binding annotation belongs to the complete rank-1 type scheme and has lower
precedence than every type constructor, arrow, and `needs` row. For example:

```ash
let render : a -> Str needs {Log} requires {Show(a)} = ...
```

means that the complete type `a -> Str needs {Log}` requires static `Show(a)` evidence. The
`needs {Log}` row remains attached to the innermost arrow as specified in §20.3. Parentheses scope
function types but do not move a trailing `requires` clause into a nested parameter type:

```ash
let apply : (a -> Str needs {Log}) -> Str requires {Show(a)} = ...
```

Trait constraints are rank-1. A nested function parameter cannot introduce its own independently
quantified `requires` clause. Higher-rank constrained values are deferred.

Empty constraint sets, open trait rows, bare trait-constraint variables, and a `| e` tail inside
`requires` are invalid. Unlike capability effects, a trait requirement set is closed and unordered.

### 21.2 Trait declarations

A trait declares one or more type parameters and one or more methods:

```ash
trait Eq(a) =
    | equal : a -> a -> Bool
    | notEqual : a -> a -> Bool =
        given (left) ->
            given (right) ->
                !Eq.equal(left)(right)
```

The type parameters after the trait name are in scope throughout its supertrait list, method
signatures, and default bodies. Every method signature must mention at least one trait parameter,
directly or through another type. This prevents a method whose implementation cannot be selected
from its ordinary argument/result types.

A method without `=` is required. A method with `= expr` has a default implementation. Inside a
default body:

- all methods of the same trait are available through the trait-qualified name;
- inherited supertrait methods are available through their own qualified names;
- the declared trait parameters and method signature determine the body's types;
- calls dispatch through the dictionary currently being constructed, not through a second implementation
  search;
- a default may perform only capabilities present in its declared method type.

Defaults are ordinary strictly evaluated Ashes expressions. A dependency cycle made only of defaults,
with no supplied implementation method breaking the cycle, is rejected.

Traits may have multiple parameters:

```ash
trait Convert(source, destination) =
    | convert : source -> destination
```

Associated types, higher-kinded parameters, existential dictionaries, and heterogeneous operator
outputs are not part of this version.

### 21.3 Supertraits

`requires` on a trait declaration lists its supertraits:

```ash
trait Ord(a) requires {Eq(a)} =
    | compare : a -> a -> Ordering
```

Evidence for `Ord(T)` always contains evidence for `Eq(T)`. A constrained function that requires
`Ord(a)` may call `Eq.equal` without separately writing `Eq(a)`. Canonicalization removes a written
`Eq(a)` when the same scheme already contains `Ord(a)`.

The supertrait graph must be acyclic. A direct cycle (`A` requires `A`) and an indirect cycle
(`A` requires `B`, `B` requires `A`) are declaration errors. Diamond inheritance is legal; the
shared ancestor is represented once in canonical evidence.

### 21.4 Implementation declarations

An implementation supplies a trait for a concrete or generic type shape:

```ash
type Point =
    | Point(Int, Int)

implement Eq(Point) =
    | equal =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Point(lx, ly), Point(rx, ry)) ->
                        if lx == rx then ly == ry else false

implement Eq(List(a)) requires {Eq(a)} =
    | equal =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | ([], []) -> true
                    | (x :: xs, y :: ys) ->
                        if Eq.equal(x)(y) then Eq.equal(xs)(ys) else false
                    | _ -> false
```

Every implementation method is written `| method = expr`; its signature comes from the trait after
substituting the implementation head. An implementation:

- supplies every method without a default exactly once;
- may override a default method exactly once;
- may not supply unknown methods;
- may not repeat a method;
- must match each substituted method type and capability row;
- may perform fewer capabilities than the declared method, but never additional ones;
- automatically contains evidence for every supertrait, resolved from the same program-global
  registry.

An implementation requirement introduces the evidence needed to construct a generic implementation. Every type
variable in an implementation requirement must occur in the implementation head. Implementation declarations do not
bind values and are not callable or first-class.

### 21.5 Namespaces, qualification, and imports

Trait names occupy the type-level declaration namespace together with nominal types and capability
names. A module cannot declare a type, capability, or trait with the same name. This namespace is
separate from ordinary values, so a value may have the same final segment as a trait.

Method names are local to their declaring trait and do not enter the unqualified value namespace.
Trait methods are always referenced as `Trait.method` or `Module.Trait.method`. Two traits may both
declare `show` without conflict. Constructors remain in the existing constructor/value namespace.

Traits are exported declarations and follow the same sequential module visibility and selector-import
rules as types. Importing a trait makes the trait name available; it does not import its methods as
unqualified values. Implementations are different: every legal implementation in the resolved package graph is
visible to resolution whether or not its defining module was imported explicitly. This global rule is
what makes implementation selection coherent.

Trait names and module names may share a segment only when the complete qualified reference remains
unambiguous. A qualified expression ending in a method is resolved as a trait method only when the
prefix names a trait; otherwise existing module/value resolution applies.

Capabilities and traits may use the same method/operation spelling because both remain qualified.
`Clock.now` is a capability operation and `Show.show` is a trait method. A trait implementation cannot be
installed with `handle`, and a capability provider cannot satisfy a trait constraint.

### 21.6 Constrained type schemes and inference

A trait-using expression produces ordinary type equations plus trait constraints. Constraints are
part of a generalized type scheme but remain separate from capability rows:

```ash
let equal = given (left) -> given (right) -> left == right
```

has the conceptual principal scheme:

```text
forall a. a -> a -> Bool requires {Eq(a)}
```

At a non-recursive `let`, Ashes generalizes type variables, capability-row variables, and constraints
together. Instantiating the binding freshens every quantified variable in both its ordinary type and
its constraints. Constraints propagate through calls, closures, partial application, higher-order
arguments, matches, async bodies, and capability-performing functions.

A written `requires` clause states the complete external trait requirement of that scheme after
supertrait simplification. Missing inferred constraints and written constraints that the body does not
justify are errors; annotations do not silently add or discard evidence. Non-recursive bindings,
including exported bindings, recursive bindings, and mutually recursive groups, may infer constraints.
Recursive members are inferred against one shared monomorphic boundary and generalized together after
every body has been checked. Their canonical constraint order defines stable module metadata and a
deterministic hidden evidence ABI. Written annotations remain optional documentation and an explicit
contract checked against the inferred requirements.

Constraints use a deterministic canonical order: fully qualified trait name first, then the canonical
printed form of each argument from left to right. Exact duplicates are removed and supertraits implied
by a stronger constraint are omitted. This order is used by diagnostics, formatter output, hover text,
module metadata, and dictionary parameters.

Multiple requirements for the same trait remain distinct when their complete type arguments differ.
For example, `requires {Eq(a), Eq(b)}` threads two independently selected dictionaries; calls and
method references match evidence by the complete instantiated constraint rather than by the trait name
alone.

A constraint is **ambiguous** when one of its variables cannot be determined from the scheme's
ordinary argument/result type, its expected type, or another resolved constraint. Ashes does not use
numeric defaulting to hide ambiguity. Numeric literals retain their existing concrete types and suffix
rules.

Rejected ambiguous declaration:

```ash
// `a` occurs only in the constraint, so callers cannot choose it.
let invalid : Bool requires {Default(a)} = true
```

### 21.7 Coherence and package ownership

For a complete resolved program, every trait goal has at most one applicable implementation. Implementation
selection never depends on declaration order, import order, dependency traversal order, or a
"most specific" rule.

Two implementation heads overlap when ordinary type unification can make their trait names and all arguments
equal after freshening their variables. Overlap is rejected even if no current call uses the overlap:

```ash
implement Eq(List(a)) requires {Eq(a)} = ...
implement Eq(List(Int)) = ... // rejected: overlaps the generic head
```

Exact duplicate heads are the same coherence error with a more specific diagnostic. The diagnostic
names both package identities, modules, source files, and declaration spans.

An implementation is legal only when its defining package owns the trait or owns at least one outer nominal
type constructor in the implementation head. Ownership is based on the resolved package identity, never on
directory layout:

- a registry dependency is identified by its locked package namespace and version;
- a path dependency is identified by its resolved manifest and package namespace;
- the root project is identified by its selected `ashes.json` manifest and declared/default package
  namespace;
- single-file compilation is one anonymous root package;
- primitive types and compiler-shipped traits are owned by the core Ashes package.

Modules in one package may cooperate on implementations. A package may define `Eq(LocalType)` or
`LocalTrait(Str)`, but not `Eq(ForeignType)` when both names belong to dependencies. A nominal wrapper
is the supported way to choose different behavior for a foreign type.

Implementations are program-global across the complete resolved dependency graph, including dependencies not
directly imported by the entry module. Adding a dependency can therefore reveal a coherence error, but
cannot silently change which of two implementations wins because overlapping programs are rejected.

### 21.8 Resolution and termination

To resolve a required constraint, the compiler:

1. freshens and unifies the goal with every registered implementation head without mutating caller inference
   state speculatively;
2. reports a missing-implementation error when no head matches;
3. reports a coherence error when more than one head matches;
4. recursively resolves the selected implementation requirements and its supertraits;
5. threads an existing dictionary parameter when the goal remains abstract inside a constrained
   function;
6. otherwise constructs or specializes the unique concrete implementation.

Resolution of conditional implementations must terminate. Define the structural size of a type as the count
of its concrete type-constructor nodes; type variables contribute zero. The size of a constraint is
one plus the sum of its argument sizes. Every requirement on a generic implementation must be strictly
smaller than the implementation head after the head variables are treated consistently. For example,
`Eq(a)` is smaller than `Eq(List(a))`, but `Eq(List(List(a)))` is not.

Supertraits use their separately checked acyclic graph and are not implementation requirements for this
size test. During resolution, repeating a goal or encountering a non-decreasing requirement is an
error with the complete requirement trace. A finite compiler depth limit is a final safety bound and
produces a diagnostic rather than an exception or hang.

Resolved concrete goals may be cached, but cache keys and traversal are canonical and independent of
process hash ordering.

### 21.9 Evidence and evaluation

An abstract constrained function receives hidden immutable dictionary parameters in canonical
constraint order. A dictionary contains method values and inherited evidence in canonical declaration
order. Dictionaries are compiler-generated implementation values: source code cannot construct,
inspect, compare, store as an existential, or test their identity.

Nested and escaping closures capture required dictionaries through the ordinary closure environment.
Recursive and mutually recursive functions pass them on every recursive edge. Partial applications and
constrained functions stored in ordinary aggregates retain their evidence. Dictionary values
participate in ordinary ownership analysis, Perceus duplication/drop insertion, async frame capture,
and tail-call lowering.

When the unique implementation is concrete, the compiler may specialize a method call to a direct function
call. This is an optimization only: disabling inlining, specialization, reuse, or other optimizations
must not change whether a trait program compiles or what it observes.

Trait method arguments and bodies retain Ashes's strict left-to-right evaluation behavior. Operator
desugaring evaluates each operand once in source order before invoking the selected method.

### 21.10 Capabilities in trait methods

A trait method may declare a capability row in its function type:

```ash
trait Audit(a) =
    | audit : a -> Str needs {Log}
```

An implementation may be pure or may use a subset of `Log`, but may not introduce an undeclared
capability. Calling `Audit.audit` propagates its declared `needs` row independently of the static
`Audit(a)` constraint. Handlers and providers satisfy the capability exactly as in §20; implementation
selection satisfies the trait. Trait dictionaries never use dynamic capability-handler globals for
method selection.

### 21.11 Standard traits

The core Ashes package exports the initial standard traits and `Ordering` from `Ashes.Trait`.
Operators can resolve these traits without an import. Direct method use must either qualify the full
name (`Ashes.Trait.Eq.equal`) or import the trait selector (`import Ashes.Trait.Eq`) and use
`Eq.equal`. Importing the whole module permits `Trait.Eq.equal` through the chosen module alias in
the usual way.

The required primitive methods are:

```text
Eq(a)          equal      : a -> a -> Bool
Ord(a)         compare    : a -> a -> Ordering       requires Eq(a)
Show(a)        show       : a -> Str
Hash(a)        hash       : a -> Int
Default(a)     default    : Unit -> a
Add(a)         add        : a -> a -> a
Subtract(a)    subtract   : a -> a -> a
Multiply(a)    multiply   : a -> a -> a
Divide(a)      divide     : a -> a -> a
Remainder(a)   remainder  : a -> a -> a
Negate(a)      negate     : a -> a
Not(a)         not        : a -> a
BitAnd(a)      bitAnd     : a -> a -> a
BitOr(a)       bitOr      : a -> a -> a
BitXor(a)      bitXor     : a -> a -> a
ShiftLeft(a)   shiftLeft  : a -> a -> a
ShiftRight(a)  shiftRight : a -> a -> a
BitwiseNot(a)  bitwiseNot : a -> a
```

`Eq` additionally declares `notEqual : a -> a -> Bool`, defaulted to logical negation of `equal`.
`Ord` additionally declares `less`, `lessOrEqual`, `greater`, and `greaterOrEqual`, each with type
`a -> a -> Bool` and a default derived from `compare`. These defaults produce false for
`Ordering.Unordered`.

`Ordering` is the compiler-shipped ADT:

```ash
type Ordering =
    | Less
    | Equal
    | Greater
    | Unordered
```

`Unordered` is required to preserve IEEE Float behavior. `compare(left)(right)` returns `Unordered`
when either Float operand is NaN. For that result, `<`, `<=`, `>`, and `>=` are all false. Float
`Eq.equal` remains IEEE equality: NaN is unequal to every value including itself, while `0.0` and
`-0.0` are equal.

The source operators map as follows:

| Operators | Trait method |
|---|---|
| `==`, `!=` | `Eq.equal`, `Eq.notEqual` |
| `<`, `<=`, `>`, `>=` | `Ord.less`, `Ord.lessOrEqual`, `Ord.greater`, `Ord.greaterOrEqual` |
| `+` | `Add.add` |
| binary `-` | `Subtract.subtract` |
| `*` | `Multiply.multiply` |
| `/` | `Divide.divide` |
| `%` | `Remainder.remainder` |
| unary `-` | `Negate.negate` |
| `!` | `Not.not` |
| `&` | `BitAnd.bitAnd` |
| `\|` | `BitOr.bitOr` |
| `^` | `BitXor.bitXor` |
| `<<` | `ShiftLeft.shiftLeft` |
| `>>` | `ShiftRight.shiftRight` |
| `~` | `BitwiseNot.bitwiseNot` |

Function application, pipelines, Result pipelines, list construction, patterns, record access, and
record updates remain dedicated language operations.

### 21.12 Primitive and structural implementations

Core implementations preserve the existing primitive behavior exactly:

- `Eq`: `Int`, `Float`, `BigInt`, every `uN`, `Bool`, and `Str`;
- `Ord`: `Int`, `Float`, `BigInt`, every `uN`, and `Str` using existing byte ordering;
- `Add`: `Int`, `Float`, `BigInt`, every `uN`, and `Str`;
- `Subtract`, `Multiply`, and `Divide`: `Int`, `Float`, `BigInt`, and every `uN`;
- `Remainder`: `Int`, `BigInt`, and every `uN`;
- `Negate`: `Int`, `Float`, `BigInt`, and every `uN`, preserving unsigned wrapping;
- `Not`: `Bool` only;
- bitwise operations and shifts: `Int` and every supported unsigned width where the current operator
  is defined;
- `Show`: primitive textual forms use invariant culture; `Str` is escaped and quoted;
- `Hash`: equal primitive values have equal stable hashes; Float normalizes both signed zero encodings
  before hashing and does not rely on process-randomized hashing;
- `Default`: `0`, `0.0`, `0N`, width-correct unsigned zero, `false`, and `""` respectively where
  defined.

Division by zero, remainder by zero, integer overflow/wrapping, unsigned masking, shift validation,
BigInt behavior, Float arithmetic, string concatenation, and byte-for-byte string equality retain the
semantics documented for the existing operators. Traits do not introduce numeric coercions.

Conditional structural implementations are supplied for:

- `Eq(List(a))`, `Ord(List(a))`, `Show(List(a))`, and `Hash(List(a))` when the element evidence exists;
- `Eq`, `Ord`, `Show`, `Hash`, and `Default` for every supported tuple arity when each component has
  the corresponding evidence;
- `Eq`, `Ord`, `Show`, and `Hash` for `Maybe(a)` when the payload evidence exists;
- `Eq`, `Ord`, `Show`, and `Hash` for `Result(e, a)` when evidence exists for both parameters;
- `Default(List(a))` as `[]` without requiring element evidence;
- `Default(Maybe(a))` as `None` without requiring payload evidence.

Structural ordering is lexicographic. Lists compare element-by-element, then the shorter equal-prefix
list is less. Tuples compare fields from left to right. `None < Some(_)`; `Error(_) < Ok(_)`.
`Unordered` propagates immediately from any nested comparison.

Structural display is canonical and source-shaped: lists use `[a, b]`, tuples use `(a, b)`, `Maybe`
uses `None`/`Some(value)`, and `Result` uses `Error(value)`/`Ok(value)`. Structural hashing includes
stable constructor/position tags so distinct shapes do not collapse merely because payload hashes
match.

No core `Eq`, `Ord`, `Show`, `Hash`, or `Default` implementation exists for functions, tasks, capabilities,
handlers, resources, or opaque external values. Nominal user ADTs and records require a manual implementation
or an explicit `deriving` clause; there is no implicit structural implementation.

### 21.13 Derived implementations

A nominal ADT or record may request ordinary coherent implementations directly after its constructor
or field branches:

```ash
type Color =
    | Red
    | Green
    | Blue
    deriving {Eq, Ord, Show, Hash}

type Box(a) =
    | value: a
    deriving {Eq, Show}
```

The `deriving` clause is part of the type declaration. It is written once, after every `|` branch,
and contains a non-empty comma-separated set drawn from `Eq`, `Ord`, `Show`, and `Hash`. Duplicate or
unknown entries are errors. The formatter preserves the written order and emits the clause indented
at the same level as the branches. `deriving` is a reserved keyword.

Deriving generates ordinary `implement` declarations before coherence checking. Generated
implementations have the type declaration's package, module, source path, and source span, obey the
same orphan and overlap rules as handwritten implementations, use the same dictionary ABI, and may
conflict with a handwritten implementation. There is no implicit fallback or second dispatch path.

For a parameterized declaration, the generated implementation requires the corresponding trait for
each type parameter whose occurrences contribute to a constructor payload or record field. Nested
supported containers resolve through their ordinary conditional implementations. A direct or nested
regular recursive occurrence of the type being derived reuses the implementation currently being
constructed and does not add a cyclic external requirement. Deriving is rejected when a compared,
displayed, or hashed field contains a function, task, capability, handler, resource, opaque external
type, an unbound type variable, or unsupported non-regular recursion.

The generated methods are deterministic:

- `Eq` first compares constructors and then compares payloads or fields from left to right;
- `Ord` orders constructors by declaration order, compares equal-constructor payloads or fields
  lexicographically from left to right, and propagates `Unordered`;
- `Show` renders `Constructor`, `Constructor(value1, value2)`, or
  `Record(field1 = value1, field2 = value2)` using declaration order;
- `Hash` starts with the zero-based constructor ordinal plus one and folds each payload or field in
  declaration order as `state * 16777619 + Hash.hash(value)`.

For records, field declaration order is the ordering, display, and hash order even though record
construction accepts named arguments in any order. Empty-payload constructors compare equal to the
same constructor, display as their constructor name, and hash from their constructor tag alone.

### 21.14 Accepted and rejected examples

Constraint inference and explicit constraints:

```ash
let inferred = given (x) -> given (y) -> Eq.equal(x)(y)

let recursive contains : a -> List(a) -> Bool requires {Eq(a)} =
    given (needle) ->
        given (items) ->
            match items with
                | [] -> false
                | item :: rest ->
                    if Eq.equal(item)(needle) then true else contains(needle)(rest)
```

Generic implementation and supertrait use:

```ash
implement Eq(List(a)) requires {Eq(a)} = ...

let orderedEqual : a -> a -> Bool requires {Ord(a)} =
    given (left) -> given (right) -> Eq.equal(left)(right)
```

Default method:

```ash
trait Greet(a) =
    | name : a -> Str
    | greet : a -> Str =
        given (value) -> "Hello, " + Greet.name(value)

implement Greet(Point) =
    | name = given (point) -> "Point"
    // greet is not implemented, so Greet.greet uses the trait's default body above.
```

`Greet.greet(point)` evaluates to `"Hello, Point"`: an implementation that supplies every method with
no default (here, `name`) inherits every method that has one (here, `greet`) without repeating it.

Effectful method:

```ash
capability Log =
    | write : Str -> Unit

trait Report(a) =
    | report : a -> Unit needs {Log}

implement Report(Point) =
    | report = given (point) -> Log.write(Show.show(point))
```

The implementation above is accepted only if its body requires no capabilities beyond `Log` and any static
trait requirements are declared or resolvable.

Rejected forms:

```ash
implement Eq(List(a)) requires {Eq(List(a))} = ... // non-decreasing requirement
implement Eq(List(a)) requires {Eq(b)} = ...       // b absent from the head
implement Eq(Foreign.Point) = ...                  // orphan if Eq and Point are foreign

trait First(a) requires {Second(a)} = ...
trait Second(a) requires {First(a)} = ...          // supertrait cycle

trait Choice(a) =
    | first : a -> Bool = given (value) -> Choice.second(value)
    | second : a -> Bool = given (value) -> Choice.first(value)
    | base : a -> Bool
implement Choice(Int) =
    | base = given (value) -> true             // first/second both left as defaults: cycle

let ambiguous : Bool requires {Default(a)} = true // a cannot be selected by a caller

type Callback =
    | Callback(Int -> Int)
    deriving {Eq} // rejected: functions have no Eq implementation
```

Declaring both `Eq(List(a))` and `Eq(List(Int))` is rejected as overlap. Calling a constrained
function at a concrete type with no matching implementation is a missing-implementation error. Calling it while
the type remains abstract is accepted only when the enclosing scheme carries and threads the same
constraint.

### 21.15 Source compatibility and deferred extensions

`trait`, `implement`, `requires`, and `deriving` are reserved keywords. Programs that previously used
them as identifiers must rename those bindings. This is an intentional source-compatibility change;
the formatter never escapes keywords as identifiers.

The initial trait system does not include associated types, higher-kinded parameters, local implementations,
overlapping/incoherent selection, runtime trait objects, user-defined operator tokens, overloaded
numeric literals, numeric defaulting, or heterogeneous operator outputs.

---

## 22. Unsupported (Future)

See [future/FUTURE_FEATURES.md](../future/FUTURE_FEATURES.md) for the list of planned but not yet supported features.

> **Note**
>
> project-mode `import Foo` / `import Foo.Bar` lines are supported by the project system
> (`ashes.json`) and are resolved before expression parsing. Built-in
> `import Ashes.IO` is handled by the compiler directly.
>
