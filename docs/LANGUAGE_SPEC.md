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

`let`, `rec`, `in`, `if`, `then`, `else`, `match`, `with`, `fun`,
`true`, `false`, `type`, `async`, `await`

Programs are composed using nested expressions such as:

let x = 10
in Ashes.IO.print(x + 1)

Built-in standard library members live under reserved `Ashes` modules.

Canonical built-ins available today include:

- `Ashes.IO.print(expr)`
- `Ashes.IO.panic("message")`
- `Ashes.IO.args`
- `Ashes.IO.write(expr)`
- `Ashes.IO.writeLine(expr)`
- `Ashes.IO.readLine()`
- `Ashes.File.readText(path)`
- `Ashes.File.writeText(path, text)`
- `Ashes.File.exists(path)`
- `Ashes.Http.get(url)`
- `Ashes.Http.post(url, body)`
- `Ashes.Net.Tcp.connect(host)(port)`
- `Ashes.Net.Tcp.send(socket)(text)`
- `Ashes.Net.Tcp.receive(socket)(maxBytes)`
- `Ashes.Net.Tcp.close(socket)`
- `Ashes.Async.run(task)`
- `Ashes.Async.fromResult(result)`

`Ashes` is reserved for compiler-provided modules and cannot be redefined by user code.
The reserved `Ashes` namespace is a module root, not a direct alias surface for
`print`, `panic`, or `args`; those live under `Ashes.IO` only.

---

# 2. Values

## 2.1 Integers

10
42

Integer literals are non-negative decimal values.
Negative integers are written with unary negation, for example `-1`.

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

- `Ashes.File.readText(path)` returns `Result(Str, Str)`.
- `Ashes.File.writeText(path, text)` returns `Result(Str, Unit)`.
- `Ashes.File.exists(path)` returns `Result(Str, Bool)`.
- Filesystem text is interpreted and written as UTF-8.
- Invalid UTF-8 passed through `Ashes.File.readText` returns `Error(...)`.
- Binary file APIs are not part of the current language surface.

Networking APIs live under `Ashes.Net.Tcp`:

- `Ashes.Net.Tcp.connect(host)(port)` returns `Result(Str, Socket)`.
- `Ashes.Net.Tcp.send(socket)(text)` returns `Result(Str, Int)`.
- `Ashes.Net.Tcp.receive(socket)(maxBytes)` returns `Result(Str, Str)`.
- `Ashes.Net.Tcp.close(socket)` returns `Result(Str, Unit)`.

Networking rules:

- `connect` supports IPv4 address literals such as `"127.0.0.1"`.
- `connect` may also resolve hostnames through the runtime host-resolution path
    (for example `localhost` and other names available through system host
    configuration).
- Unresolvable hostnames return `Error(...)`.
- `send` attempts to write the full UTF-8 buffer before returning `Ok(bytesWritten)`.
- `receive` reads at most `maxBytes` bytes and returns `Ok("")` on EOF.
- Invalid UTF-8 received from the network returns `Error(...)`.
- `close` is explicit and deterministic; using a closed socket returns `Error(...)`.
- **Automatic cleanup**: `Socket` is a **resource type**. The compiler automatically
  releases unclosed sockets when their binding goes out of scope. If a socket is
  closed explicitly via `Ashes.Net.Tcp.close`, the automatic cleanup is skipped.
- **Use-after-close**: using a socket after it has been closed (via `send`, `receive`,
  or a second `close`) is a compile-time error.
- **Double-close**: calling `close` on an already-closed socket is a compile-time error.

Basic HTTP client APIs live under `Ashes.Http`:

- `Ashes.Http.get(url)` returns `Result(Str, Str)`.
- `Ashes.Http.post(url, body)` returns `Result(Str, Str)`.

Current HTTP rules:

- Only `http://` URLs are supported.
- `https://` returns `Error(...)`.
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

Mixed numeric operators are not allowed:

- `Int op Float` is a type error.
- `Float op Int` is a type error.

Unary negation is supported for integers:

- `-x` evaluates to the negated integer value of `x`.
- `-expr` binds tighter than `*` and `/`.

## 3.2 Comparison

Comparison operators evaluate to `Bool`.

| Operator | Types               | Description                        |
|----------|---------------------|------------------------------------|
| `>=`     | `Int >= Int`        | Greater than or equal              |
| `>=`     | `Float >= Float`    | Greater than or equal              |
| `<=`     | `Int <= Int`        | Less than or equal                 |
| `<=`     | `Float <= Float`    | Less than or equal                 |
| `==`     | `Int == Int`        | Equal (integers)                   |
| `==`     | `Float == Float`    | Equal (floats)                     |
| `==`     | `Str == Str`        | Equal (strings, byte-for-byte)     |
| `!=`     | `Int != Int`        | Not equal (integers)               |
| `!=`     | `Float != Float`    | Not equal (floats)                 |
| `!=`     | `Str != Str`        | Not equal (strings, byte-for-byte) |

Examples:

10 >= 5         // => true
3 <= 3          // => true
1 == 1          // => true
1 != 2          // => true
"hi" == "hi"    // => true
"hi" != "bye"   // => true

Both operands of `==` and `!=` must have the same type. Mixing `Int` and `Str` is a type error.

## 3.3 Cons

`::` constructs a new list by prepending a head value to a tail list.

Example:

1 :: [2,3]  // => [1,2,3]

## 3.4 Pipes

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
    |?> (fun (n) -> n + 1)

let bumpIfOk2 result =
    let? n = result
    in
    Ok(n + 1)

## 3.5 Precedence and Associativity

From lowest precedence to highest:

| Level | Operators                      | Associativity |
|-------|--------------------------------|---------------|
| 1     | `|>`, `|?>`, `|!>`             | left          |
| 2     | `>=`, `<=`, `==`, `!=`         | left          |
| 3     | `::`                           | right         |
| 4     | `+`, `-`                       | left          |
| 5     | `*`, `/`                       | left          |
| 6     | unary `-`                      | right         |
| 7     | function application           | left          |

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

Rules:

- The type name should begin with an uppercase letter by convention.
- Each constructor is introduced by `|`.
- Constructors may have zero or more payload parameters in parentheses.
- Type declarations appear before the expression body of the program.

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

## 5.1 Result Binding

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

---

# 6. Recursive Bindings

Recursive bindings must be declared with `rec`.

Syntax:

let rec name = value
in body

Example:

let rec loop =
    fun (i) ->
        if i >= 10
        then i
        else loop(i + 1)
in Ashes.IO.print(loop(0))

Without `rec`, a binding cannot reference itself.

`let rec` bindings are **monomorphic**: during inference, the recursive name is bound to a
single monotype (a non-generalized type, which may still contain type variables). This
means the function may not be used at multiple distinct types within its own definition
(no polymorphic recursion). Non-recursive `let` bindings are generalized and may be used
polymorphically.

Self-recursive calls in tail position are guaranteed not to consume additional stack
frames. Tail-position arguments are still evaluated strictly before the recursive jump is
performed.

---

# 7. Functions

Anonymous functions are declared using `fun`.

Syntax:

fun (param1, param2, ...) -> expr

Example:

let add = fun (x, y) -> x + y
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

    let rec loop x y =
        if x >= 100000
        then y
        else loop (x + 1) (y + 1)
    in Ashes.IO.print (loop 0 0)

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

let unwrapOr =
    fun (opt) ->
        fun (def) ->
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

let rec sum =
    fun (lst) ->
        fun (acc) ->
            match lst with
                | [] -> acc
                | x :: rest -> sum(rest)(acc + x)
in Ashes.IO.print(sum([1, 2, 3])(1))

Default-value list utilities are written as regular user code, for example:

let rec lastOr =
    fun (xs) ->
        fun (default) ->
            let rec loop =
                fun (ys) ->
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

## 13.2 Ashes.IO Module

The built-in `Ashes.IO` module exports:

- `print(expr)` — prints the evaluated expression to standard output.
- `panic("message")` — prints the message and aborts with a non-zero exit code.
  `panic` has Never/Bottom behavior, so it typechecks in any expression context.
- `args` — a `List<String>` containing command-line arguments passed to the
  compiled program (excluding the executable path/name at `argv[0]`).
- `write("text")` — writes a string to standard output without adding a newline.
- `writeLine("text")` — writes a string to standard output and then writes `\n`.
- `readLine()` — reads one line from standard input and returns `Some(line)` or `None` on EOF.

Other built-in runtime modules are also always available through qualified access:

- `Ashes.File.readText(path)` — `Result(Str, Str)` UTF-8 file read.
- `Ashes.File.writeText(path, text)` — `Result(Str, Unit)` UTF-8 file write.
- `Ashes.File.exists(path)` — `Result(Str, Bool)` filesystem existence check.
- `Ashes.Net.Tcp.connect(host)(port)` — `Result(Str, Socket)` blocking TCP connect.
- `Ashes.Net.Tcp.send(socket)(text)` — `Result(Str, Int)` blocking TCP send.
- `Ashes.Net.Tcp.receive(socket)(maxBytes)` — `Result(Str, Str)` blocking TCP receive.
- `Ashes.Net.Tcp.close(socket)` — `Result(Str, Unit)` explicit socket close.
- `Ashes.Http.get(url)` — `Result(Str, Str)` blocking HTTP GET for plain `http://` URLs.
- `Ashes.Http.post(url, body)` — `Result(Str, Str)` blocking HTTP POST for plain `http://` URLs.

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

let describe =
    fun (result) ->
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
- `Ashes.Maybe` — helper functions for the built-in `Maybe(T)` runtime type.
- `Ashes.Result` — helper functions for the built-in `Result(E, A)` runtime type.
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
`future/FUTURE_FEATURES.md`.

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

    let id = fun (x) -> x
    in
    let _a = id(1)
    in
    let _b = id("x")
    in Ashes.IO.print("ok")

`id` is inferred once, generalized, then instantiated separately for integer and string uses.

## 14.2 Recursion and Polymorphism

`let rec` bindings are monomorphic during inference. The recursive name is checked
using a single monotype inside its own definition, so polymorphic recursion is not inferred.

## 14.3 Infinite Types (Occurs Check)

Inference rejects infinite/recursive types via an occurs check. Self-application patterns
such as `x(x)` are invalid, because they would require a type variable to contain itself.

## 14.4 What to Expect

Common patterns that typecheck:

- polymorphic helper functions (for example `id`, `const`, `map`) defined with non-recursive `let`
- the same helper used at different concrete types in different call sites

Common patterns that do not typecheck:

- polymorphic recursion in a `let rec` definition
- self-application that would create an infinite type
- list elements differ in type
- match branches differ in return type
- cons tail is not a list
- recursive binding lacks `rec`

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

When a resource is closed explicitly, the automatic cleanup for that
resource is skipped (no double close).

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

    let rec sum = fun (n) -> fun (acc) ->
        if n == 0 then acc
        else sum(n - 1)(acc + n)
    in sum(1000000)(0)

run in constant stack space, without risk of stack overflow.

## 18.4 Zero-Cost Abstraction Philosophy

Values are immutable and freely shared; the compiler handles ownership
and memory safely behind the scenes. Optimizations are never visible to
user code. The compiler is free to reorder, eliminate, or restructure
internal operations as long as the observable result is identical.

---

# 19. Async/Await

Ashes supports structured concurrency via `async` and `await` expressions.

## 19.1 Task Type

    Task(E, A)

`Task(E, A)` is a built-in parametric type representing an asynchronous
computation that may fail with error type `E` or succeed with value type `A`.

- `Task` is an **owned type** (like `String`, `List`, closures).
- `Task` values are **not resource types** — they do not have use-after-close
  or double-close restrictions.
- The compiler inserts `Drop` for `Task` at scope exit like any other owned type.

## 19.2 Async Expressions

`async <expr>` wraps an expression into a `Task(E, A)` value:

    let task = async
        let response = await Ashes.Http.get("http://example.com")
        in response
    in Ashes.Async.run(task)

- `async` is an expression, not a declaration modifier.
- `async <expr>` where `<expr> : A` produces `Task(E, A)`.
- The error type `E` is unified from any `await` sub-expressions.
- `async` blocks may be nested. Inner blocks create independent tasks.

## 19.3 Await Expressions

`await <expr>` unwraps a `Task(E, A)` inside an `async` block:

    async
        let a = await taskA
        in
            let b = await taskB
            in a + b

- `await <expr>` where `<expr> : Task(E, A)` produces `A`.
- `await` may only appear inside an `async` block. Using `await` outside
  `async` is a compile-time error (`ASH010`).
- Tasks are currently infallible: `Ashes.Async.run` always returns `Ok(value)`.
  Task failure/error propagation semantics are reserved for a future release.

## 19.4 Async Let (let!)

`let!` is sugar for `await` in a binding position:

    async
        let! response = Ashes.Http.get("http://example.com")
        in response

This desugars to:

    async
        let response = await Ashes.Http.get("http://example.com")
        in response

`let!` flattens binding chains — no additional nesting per await point.
Multiple `let!` bindings chain sequentially:

    async
        let! a = Ashes.Http.get("http://a.com")
        let! b = Ashes.Http.get("http://b.com")
        in a + b

Desugars to:

    async
        let a = await Ashes.Http.get("http://a.com")
        in
            let b = await Ashes.Http.get("http://b.com")
            in a + b

## 19.5 Type Inference Rules

- `async <expr>` where `<expr> : A` produces `Task(E, A)`.
  `E` is unified from any `await` sub-expression.
- `await <expr>` where `<expr> : Task(E, A)` produces `A`.
- Functions returning `Task` are regular functions; there is no `async fun`
  modifier.

## 19.6 Result Interop

`Task(E, A)` and `Result(E, A)` share the same error-propagation model:

- `Ashes.Async.fromResult(result)` — wraps a `Result(E, A)` into a
  `Task(E, A)` that completes immediately.
- `Ashes.Async.run(task)` — runs a task to completion and returns
  `Result(E, A)`.

## 19.7 Ashes.Async Module

| Function | Type |
|----------|------|
| `Ashes.Async.run(task)` | `Task(E, A) -> Result(E, A)` |
| `Ashes.Async.fromResult(r)` | `Result(E, A) -> Task(E, A)` |
| `Ashes.Async.sleep(ms)` | `Int -> Task(Str, Int)` |
| `Ashes.Async.all(tasks)` | `List(Task(E, A)) -> Task(E, List(A))` |
| `Ashes.Async.race(tasks)` | `List(Task(E, A)) -> Task(E, A)` |

### 19.7.1 Ashes.Async.sleep

`Ashes.Async.sleep(ms)` creates a task that suspends for the given
number of milliseconds, then completes with `0`:

    async
        let _ = await Ashes.Async.sleep(100)
        in 42

- The argument is an `Int` representing milliseconds.
- The returned task has type `Task(Str, Int)`.
- On completion, the result is `0` (unit placeholder).
- `sleep` must be used inside an `async` block via `await`.
- On Linux, `sleep` uses the `nanosleep` syscall.
- On Windows, `sleep` uses the `Sleep` kernel32 function.

### 19.7.2 Ashes.Async.all

`Ashes.Async.all(tasks)` takes a list of tasks and runs them all,
collecting results into a list in the original order:

    async
        let results = await Ashes.Async.all([async 1, async 2, async 3])
        in results

- The argument is a `List(Task(E, A))`.
- The returned task has type `Task(E, List(A))`.
- All tasks are run sequentially (left to right).
- Results are collected in the same order as the input list.
- An empty input list produces an empty result list.

### 19.7.3 Ashes.Async.race

`Ashes.Async.race(tasks)` takes a list of tasks and returns the result
of the first task:

    async
        let result = await Ashes.Async.race([async 42, async 99])
        in result

- The argument is a `List(Task(E, A))`.
- The returned task has type `Task(E, A)`.
- Only the first task in the list is run.
- An empty input list produces `0` (unit placeholder).

## 19.8 Diagnostics

- `ASH010` — `await` used outside an `async` block.
- `ASH011` — `async` block has incompatible error types across await points.

---

# 20. Unsupported (Future)

See `future/FUTURE_FEATURES.md` for the list of planned but not yet supported features.

Note: project-mode `import Foo` / `import Foo.Bar` lines are supported by the project system
(`ashes.json` + `PROJECT_SPEC.md`) and are resolved before expression parsing. Built-in
`import Ashes.IO` is handled by the compiler directly.
