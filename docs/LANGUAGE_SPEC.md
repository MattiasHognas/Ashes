# Ashes Language Specification

Ashes is a pure, statically typed, expression-based functional programming language
compiled directly to native code.

Values are immutable and freely shared; the compiler handles ownership
and memory safely behind the scenes.

This document describes the **surface syntax** and **semantic behavior** of the
currently supported language features.

---

# 1. Program Structure

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
`effect`, `uses`, `perform`, `handle`

Two principles govern the keyword set:

1. **Words for meaning, symbols for plumbing.** Keywords carry semantics and are full English
   words. Structural tokens — `->`, `=`, `|`, `(...)`, `{...}`, `::` — are plumbing and stay
   symbolic; no arrow is ever replaced with a word.
2. **No abbreviations.** A keyword is written out in full: `recursive`, not `rec`; `external`,
   not `extern`; `given`, not `fun`.

The former spellings `fun`, `rec`, and `extern` were renamed to `given`, `recursive`, and
`external` as a breaking change; the old spellings are no longer keywords and are ordinary
identifiers today. Pre-rename sources are migrated by rewriting the three keywords (the
canonical formatter of a pre-rename compiler emits exactly the forms the rewrite expects).

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
- `Ashes.File.readText(path)` returning `Result(Str, Str)`
- `Ashes.File.writeText(path, text)` returning `Result(Str, Unit)`
- `Ashes.File.exists(path)` returning `Result(Str, Bool)`
- `Ashes.Text.uncons(text)` returning `Maybe((Str, Str))`
- `Ashes.Text.parseInt(text)` returning `Result(Str, Int)`
- `Ashes.Text.parseFloat(text)` returning `Result(Str, Float)`
- `Ashes.Text.fromInt(value)` returning `Str`
- `Ashes.Text.fromFloat(value)` returning `Str`
- `Ashes.Text.toHex(value)` returning `Str`
- `Ashes.Http.get(url)` returning `Task(Str, Str)`
- `Ashes.Http.post(url, body)` returning `Task(Str, Str)`
- `Ashes.Net.Tcp.connect(host)(port)` returning `Task(Str, Socket)`
- `Ashes.Net.Tcp.send(socket)(text)` returning `Task(Str, Int)`
- `Ashes.Net.Tcp.receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `Ashes.Net.Tcp.close(socket)` returning `Task(Str, Unit)`
- `Ashes.Net.Tls.connect(host)(port)` returning `Task(Str, TlsSocket)`
- `Ashes.Net.Tls.send(socket)(text)` returning `Task(Str, Int)`
- `Ashes.Net.Tls.receive(socket)(maxBytes)` returning `Task(Str, Str)`
- `Ashes.Net.Tls.close(socket)` returning `Task(Str, Unit)`
- `Ashes.Async.run(task)` returning `Result(E, A)`
- `Ashes.Async.task(value)` returning `Task(Str, A)`
- `Ashes.Async.fromResult(result)` returning `Task(E, A)`
- `Ashes.Async.sleep(ms)` returning `Task(Str, Int)`
- `Ashes.Async.all(tasks)` returning `Task(E, List(A))`
- `Ashes.Async.race(tasks)` returning `Task(E, A)`
- `Ashes.Async.spawn(task)` returning `Unit`

Shipped standard-library modules under the reserved `Ashes` namespace also include:

- `Ashes.List`
- `Ashes.Maybe`
- `Ashes.Result`
- `Ashes.String`
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

## 1.1 File Structure and Top-Level Declarations

A source file is a flat sequence of imports, then top-level declarations, then an
optional trailing expression:

```
file        ::= import* declaration* expr?
declaration ::= let | letrec | type | external
letrec      ::= "let" "recursive" binding ("and" binding)*
```

- `import` lines come first (see §13.1).
- `declaration` is a top-level `let`, a `let recursive ... and ...` group, a `type`
  declaration, or an `external` declaration. Top-level `let`/`let recursive` declarations
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

### Sequential scoping (Model A)

Top-level binding scope is **sequential**, following OCaml/F# ordering:

- Each top-level binding is visible to all **subsequent** declarations and to the
  trailing expression.
- A binding is **not** visible to declarations that appear before it. Referring to a
  later binding from an earlier one is a forward-reference error
  (diagnostic `ASH014`).
- Two top-level declarations may not bind the same name (diagnostic `ASH013`).

A plain top-level `let f = ...` cannot refer to itself; self-recursion requires
`let recursive` exactly as in nested `let` bindings (see §6).

### Mutual recursion (`let recursive ... and ...`)

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

### Trailing expression

- The trailing expression is **optional**. A file containing only declarations is
  legal and, when compiled as a program, produces no output.
- When present, the trailing expression is the program's entry point in a
  single-file program.
- When a file is imported as a module, its trailing expression is **ignored
  entirely** — only its top-level declarations contribute exports (see §13.1).

### Backward compatibility

Both existing styles remain fully valid:

- A file that is a single expression (today's bare-expression style).
- The nested `let ... in` pyramid style. Nested `let ... in` expressions are
  ordinary expressions and may still appear anywhere an expression is allowed,
  including as the trailing expression of a file with top-level declarations.

---

# 2. Values

## 2.1 Integers

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

## 2.1.1 Floats

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

## 2.2 Strings

“hello”
“world”
“a” + “b”

Strings support concatenation using `+`.

Ashes strings represent UTF-8 text.

Filesystem text APIs operate on UTF-8 encoded files:

- `Ashes.File.readText(path)` returning `Result(Str, Str)`.
- `Ashes.File.writeText(path, text)` returning `Result(Str, Unit)`.
- `Ashes.File.exists(path)` returning `Result(Str, Bool)`.
- Filesystem text is interpreted and written as UTF-8.
- Invalid UTF-8 passed through `Ashes.File.readText` returns `Error(...)`.
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
  uses the same hermetic `rustls` runtime path as `https://` in `Ashes.Http`.
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

Basic HTTP client APIs live under `Ashes.Http`:

- `Ashes.Http.get(url)` returning `Task(Str, Str)`.
- `Ashes.Http.post(url, body)` returning `Task(Str, Str)`.

Example:

```ash
match Ashes.Async.run(async
  let response = await Ashes.Http.get("http://example.com")
  in response) with
  | Ok(text) -> Ashes.IO.print(text)
  | Error(err) -> Ashes.IO.print(err)
```

Current HTTP rules:

- `http://` and `https://` URLs are supported.
- `https://` defaults to port 443 and, on the current Linux x64,
  Linux arm64, and Windows x64 backends, uses the hermetic `rustls`
  runtime embedded into TLS-using executables.
- Other backends may still return a runtime error for `https://` until
  their TLS runtime support lands.
- Non-2xx responses return `Error("HTTP <status>")`.
- Chunked transfer encoding is not supported and returns `Error(...)`.
- The successful payload is the response body text after the HTTP header separator.

## 2.3 Booleans

true
false

## 2.4 Tuples

Tuple literals:

(1, "x")
(true, 42, "ok")

Rules:

- Tuples have arity 2 or more.
- `(expr)` is grouping, not a tuple.
- Tuple elements may have different types.

---

# 3. Operators

## 3.1 Arithmetic

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

## 3.2 Comparison

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
| `!=`     | `Int != Int`        | Not equal (integers)               |
| `!=`     | `Float != Float`    | Not equal (floats)                 |
| `!=`     | `uN != uN`          | Not equal (unsigned integers)      |
| `!=`     | `Str != Str`        | Not equal (strings, byte-for-byte) |

Examples:

10 >= 5         // => true
3 <= 3          // => true
1 == 1          // => true
1 != 2          // => true
"hi" == "hi"    // => true
"hi" != "bye"   // => true

Both operands of `==` and `!=` must have the same type. Mixing `Int` and `Str` is a type error.

## 3.3 Bitwise

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

## 3.4 Cons

`::` constructs a new list by prepending a head value to a tail list.

Example:

1 :: [2,3]  // => [1,2,3]

## 3.5 Pipes

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

let bumpIfOk result =
    let? n = result
    in
    Ok(n)

This is equivalent to:

let bumpIfOk result =
    match result with
        | Ok(n) ->
            Ok(n)
        | Error(e) ->
            Error(e)

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

## 3.6 Precedence and Associativity

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
| 10    | unary `-`                      | right         |
| 11    | function application           | left          |

`>=`, `<=`, `==`, and `!=` share the same precedence level in the current grammar.
Function application (both `f(x)` and `f x` whitespace syntax) binds tighter than
all operators above.

---

# 4. Type Declarations

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
directly, and helper functions are available from the shipped `Ashes.Result` module.

Generic ADTs declare their type parameters explicitly after the type name.
For migration compatibility, code may omit explicit type parameters and rely on
constructor payload names, but canonical Ashes source should declare them explicitly.
When type parameters are omitted, a payload naming the declaring type itself (a
self-recursive field, e.g. `Node(Tree, Int, Tree)`) or a primitive type (`Int`,
`Bool`, `Str`, `Bytes`, `Float`) is a concrete field type, **not** an inferred type
parameter; only genuinely unbound payload names are treated as implicit parameters.
This lets a self-recursive ADT be built by a recursive function (`let recursive build n =
… Node(build(n - 1)) …`) without the self-referential field being over-generalized.

Rules:

- The type name should begin with an uppercase letter by convention.
- Each constructor is introduced by `|`.
- Constructors may have zero or more payload parameters in parentheses.
- Type declarations appear before the expression body of the program.

## 4.1 Record Types

Record types are single-constructor ADTs with named fields. Records use a
brace-free syntax that mirrors ADT declarations and ordinary constructor calls;
Ashes source never uses curly braces.

Syntax:

type TypeName =
    | field1: Type1
    | field2: Type2

Example:

type Point =
    | x: Int
    | y: Int

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

> The earlier curly-brace record syntax (`type T = { f: T }`, `T { f = e }`,
> `{ base with f = e }`) has been removed. Sources using it are rejected with a
> diagnostic pointing to the brace-free forms above.

# 5. Let Bindings

Syntax:

let name = value
in body

Example:

let z = 20
in Ashes.IO.print(z)

Bindings are:

- immutable
- scoped to the `in` expression
- expression-based

## 5.1 External Declarations

Top-level `external` declarations expose C ABI functions to Ashes code.

Syntax:

external strlen(Str) -> Int
external getpid() -> Int = "getpid"
external type LLVMModuleRef
external LLVMModuleCreateWithName(Str) -> LLVMModuleRef

Rules:

- `external` declarations appear before the program body, alongside `type`
  declarations.
- Supported external parameter and return types are `Int`, `u8`, `u16`, `u32`,
  `u64`, `Float`, `Bool`, `Str`, opaque external types declared with
  `external type`, and pointers to supported external types using `*T`.
  Unsigned external parameters and returns use Ashes unsigned values at call
  sites and are converted to the requested C ABI width at the external boundary.
- `void` is supported for external return types and produces Ashes `Unit`.
- `Str` arguments are passed to C as null-terminated UTF-8 byte pointers.
- Opaque external types are represented as native pointer-sized words and are
  intended for handles such as LLVM-C references.
- Pointer external types are represented as native pointers and may be nested for
  C buffer and out-parameter APIs such as `*u8` and `**LLVMModuleRef`.
- The optional string after `=` overrides the C symbol name. A symbol override
  may use `symbol@library` to request a dynamic import from that shared library
  or DLL. Windows external imports require an explicit DLL name.
- External functions must be called directly; they are not first-class function
  values in this initial FFI surface.

## 5.2 Result Binding

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

## 5.3 Type Annotations

Let bindings may carry an optional type annotation between the name and `=`.

Syntax:

let name : TypeExpr = value
in body

Example:

let x : Int = 42
in Ashes.IO.print(x)

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

# 6. Recursive Bindings

Recursive bindings must be declared with `recursive`.

Syntax:

let recursive name = value
in body

Example:

let recursive loop i =
    if i >= 10
    then i
    else loop(i + 1)
in Ashes.IO.print(loop(0))

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

# 7. Functions

Anonymous functions are declared using `given`.

Syntax:

given (param1, param2, ...) -> expr

Example:

let add = given (x, y) -> x + y
in Ashes.IO.print(add(10, 5))

Functions:

- are first-class values
- may be passed as arguments
- may return functions
- may capture outer variables (closures)

## 7.1 Function Application

Ashes supports two equivalent syntaxes for calling functions.

### Parenthesized Application

The traditional syntax wraps arguments in parentheses:

f(x)
f(x, y)
f(x)(y)
f()
Ashes.IO.print(42)

Multiple parenthesized arguments are syntax sugar for curried application:

f(x, y)

is equivalent to:

f(x)(y)

This also extends to longer calls:

f(a, b, c)

is equivalent to:

f(a)(b)(c)

An empty argument list is sugar for passing the built-in `Unit` value:

f()

is equivalent to:

f(Unit)

### ML-style (Whitespace) Application

Arguments may also be passed using whitespace, without parentheses:

f x
f x y
Ashes.IO.print 42
Ashes.IO.print "hello"
Ashes.IO.print "hello"

This is pure syntax sugar. `f x y` is parsed identically to `f(x)(y)` — both
produce the same call structure and semantics. The only AST difference is a
formatting-only flag that records whether whitespace application was used.

### Left Associativity

Function application is left-associative:

f x y

parses as:

((f x) y)

which is equivalent to `f(x)(y)`.

### Precedence

Function application binds tighter than all binary operators:

f x + y

parses as:

(f x) + y

not `f (x + y)`.

### Valid Whitespace Arguments

The following tokens may appear as whitespace arguments (without parentheses):

- identifiers: `f x`
- integer literals: `f 42`
- string literals: `f "hello"`
- boolean literals: `f true`, `f false`
- list literals: `f [1, 2, 3]`

Complex expressions must be parenthesized:

f (1 + 2)
Ashes.IO.print (add 3 4)

Keywords such as `then`, `else`, `in`, `with`, `|` are never treated as
whitespace arguments.

### Examples

    let id x = x
    in Ashes.IO.print (id 42)

    let add x y = x + y
    in Ashes.IO.print (add 3 4)

    let recursive loop x y =
        if x >= 100000
        then y
        else loop (x + 1) (y + 1)
    in Ashes.IO.print (loop 0 0)

## 7.2 Parameter Sugar

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

---

# 8. If Expressions

Syntax:

if condition
then whenTrue
else whenFalse

Example:

if 10 >= 10
then "true"
else "false"

All branches must return compatible types.

---

# 9. Lists

Lists are immutable linked lists. All list operations — cons (`::`),
`Ashes.List.append`, `Ashes.List.map`, `Ashes.List.filter`, etc. —
return a **new** list. The original list is never modified.

## 9.1 Empty List

[]

## 9.2 List Literals

[1,2,3]
[“a”,“b”]

All elements must have the same type.

Mixed-type lists are invalid.

---

# 10. Cons Operator

Syntax:

x :: xs

Meaning:

Construct a new list by placing `x` at the front of `xs`.

Example:

1 :: [2,3]      // => [1,2,3]

Type rule:

If:

x : T
xs : List<T>

Then:

x :: xs : List<T>

---

# 11. Pattern Matching

Pattern matching is performed using `match`.

Syntax:

match value with
| pattern1 -> expr1
| pattern2 -> expr2

Each arm may include an optional guard:

| pattern when condition -> expr

---

## 11.1 List Patterns

Supported patterns:

[]
x :: xs

Meaning:

- `[]` matches the empty list
- `x :: xs` matches non-empty lists
  - `x` binds the first element
  - `xs` binds the remainder

Example:

match xs with
| [] -> 0
| x :: rest -> x

Exhaustiveness:

- List matches must be exhaustive.
- A match over a list must cover both structural shapes:
  - `[]`
  - `x :: xs`
- A catch-all arm (such as `_` or a non-constructor variable pattern) also
  satisfies exhaustiveness.

---

## 11.2 Tuple Patterns

Tuple patterns destructure tuples by position.

Syntax:

| (p1, p2, …) -> expr

Rules:

- Tuple pattern arity must match tuple value arity.
- Subpatterns are type-checked recursively.

Example:

match p with
| (a, b) -> a

---

## 11.3 Wildcard Pattern

`_` matches any value and binds nothing.

Example:

match xs with
| [] -> 0
| _ -> 1

The wildcard can appear in any pattern position, including inside constructor patterns:

match opt with
| None -> 0
| Some(_) -> 1

If a wildcard (or other catch-all pattern such as a non-constructor variable pattern)
appears in a `match`, all later arms are unreachable and are rejected.

---

## 11.4 Constructor Patterns

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

import Ashes.Maybe

let unwrapOr opt def =
    match opt with
        | None -> def
        | Some(x) -> x
in Ashes.IO.print(unwrapOr(Some(10))(0))

Errors:

- Unknown constructor name in pattern.
- Wrong number of pattern arguments (arity mismatch).
- Redundant constructor arm: if an earlier arm already matches a constructor, a later arm
  for the same constructor is unreachable and rejected.

> **Note (v0.x runtime representation)**
>
> In the current v0.x implementation all constructor values are heap-allocated tagged
> cells. Each cell stores a constructor tag (its 0-based index in the declaring type)
> at offset 0, followed by any payloads at subsequent 8-byte offsets.
>
> - Nullary constructors: 8-byte cell `[tag]`.
> - Arity-`n` constructors: `8 * (1 + n)` byte cell `[tag, payload0, ..., payload(n-1)]`.
>
> Pattern matching reads the tag from the heap cell and compares it against the
> expected tag for the matched constructor, so different constructors are always
> distinguishable and payload values such as `0` or negative integers are safely
> represented.
>

## 11.5 Integer Literal Patterns

Integer literal patterns match values by equality.

Syntax:

| 0 -> expr0
| 1 -> expr1
| n -> exprDefault

Rules:

- An integer literal pattern matches when the matched value equals the literal.
- Integer literal patterns may be mixed with variable and wildcard patterns.
- Integer patterns alone are never exhaustive (integers are unbounded); a catch-all
  arm (`_` or a variable) is required.

Example:

match n with
| 0 -> "zero"
| 1 -> "one"
| _ -> "other"

Negative integers are supported:

match n with
| -1 -> "negative one"
| 0 -> "zero"
| _ -> "positive"

---

## 11.6 String Literal Patterns

String literal patterns match values by equality.

Syntax:

| "hello" -> expr1
| "world" -> expr2
| s -> exprDefault

Rules:

- A string literal pattern matches when the matched value equals the literal.
- String patterns alone are never exhaustive; a catch-all arm is required.

Example:

match greeting with
| "hello" -> "English"
| "hola" -> "Spanish"
| _ -> "unknown"

---

## 11.7 Boolean Literal Patterns

Boolean literal patterns match `true` or `false`.

Syntax:

| true -> expr1
| false -> expr2

Rules:

- Boolean literal patterns match when the value equals the literal.
- A match covering both `true` and `false` is exhaustive.

Example:

match flag with
| true -> "yes"
| false -> "no"

---

## 11.8 Pattern Guards

Match arms can include an optional `when` guard clause that adds a boolean
condition to the pattern.

Syntax:

| pattern when condition -> expr

Semantics:

1. The pattern is matched first.
2. If the pattern matches, the `when` condition is evaluated.
3. If the condition is `true`, the branch expression is executed.
4. If the condition is `false`, matching continues with the next arm.

The guard expression has access to all bindings introduced by the pattern.

Example:

match x with
| n when n >= 10 -> "big"
| _ -> "small"

Example with constructor patterns:

type Outcome =
    | Good(Int)
    | Bad(String)

match outcome with
| Good(n) when n >= 100 -> "excellent"
| Good(n) -> "ok"
| Bad(e) -> e

Exhaustiveness:

- A guarded arm does not count toward exhaustiveness, because the guard
  may be `false`. A match must still have unguarded arms that cover all
  patterns.
- A guarded catch-all pattern (e.g. `_ when cond`) does not make
  subsequent arms unreachable.

Desugaring model:

`| p when cond -> expr` behaves like:

| p -> if cond then expr else <continue to next arm>

Pattern guards are syntax sugar — no new evaluation rules are introduced.

---

## 11.9 Let Pattern Bindings

Let expressions support destructuring patterns on the left side of `=`.

Syntax:

let (a, b) = expr in body
let x :: xs = expr in body

Rules:

- Only irrefutable patterns (patterns that always match) are allowed.
- Tuple patterns are irrefutable when the arity matches.
- Variable patterns and wildcard patterns are irrefutable.
- Constructor patterns (e.g. `Some(x)`) are not allowed in let bindings
  because they are refutable — use `match` instead.
- Integer, string, and boolean literal patterns are not allowed in let
  bindings because they are refutable.
- List cons patterns (`x :: xs`) are allowed but will fail at runtime
  if the list is empty.

Example:

let (x, y) = (1, 2)
in x + y

let first :: rest = [1, 2, 3]
in first

---

# 12. Recursion over Lists

Example:

let recursive sum lst acc =
    match lst with
        | [] -> acc
        | x :: rest -> sum(rest)(acc + x)
in Ashes.IO.print(sum([1, 2, 3])(1))

Default-value list utilities are written as regular user code, for example:

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

---

# 13. Standard Library Modules

Ashes has **no implicit prelude**. All standard library functions live under the
`Ashes` namespace and must be accessed explicitly.

## 13.1 Accessing Standard Library

Ashes has no implicit standard-library open. Unqualified `print`, `panic`, and
`args` are available only after `import Ashes.IO`, while the qualified
`Ashes.IO.*` forms are always valid.

`write` and `writeLine` remain qualified-only and must be called as
`Ashes.IO.write(...)` and `Ashes.IO.writeLine(...)`.

There are two common ways to use standard library functions:

### Qualified access (no import required)

    Ashes.IO.print "hello"
    Ashes.IO.panic "boom"
    Ashes.IO.args

### Import and use unqualified names

    import Ashes.IO

    print "hello"
    panic "boom"

`import Module` brings the module's exported names into local scope. The import
must appear at the top of the source file, before any expressions.

### Import Aliasing

An import may include an alias using `as`:

    import Ashes.IO as IO

    IO.print "hello"

The alias is a short name that can be used in place of the full module path for
qualified access.  Unqualified access to the module's exported names is still
available (e.g. `print "hello"` still works after `import Ashes.IO as IO`).

The alias must be a valid identifier (letter followed by alphanumerics/underscores).
Aliases may be lowercase (e.g. `import Ashes.List as list`).

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

### Import Selectors

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
import Ashes.List.map as listMap
import Ashes.Result.Result as R
```

Rules:

- `import M.name` makes `name` (a binding or type exported by `M`) available
  unqualified. `import M.name as alias` makes it available as `alias` instead.
- The selected name must be an export of `M` (see “Module Exports” below).
- If two unqualified selectors bring the **same** unqualified name into scope, it is
  a compile-time error (diagnostic `ASH016`). Resolve the conflict with `as`.
- Selector imports compose with whole-module imports; the same module may be imported
  both wholesale and via selectors.

Built-in standard-library modules (`Ashes.IO`, `Ashes.List`, `Ashes.Text`,
`Ashes.Result`, `Ashes.Maybe`, etc.) resolve through the **identical** path as
user modules: each module has a known set of exported bindings and types, and
selectors are checked against that set. For example `import Ashes.IO.print` and
`import Ashes.IO.print as p` behave exactly like selectors against a user module.

### Module Exports

When a file is imported as a module, its exports are:

- all top-level `let` bindings,
- all bindings of top-level `let recursive ... and ...` groups, and
- all top-level `type` declarations (and their constructors).

The following are **never** exported:

- `external` declarations, and
- the trailing expression.

There is **no implicit re-export**: names a module itself imported from other
modules are not re-exported to its importers. Each importer must import what it
needs directly.

### Inline Modules

A file may declare **inline modules** — nested, named namespaces — directly in
its body, so related `let` / `type` declarations can be grouped under a
qualifier without spawning a new file. This is purely a compile-time namespacing
feature: an inline module has no runtime representation, is not a value, and is
erased during lowering exactly as a file module is.

```
module Geometry =
    let pi = 3.14159
    let area = given (r) -> pi * r * r

Ashes.IO.print(Ashes.Text.fromFloat(Geometry.area(2.0)))
```

- **Introducer.** `module Name =` (a capitalized `UpperCamel` name), followed by
  a **layout block**: the run of lines indented past the `module` keyword. The
  block ends at the first line dedented back to (or past) the `module` column —
  the same column rule the parser uses to find the next top-level item. `module`
  is recognized only in this declaration position; it remains an ordinary
  identifier elsewhere.
- **Members.** `let`, `let recursive ... and ...`, `type`, and nested `module`
  declarations — the same forms a file may contain. A `module` block may **not**
  contain a trailing expression or an `external` declaration.
- **Identity.** An inline module is an **exported submodule** of its file:
  `File.Inner.member` is path-addressable from other files, so promoting an
  inline module to its own file (`File/Inner.ash`) leaves every `import` and call
  site unchanged. A separate inline module and a file that resolve to the *same*
  path are a compile-time ambiguity error.
- **Exports.** Identical to file modules (above): all top-level `let` bindings,
  all `let recursive ... and ...` groups, and all `type` declarations with their
  constructors are exported; nested modules are exported as submodules. No
  implicit re-export.
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
[DIAGNOSTICS.md](DIAGNOSTICS.md)); unknown-member, unknown-selector, and
import-collision cases reuse `ASH013`–`ASH016`, since inline modules resolve
through the same path as file modules.

## 13.2 Ashes.IO Module

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

- `Ashes.File.readText(path)` returning `Result(Str, Str)` - UTF-8 file read.
- `Ashes.File.writeText(path, text)` returning `Result(Str, Unit)` - UTF-8 file write.
- `Ashes.File.exists(path)` returning `Result(Str, Bool)` - filesystem existence check.
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
- `Ashes.Http.get(url)` returning `Task(Str, Str)` - async HTTP GET for `http://` and `https://` URLs.
- `Ashes.Http.post(url, body)` returning `Task(Str, Str)` - async HTTP POST for `http://` and `https://` URLs.

## 13.3 Built-in Runtime Types

The compiler also provides built-in runtime ADTs:

    type Unit =
        | Unit

    type Maybe(T) =
        | None
        | Some(T)

    type Result(E, A) =
        | Ok(A)
        | Error(E)

`Maybe` and `Result` behave like any other algebraic data type during type checking
and pattern matching.

Examples:

        let value = Some("hello")
        in
        match value with
                | None -> Ashes.IO.print("empty")
                | Some(text) -> Ashes.IO.print(text)

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

## 13.4 Error Handling

Ashes uses explicit `Result(E, A)` values for recoverable failures.

Idiomatic Result handling patterns are:

- `match value with | Ok(x) -> ... | Error(e) -> ...` when both branches must be handled explicitly.
- `let? name = value in body` when a sequence of Result-returning operations should short-circuit on `Error`.
- `|?>` when a Result-success pipeline reads more clearly than nested matches.
- `|!>` when only the error payload should be transformed.

Example:

let describe result =
    match result with
        | Ok(value) -> Ashes.IO.writeLine("ok")
        | Error(message) -> Ashes.IO.writeLine(message)
in describe(Ok(1))

`Ashes.IO.panic(message)` is reserved for unrecoverable failures.
It prints the provided message and terminates execution with a non-zero exit code.

## 13.5 Shipped Standard Library Modules

Ashes also ships pure library modules implemented in Ashes source.
Every function in these modules is pure: it returns a new value and
never mutates its arguments.

Current shipped modules include:

- `Ashes.List` — helper functions for the built-in list type.
- `Ashes.Map` — persistent immutable map helpers and the shipped `MapTree(K, V)` value type.
- `Ashes.Maybe` — helper functions for the built-in `Maybe(T)` runtime type.
- `Ashes.Result` — helper functions for the built-in `Result(E, A)` runtime type.
- `Ashes.String` — pure string helpers built on `Ashes.Text`.
- `Ashes.Test` — assertion helpers for tests and small programs.

Stable helper surfaces:

### `Ashes.List`

- `Ashes.List.append : List<a> -> List<a> -> List<a>`
- `Ashes.List.length : List<a> -> Int`
- `Ashes.List.head : List<a> -> Maybe(a)`
- `Ashes.List.tail : List<a> -> Maybe(List<a>)`
- `Ashes.List.map : (a -> b) -> List<a> -> List<b>`
- `Ashes.List.filter : (a -> Bool) -> List<a> -> List<a>`
- `Ashes.List.foldLeft : (b -> a -> b) -> b -> List<a> -> b`
- `Ashes.List.fold : (a -> b -> b) -> b -> List<a> -> b`
- `Ashes.List.isEmpty : List<a> -> Bool`
- `Ashes.List.reverse : List<a> -> List<a>`

### `Ashes.Map`

- `Ashes.Map.empty : MapTree(k, v)`
- `Ashes.Map.isEmpty : MapTree(k, v) -> Bool`
- `Ashes.Map.get : (k -> k -> Int) -> k -> MapTree(k, v) -> Maybe(v)`
- `Ashes.Map.contains : (k -> k -> Int) -> k -> MapTree(k, v) -> Bool`
- `Ashes.Map.set : (k -> k -> Int) -> k -> v -> MapTree(k, v) -> MapTree(k, v)`
- `Ashes.Map.insert : (k -> k -> Int) -> k -> v -> MapTree(k, v) -> MapTree(k, v)`
- `Ashes.Map.size : MapTree(k, v) -> Int`
- `Ashes.Map.foldLeft : (s -> k -> v -> s) -> s -> MapTree(k, v) -> s`
- `Ashes.Map.toList : MapTree(k, v) -> List<(k, v)>`
- `Ashes.Map.fromList : (k -> k -> Int) -> List<(k, v)> -> MapTree(k, v)`

`Ashes.Map` is a persistent AVL tree. Callers provide a total ordering
function because the language does not yet have a built-in ordering abstraction.

### `Ashes.Maybe`

- `Ashes.Maybe.default : a -> Maybe(a) -> a`
- `Ashes.Maybe.flatMap : (a -> Maybe(b)) -> Maybe(a) -> Maybe(b)`
- `Ashes.Maybe.getOrElse : a -> Maybe(a) -> a`
- `Ashes.Maybe.isNone : Maybe(a) -> Bool`
- `Ashes.Maybe.isSome : Maybe(a) -> Bool`
- `Ashes.Maybe.map : (a -> b) -> Maybe(a) -> Maybe(b)`
- `Ashes.Maybe.unwrapOr : Maybe(a) -> a -> a`

### `Ashes.Result`

- `Ashes.Result.default : a -> Result(E, a) -> a`
- `Ashes.Result.bind : (a -> Result(E, b)) -> Result(E, a) -> Result(E, b)`
- `Ashes.Result.map : (a -> b) -> Result(E, a) -> Result(E, b)`
- `Ashes.Result.flatMap : (a -> Result(E, b)) -> Result(E, a) -> Result(E, b)`
- `Ashes.Result.getOrElse : a -> Result(E, a) -> a`
- `Ashes.Result.isOk : Result(E, a) -> Bool`
- `Ashes.Result.isError : Result(E, a) -> Bool`
- `Ashes.Result.mapError : (E -> F) -> Result(E, a) -> Result(F, a)`

### `Ashes.String`

- `Ashes.String.substring : Str -> Int -> Int -> Str`
- `Ashes.String.length : Str -> Int`
- `Ashes.String.indexOf : Str -> Str -> Int`
- `Ashes.String.startsWith : Str -> Str -> Bool`
- `Ashes.String.contains : Str -> Str -> Bool`
- `Ashes.String.split : Str -> Str -> List<Str>`
- `Ashes.String.trim : Str -> Str`
- `Ashes.String.isLetter : Str -> Bool`
- `Ashes.String.isDigit : Str -> Bool`
- `Ashes.String.isWhiteSpace : Str -> Bool`

`Ashes.Test` currently exports:

- `assertEqual(expected, actual)` — succeeds when the two values are equal and
    aborts via `Ashes.IO.panic` when they are not.
- `fail(message)` — always aborts via `Ashes.IO.panic`.

Example:

        import Ashes.Test

        let checked = assertEqual(3, 3)
        in Ashes.IO.print("ok")

Like other multi-argument calls in Ashes, `assertEqual(expected, actual)` is
surface sugar for curried application.

These helper modules are compiler-shipped and live under the reserved `Ashes.*`
namespace. User projects cannot override them with project-local modules.

## 13.6 Future Standard Library Modules

The module system supports nested module paths. Future modules are tracked in
[future/FUTURE_FEATURES.md](future/FUTURE_FEATURES.md).

The `Ashes` namespace is reserved and cannot be used for user-defined modules.
This applies to `Ashes` itself and to any `Ashes.*` module path.

## 13.7 Core Language vs Library

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

# 14. Type Inference

Ashes uses static Hindley-Milner style type inference with let-polymorphism.

## 14.1 Let-Polymorphism

For non-recursive `let` bindings, inferred types are generalized into type schemes.
Conceptually, this is `forall` quantification (for example `forall a. a -> a`), even
though users do not write `forall` syntax in source code.

At each use site of a generalized identifier, the compiler instantiates the scheme
with fresh type variables. This allows one binding to be reused at multiple types.

Example:

    let id x = x
    in
    let _a = id(1)
    in
    let _b = id("x")
    in Ashes.IO.print("ok")

`id` is inferred once, generalized, then instantiated separately for integer and string uses.

## 14.2 Recursion and Polymorphism

`let recursive` bindings are monomorphic during inference. The recursive name is checked
using a single monotype inside its own definition, so polymorphic recursion is not inferred.

## 14.3 Infinite Types (Occurs Check)

Inference rejects infinite/recursive types via an occurs check. Self-application patterns
such as `x(x)` are invalid, because they would require a type variable to contain itself.

## 14.4 What to Expect

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

# 15. Evaluation Strategy

Ashes is:

- strictly evaluated
- immutable
- recursion-based
- pure — all functions return new values; no function mutates its arguments

Iteration is expressed using recursion and pattern matching.

**Purity contract.** Every standard library and user-defined function
is pure in the following sense:

- Calling a function never changes the value of any existing binding.
- Operations that conceptually "add" or "remove" (e.g. `Ashes.List.append`,
  cons `::`, `Ashes.List.filter`) always return a **new** value; the
  original is unmodified.
- There are no in-place updates. If a program needs a modified version of
  a value, it builds one via expression — the original remains available
  until it goes out of scope.

The compiler and runtime may optimize representation internally (structure
sharing, in-place reuse when safe), but these optimizations are invisible
to user code. From the programmer's perspective, every value is immutable
once created.

---

# 16. Resource Types and Deterministic Cleanup

Certain built-in types represent external system resources (file handles,
sockets). These are called **resource types**.

Currently classified resource types:

- `Socket` — TCP socket handles from `Ashes.Net.Tcp.connect`
- `TlsSocket` — TLS session handles from `Ashes.Net.Tls.connect` or a server-side
  `Ashes.Net.Tls.Server.handshake`

## 16.1 Automatic Cleanup

Resource bindings are automatically cleaned up when they go out of scope.
The compiler inserts cleanup calls at the end of every scope that contains
a live resource binding. This includes:

- `let` binding scopes
- `match` case branches
- The program's top-level scope

Users do not write cleanup calls manually unless they want explicit control
over when a resource is released.

## 16.2 Explicit Close

Resources may be closed explicitly using the appropriate API:

- `Ashes.Net.Tcp.close(socket)` — closes a socket
- `Ashes.Net.Tls.close(socket)` — closes a TLS session

When a resource is closed explicitly, the automatic cleanup for that
resource is skipped (no double close).

## 16.2.1 Move on Transfer

Ownership of a resource **moves** out of a scope when the resource is handed off to something that
takes responsibility for it — so the original scope no longer cleans it up. Ownership moves when a
resource binding is:

- stored into an aggregate (constructor field, tuple element, list cell),
- passed as an argument to a function or handler call, or
- passed to `Ashes.Async.spawn`.

Because Ashes has no borrowing, passing a resource to a function transfers it: the callee now owns
the resource (and is responsible for closing it), and the caller must not use or close it afterward.
This is what lets a combinator hand an accepted socket to an opaque handler that closes it, without
the combinator's own scope closing it a second time (for example `Ashes.Net.Tcp.Server.serve` and
`Ashes.Net.Tls.Server.serveTls`).

## 16.3 Compile-Time Safety

The compiler enforces resource safety with two rules:

1. **No use-after-close.** Using a resource after it has been closed
   (passing it to `send` or `receive`) is a compile-time error
   (diagnostic `ASH006`).

2. **No double-close.** Calling `close` on an already-closed resource
   is a compile-time error (diagnostic `ASH007`).

These checks are performed at compile time during semantic analysis.

## 16.4 What Is Not Affected

Resource safety rules (use-after-close, double-close) apply only to resource
types. General owned types (String, List, ADTs, closures) receive automatic
Drop but have no restrictions on reuse — see §17.

## 16.5 No Garbage Collection

All resource cleanup is deterministic and compile-time verified. There is
no garbage collector. The compiler guarantees exactly-once cleanup for every
resource binding.

---

# 17. Ownership Model

Ashes uses an **implicit sharing** model for memory management. Every
value has a clear owner, but ownership is managed entirely by the
compiler — users never write move, borrow, or drop operations.

## 17.1 Copy vs Owned Types

| Category | Types | Behaviour |
|----------|-------|-----------|
| **Copy** | `Int`, `Float`, `Bool` | Stack-allocated, trivially duplicated. No cleanup needed. |
| **Owned** | `String`, `List`, `Tuple`, `Function` (closures), ADTs (`Result`, `Maybe`, user-defined), resource types (`Socket`) | Heap-allocated. The compiler inserts `Drop` at scope exit. |

Copy types may be used any number of times without restriction.

Owned types are tracked by the compiler for deterministic cleanup.

## 17.2 Implicit Sharing

Values in Ashes are **implicitly shared**. When a binding is used —
passed to a function, stored in a data structure, or returned from a
scope — the compiler shares it automatically. There is no explicit
borrow syntax (`&x`), no move keyword, and no use-after-move errors.

The compiler decides when to share (borrow) and when to copy. Since all
values are immutable, sharing is always safe.

## 17.3 Deterministic Drop

Every owned binding receives a `Drop` at the end of its owning scope.
This applies to:

- `let` binding scopes
- `match` case branches
- The program's top-level scope

For resource types (Socket), `Drop` invokes platform-specific cleanup
(e.g. close the socket). For other owned types, `Drop` is currently a
no-op in the linear allocator and will perform actual deallocation when
a freeing allocator is introduced.

## 17.4 Moves as Optimisation

"Move" in Ashes is a **compiler optimisation**, not a user-visible
operation. When the compiler can prove a value is used for the last
time, it may transfer the underlying memory rather than sharing. This
is invisible to user code — the program behaves identically regardless
of whether a move or share occurs.

## 17.5 Borrowing Is Inferred

Borrowing is **compiler-inferred**, not user-annotated. There is no
`&x` syntax, no borrow operator, and no lifetime annotations.

When an owned value is accessed — passed to a function, used in an
expression, or bound to another name — the compiler automatically
emits a borrow. A borrowed reference is a non-owning access: it
carries no responsibility for cleanup. The owning scope still drops
the value when it exits.

Since Ashes values are immutable, inferred borrowing is always safe:

- Multiple borrows of the same value can coexist without conflict.
- Borrows cannot outlive the owning scope (enforced by scope structure).
- There are no data races because there is no mutation.

Copy types (`Int`, `Float`, `Bool`) are never borrowed — they are
trivially duplicated on the stack.

Internally the compiler represents a borrow as a `Borrow` IR
instruction: `Borrow(target, source)`. In the current backend this
is a simple pointer pass-through. Future phases may use borrow
information for copy elision and in-place reuse optimizations.

## 17.6 No Garbage Collection

All ownership and cleanup is deterministic and resolved at compile time.
There is no garbage collector, no reference counting at runtime (in the
current implementation), and no finalizers.

---

# 18. Optimization

The Ashes compiler performs multiple levels of optimization, all invisible
to user code. Observable behaviour is always preserved — optimizations
never change what a program prints, returns, or does.

## 18.1 IR-Level Optimizations

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

## 18.2 Backend Optimizations

The LLVM backend applies instruction-level optimizations during code
generation via the target machine's optimization level (O0 through O3).
This includes register allocation, instruction scheduling, and
peephole optimizations performed by LLVM's code generator.

## 18.3 Tail-Call Optimization

Tail-recursive functions are optimized into constant-stack loops by the
compiler. When the compiler detects that a recursive call is in tail
position, it rewrites the call as a jump back to the function entry,
reusing the current stack frame. This means recursive functions like:

    let recursive sum n acc =
        if n == 0 then acc
        else sum(n - 1)(acc + n)
    in sum(1000000)(0)

run in constant stack space, without risk of stack overflow.

### 18.3.1 Mutual Recursion

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

### 18.3.2 Stack Depth and Non-Tail Recursion

Only tail calls are rewritten into loops. A recursive call whose result
is still consumed by the caller (for example `n * factorial(n - 1)`, or
rebuilding a list around the recursive result) occupies one stack frame
per active call, so its maximum depth is bounded by the thread's stack:

- **Main thread.** The executable does not override the platform stack.
  On Linux targets the main thread gets the operating system's default
  stack limit (`RLIMIT_STACK`, commonly 8 MiB). On `win-x64` the image
  reserves 8 MiB by default.
- **Parallel workers.** `Ashes.Parallel` workers default to a 1 MiB
  stack, configurable with the `--parallel-stack-size` compile flag (see
  the CLI specification).

Exhausting the stack is not a diagnosed error: the process faults
(segmentation fault on Linux, stack-overflow exception on Windows). For
unbounded input sizes, structure the recursion so the recursive call is
in tail position, typically by threading an accumulator as in the `sum`
example above.

## 18.4 Zero-Cost Abstraction Philosophy

Values are immutable and freely shared; the compiler handles ownership
and memory safely behind the scenes. Optimizations are never visible to
user code. The compiler is free to reorder, eliminate, or restructure
internal operations as long as the observable result is identical.

---

# 19. Async/Await

Ashes supports task-based concurrency via `Task` values and `await`.

## 19.1 Task Type

    Task(E, A)

`Task(E, A)` is a built-in parametric type representing an asynchronous
computation that may fail with error type `E` or succeed with value type `A`.

- `Task` is an **owned type** (like `String`, `List`, closures).
- `Task` values are **not resource types** — they do not have use-after-close
  or double-close restrictions.
- The compiler inserts `Drop` for `Task` at scope exit like any other owned type.

## 19.2 Await Expressions

`await <expr>` resolves a `Task(E, A)` to `Result(E, A)`:

    let result = await Ashes.Http.get("http://example.com")
    in result

- `await <expr>` where `<expr> : Task(E, A)` produces `Result(E, A)`.
- `await` runs the task to completion.
- `Ashes.Async.run(task)` has equivalent return shape (`Result(E, A)`), so `await` can be used directly.

## 19.3 Async Let (let!)

`let!` is sugar for `await` in a binding position:

    let! response = Ashes.Http.get("http://example.com")
    in response

This desugars to:

    let response = await Ashes.Http.get("http://example.com")
    in response

`let!` flattens binding chains — no additional nesting per await point.
Multiple `let!` bindings chain sequentially:

    let! a = Ashes.Http.get("http://a.com")
    let! b = Ashes.Http.get("http://b.com")
    in a + b

Desugars to:

    let a = await Ashes.Http.get("http://a.com")
    in
        let b = await Ashes.Http.get("http://b.com")
        in a + b

## 19.4 Type Inference Rules

- `await <expr>` where `<expr> : Task(E, A)` produces `Result(E, A)`.
- Functions returning `Task` are regular functions.

## 19.5 Result Interop

`Task(E, A)` and `Result(E, A)` share the same error-propagation model:

- `Ashes.Async.fromResult(result)` — wraps a `Result(E, A)` into a
  `Task(E, A)` that completes immediately.
- `Ashes.Async.task(value)` — wraps a value into an already successful `Task(Str, A)`.
- `Ashes.Async.run(task)` — runs a task to completion and returns
  `Result(E, A)`.

## 19.6 Ashes.Async Module

| Function | Type |
|----------|------|
| `Ashes.Async.run(task)` | `Task(E, A) -> Result(E, A)` |
| `Ashes.Async.task(value)` | `A -> Task(Str, A)` |
| `Ashes.Async.fromResult(r)` | `Result(E, A) -> Task(E, A)` |
| `Ashes.Async.sleep(ms)` | `Int -> Task(Str, Int)` |
| `Ashes.Async.all(tasks)` | `List(Task(E, A)) -> Task(E, List(A))` |
| `Ashes.Async.race(tasks)` | `List(Task(E, A)) -> Task(E, A)` |
| `Ashes.Async.spawn(task)` | `Task(E, A) -> Unit` |

`Ashes.Async.spawn(task)` detaches a task for fire-and-forget execution: the task advances
cooperatively whenever any `Ashes.Async.run` drive is blocked waiting (on a socket or timer), and
its result is dropped when it completes. Ownership of any resources the task references (for
example an accepted socket) moves into the detached task — the spawning scope no longer closes
them, so the task must release its own resources. Detached tasks still in flight when the driving
`run` completes (and the program exits) are abandoned. This is the concurrency primitive behind
`Ashes.Net.Tcp.Server.serve`'s concurrent connection handling.

### 19.6.1 Ashes.Async.sleep

`Ashes.Async.sleep(ms)` creates a task that suspends for the given
number of milliseconds, then completes with `0`:

    let _ = await Ashes.Async.sleep(100)
    in 42

- The argument is an `Int` representing milliseconds.
- The returned task has type `Task(Str, Int)`.
- On completion, the result is `0` (unit placeholder).
- `sleep` is consumed via `await`.
- On Linux, `sleep` uses the `nanosleep` syscall.
- On Windows, `sleep` uses the `Sleep` kernel32 function.

### 19.6.2 Ashes.Async.all

`Ashes.Async.all(tasks)` takes a list of tasks and runs them all,
collecting results into a list in the original order:

    let results = await Ashes.Async.all([
        Ashes.Async.task(1),
        Ashes.Async.task(2),
        Ashes.Async.task(3)
    ])
    in results

- The argument is a `List(Task(E, A))`.
- The returned task has type `Task(E, List(A))`.
- All tasks are run sequentially (left to right).
- Results are collected in the same order as the input list.
- An empty input list produces an empty result list.

### 19.6.3 Ashes.Async.race

`Ashes.Async.race(tasks)` takes a list of tasks and returns the result
of the first task to complete:

    let result = await Ashes.Async.race([
        Ashes.Async.task(42),
        Ashes.Async.task(99)
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

## 19.8 Diagnostics

Async/await has no dedicated diagnostic codes. `async` is a builtin
(`Ashes.Async.task`), not a block keyword, so there is no "outside `async`"
state to police; misuse (for example combining tasks with mismatched error
types, or consuming a `Task` without `await`/`Ashes.Async.run`) surfaces through
ordinary type-inference diagnostics. See [DIAGNOSTICS.md](DIAGNOSTICS.md) for the
full code table.

---

# 20. Capabilities and Handlers

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
resolve concrete instances (`Clock`, `Ord(Str)`) directly, and generic requirements (`needs {Ord(a)}`)
by monomorphization or dictionary passing (§20.6); providers are program-global across modules.

Implementation status: the full surface is implemented — capability declarations, `needs` rows,
capability typing, the unsatisfied-capability diagnostic, `handle`/`perform` with
**tail-resumptive and one-shot resumptive** arms, first-class operation values (for operations with
explicit signatures), static `provide` with concrete and generic (monomorphized / dictionary-passed)
resolution, and capabilities and providers declared in imported project modules. The former
`effect` / `uses` spellings are renamed to `capability` / `needs` and now produce a rename diagnostic
(`ASH025`). Aborting arms (a path that never resumes) and multi-shot `resume` are rejected with a
clear diagnostic — see section 20.7 for why. Capabilities
interacting with `async`/`await` state machines or `Ashes.Parallel` worker threads is not yet
defined; handler evidence is currently per-process, not per-task or per-thread (see
[future/FUTURE_FEATURES.md](future/FUTURE_FEATURES.md)). How handlers compile
(dynamically-scoped evidence globals, stack-allocated frames, the `resume` rewrites) is
documented in [ARCHITECTURE.md](ARCHITECTURE.md).

## 20.1 Capability Declarations

A capability is a named set of operations, declared at the top level like a `type`:

```
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

## 20.2 Performing an Operation

```
let t = perform Clock.now(Unit)  // explicit form
let t = Clock.now(Unit)          // implicit form — identical program
```

`perform` is an **optional** keyword: `perform Clock.now(x)` and `Clock.now(x)` are the same
program. The keyword is a greppability marker; the capability row in the type is the source of truth.
The formatter preserves whichever form was written. `perform` must be applied to a capability
operation call (`perform 42` is an error), and operations are always qualified by their capability
(`Clock.now`), so no ambiguity arises when two capabilities share an operation name.

## 20.3 Capability Rows (`needs`) in Type Annotations

A function type may carry a `needs` clause listing the capabilities the function performs:

```
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

## 20.4 Capability Typing

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

## 20.5 Handlers

```
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

## 20.6 Static Providers (`provide`)

A `handle` satisfies a capability *dynamically* — for the extent of a scope. A **provider**
satisfies it *statically*: `provide` supplies a fixed implementation for a **concrete** capability
instance, resolved at compile time with no handler evidence.

```
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
  at the call site — `provide Clock`, or `Ord(Str)` on `Str` values. It also resolves a call inside
  a **generic function** that is *specialized* per concrete use: a non-recursive `let`-bound
  function whose body performs `Cap.op` is **inlined at each concrete call site**, so the operation
  resolves against the caller's type. The same generic function used at two types is monomorphized
  to both:

  ```
  let display = given (x) -> Show.show(x)
  display(42)      // resolves to provide Show(Int)
  display(true)    // resolves to provide Show(Bool)  — same function, two instances
  ```

- **Generic resolution (dictionary passing).** A function that uses a capability operation at a
  generic type and is **annotated** with an explicit `needs {Cap(a)}` row is compiled by dictionary
  passing: each operation of each parameterized needed capability becomes a hidden parameter, the
  operation calls in the body reference it, and every call site supplies the implementation — from a
  provider (concrete instance) or by threading the caller's own hidden parameter (still-abstract
  instance). Because the operation is a runtime value, this covers the shapes inlining cannot:
  **recursive** and **higher-order** generics.

  ```
  let min : List(a) -> a needs {Ord(a)} =
      given (items) ->
          match items with
              | [] -> io.panic("empty")
              | x :: xs ->
                  list.foldLeft(given (best) -> given (next) ->     // Ord.compare inside a closure
                      if Ord.compare(next)(best) < 0 then next else best)(x)(xs)

  min([5, 3, 1])   // Ord(Int) provider threaded in — no handler needed
  ```

  A `needs` row may mix dynamic and static capabilities (`needs {Clock, Ord(a)}`): the
  unparameterized ones (`Clock`) are still satisfied by a handler or provider dynamically, while the
  parameterized ones (`Ord(a)`) are dictionary-passed. A generic use with **no** `needs` annotation
  is not dictionary-passed; the compiler reports a diagnostic suggesting the annotation (or a
  concrete call site / handler).

- **Providers are program-global (coherence).** A `provide` is visible across the whole program, in
  every module, regardless of imports — like an instance in a coherent typeclass system. A capability
  declared in one module and a `provide` for it in another both satisfy a `needs` requirement anywhere,
  and a generic function annotated `needs {Cap(a)}` may be defined in one module and called from
  another; the provider is resolved (or the dictionary threaded) at the call site. Because providers
  are global, a duplicate `provide` for the same concrete instance is a program-wide error (`ASH026`),
  which is what keeps resolution coherent — the same instance always resolves the same way.

  One case is not yet supported across modules: a dictionary-passing function that itself calls an
  *imported* dictionary-passing function through a qualified reference (`Other.f`) at a still-generic
  type — the caller's dictionary is not threaded across the module boundary. Define such a wrapper in
  the module that provides the function, or call it at a concrete type.

## 20.7 Worked Example

The same business code runs under any handler; only the interpretation changes:

```
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

## 20.8 Design Notes

Ashes uses **lexical handler injection** (the OCaml 5 / Koka / Eff / Frank / Unison family):
the nearest enclosing handler interprets an operation. The two other ML/FP injection routes are
deliberately not used because the language lacks their prerequisites: typeclass/monad-transformer
injection (Haskell `mtl`, tagless-final; Scala ZIO environments) needs typeclasses, and functor
injection (SML/OCaml functors) needs module functors — Ashes has neither. Relative to OCaml 5,
Ashes adds what OCaml deliberately omitted: capabilities are tracked in the type system, so an
unhandled capability is a *compile-time* error, not a runtime crash. Relative to Koka, Ashes
restricts continuations to one-shot/tail-resumptive: multi-shot `resume` would require copying a
captured slice of stack and heap — GC-style reachability — which collides with the no-GC memory
model and affine ownership (double-resume is double-use/double-drop of owned values).
Consequently capability-based generators, backtracking, and nondeterminism are out of scope —
documented limitation, not a TODO.

## 20.9 Diagnostics

`ASH017` (unsatisfied capability), `ASH018` (capability not permitted by a closed row, and the
generic-provider limitation), `ASH019` (unknown capability or operation), `ASH020` (invalid
handler), `ASH025` (the old `effect`/`uses` spellings), `ASH026` (duplicate/incomplete provider),
and `ASH027` (a capability satisfied by both a provider and a handler) cover this surface; see
[DIAGNOSTICS.md](DIAGNOSTICS.md).

---

# 21. Unsupported (Future)

See [future/FUTURE_FEATURES.md](future/FUTURE_FEATURES.md) for the list of planned but not yet supported features.

Note: project-mode `import Foo` / `import Foo.Bar` lines are supported by the project system
(`ashes.json` + `PROJECT_SPEC.md`) and are resolved before expression parsing. Built-in
`import Ashes.IO` is handled by the compiler directly.
