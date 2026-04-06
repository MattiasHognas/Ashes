# IR Reference

This document is the authoritative reference for the Ashes intermediate
representation (IR). The IR is a flat, register-based instruction set
defined in `Ashes.Semantics/Ir.cs`. The `Lowering` pass converts the
typed AST into an `IrProgram`, which the LLVM backend consumes.

------------------------------------------------------------------------

## Program Structure

### IrProgram

The root container for a compiled Ashes program:

| Field | Type | Description |
|-------|------|-------------|
| `EntryFunction` | `IrFunction` | Top-level expression (`_start_main`) |
| `Functions` | `List<IrFunction>` | Lifted lambdas and named functions |
| `StringLiterals` | `List<IrStringLiteral>` | All string constants with labels |
| `UsesPrintInt` | `bool` | Whether `PrintInt` is used |
| `UsesPrintStr` | `bool` | Whether `PrintStr` is used |
| `UsesPrintBool` | `bool` | Whether `PrintBool` is used |
| `UsesConcatStr` | `bool` | Whether `ConcatStr` is used |
| `UsesClosures` | `bool` | Whether closures are created |
| `UsesAsync` | `bool` | Whether async/await is used |

The `Uses*` flags allow the backend to omit unused runtime helpers.

### IrFunction

A single function with a flat instruction list:

| Field | Type | Description |
|-------|------|-------------|
| `Label` | `string` | Unique name (e.g., `_start_main`, `lambda_0`) |
| `Instructions` | `List<IrInst>` | Linear instruction sequence |
| `LocalCount` | `int` | Number of local variable stack slots |
| `TempCount` | `int` | Number of temporary registers |
| `HasEnvAndArgParams` | `bool` | `true` for lambdas (implicit env+arg at slots 0, 1) |
| `Coroutine` | `CoroutineInfo?` | Non-null for async coroutine functions |

### CoroutineInfo

Metadata for coroutine functions generated from `async` blocks:

| Field | Type | Description |
|-------|------|-------------|
| `StateCount` | `int` | Number of states (N await points = N+1 states) |
| `StateStructSize` | `int` | Total size of the task/state struct in bytes |
| `CaptureCount` | `int` | Number of captured environment variables |

The entry function has `HasEnvAndArgParams: false`. Lambda functions
have `true`, meaning slot 0 holds the closure environment pointer and
slot 1 holds the argument.

### IrStringLiteral

Maps a label to a string constant:

| Field | Type | Description |
|-------|------|-------------|
| `Label` | `string` | Reference name (e.g., `str_0`) |
| `Value` | `string` | The string content |

Referenced by `LoadConstStr`. The backend allocates these on the heap
as `[length:i64][bytes...]`.

------------------------------------------------------------------------

## Registers and Locals

Instructions use integer indices to address values:

- **Temporaries** (`Target`, `Source`, `Left`, `Right`) — virtual
  registers allocated per-function by `NewTemp()`.
- **Locals** (`Slot`) — stack slots allocated by `NewLocal()` for
  named bindings.

Each instruction that produces a value writes to a `Target` temporary.
Each instruction that consumes values reads from `Source`, `Left`,
`Right`, or named parameter temporaries.

------------------------------------------------------------------------

## Instruction Reference

### Constants

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `LoadConstInt` | `Target`, `Value: long` | Load integer literal |
| `LoadConstFloat` | `Target`, `Value: double` | Load floating-point literal |
| `LoadConstBool` | `Target`, `Value: bool` | Load boolean literal |
| `LoadConstStr` | `Target`, `StrLabel: string` | Load string literal by label |
| `LoadProgramArgs` | `Target` | Load command-line arguments list |

### Local Variables

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `LoadLocal` | `Target`, `Slot` | Load value from local stack slot |
| `StoreLocal` | `Slot`, `Source` | Store value into local stack slot |

### Environment and Memory

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `LoadEnv` | `Target`, `Index` | Load captured value from closure environment |
| `StoreMemOffset` | `BasePtr`, `OffsetBytes`, `Source` | Store value at `[base + offset]` |
| `LoadMemOffset` | `Target`, `BasePtr`, `OffsetBytes` | Load value from `[base + offset]` |
| `Alloc` | `Target`, `SizeBytes` | Heap-allocate `SizeBytes`; return pointer |

### Integer Arithmetic

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `AddInt` | `Target`, `Left`, `Right` | `Target = Left + Right` |
| `SubInt` | `Target`, `Left`, `Right` | `Target = Left - Right` |
| `MulInt` | `Target`, `Left`, `Right` | `Target = Left * Right` |
| `DivInt` | `Target`, `Left`, `Right` | `Target = Left / Right` |

### Float Arithmetic

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `AddFloat` | `Target`, `Left`, `Right` | `Target = Left + Right` |
| `SubFloat` | `Target`, `Left`, `Right` | `Target = Left - Right` |
| `MulFloat` | `Target`, `Left`, `Right` | `Target = Left * Right` |
| `DivFloat` | `Target`, `Left`, `Right` | `Target = Left / Right` |

### Integer Comparisons

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `CmpIntGe` | `Target`, `Left`, `Right` | `Target = (Left >= Right) ? 1 : 0` |
| `CmpIntLe` | `Target`, `Left`, `Right` | `Target = (Left <= Right) ? 1 : 0` |
| `CmpIntEq` | `Target`, `Left`, `Right` | `Target = (Left == Right) ? 1 : 0` |
| `CmpIntNe` | `Target`, `Left`, `Right` | `Target = (Left != Right) ? 1 : 0` |

### Float Comparisons

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `CmpFloatGe` | `Target`, `Left`, `Right` | `Target = (Left >= Right) ? 1 : 0` |
| `CmpFloatLe` | `Target`, `Left`, `Right` | `Target = (Left <= Right) ? 1 : 0` |
| `CmpFloatEq` | `Target`, `Left`, `Right` | `Target = (Left == Right) ? 1 : 0` |
| `CmpFloatNe` | `Target`, `Left`, `Right` | `Target = (Left != Right) ? 1 : 0` |

### String Comparisons

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `CmpStrEq` | `Target`, `Left`, `Right` | `Target = (Left == Right) ? 1 : 0` |
| `CmpStrNe` | `Target`, `Left`, `Right` | `Target = (Left != Right) ? 1 : 0` |

### String Operations

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `ConcatStr` | `Target`, `Left`, `Right` | `Target = Left ++ Right` (string concatenation) |

### Closures

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `MakeClosure` | `Target`, `FuncLabel`, `EnvPtrTemp` | Allocate 16-byte closure: `[code_ptr, env_ptr]` |
| `CallClosure` | `Target`, `ClosureTemp`, `ArgTemp` | Call closure: `Target = code(env, arg)` |

A closure is a 16-byte heap cell containing a function pointer and an
environment pointer. `MakeClosure` stores the function address (resolved
from `FuncLabel`) and the environment pointer. `CallClosure` loads both
pointers, then calls the function with the environment and argument.

### Algebraic Data Types (ADTs)

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `AllocAdt` | `Target`, `Tag`, `FieldCount` | Allocate ADT: `[tag:i64, field0..fieldN:i64]` |
| `SetAdtField` | `Ptr`, `FieldIndex`, `Source` | `*(Ptr + 8 + Index*8) = Source` |
| `GetAdtTag` | `Target`, `Ptr` | `Target = *(Ptr + 0)` |
| `GetAdtField` | `Target`, `Ptr`, `FieldIndex` | `Target = *(Ptr + 8 + Index*8)` |

ADT values are heap-allocated cells. The first 8 bytes hold an integer
tag identifying the variant. Each field occupies 8 bytes. Total size is
`(1 + FieldCount) * 8` bytes.

### Console I/O

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `PrintInt` | `Source` | Print integer with newline to stdout |
| `PrintStr` | `Source` | Print string with newline to stdout |
| `PrintBool` | `Source` | Print boolean with newline to stdout |
| `WriteStr` | `Source` | Write string to stdout (no newline) |
| `ReadLine` | `Target` | Read line from stdin → `Maybe<String>` |
| `PanicStr` | `Source` | Print error message and terminate |

`ReadLine` returns a `Maybe<String>` ADT: `Some(line)` on success,
`None` on EOF.

### File I/O

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `FileReadText` | `Target`, `PathTemp` | Read file → `Result<String>` |
| `FileWriteText` | `Target`, `PathTemp`, `TextTemp` | Write file → `Result<Unit>` |
| `FileExists` | `Target`, `PathTemp` | Check existence → `Result<Bool>` |

All file operations return `Result` ADTs: `Ok(value)` on success,
`Error(message)` on failure.

### HTTP

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `HttpGet` | `Target`, `UrlTemp` | HTTP GET → `Result<String>` |
| `HttpPost` | `Target`, `UrlTemp`, `BodyTemp` | HTTP POST → `Result<String>` |

### TCP Networking

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `NetTcpConnect` | `Target`, `HostTemp`, `PortTemp` | Connect → `Result<Socket>` |
| `NetTcpSend` | `Target`, `SocketTemp`, `TextTemp` | Send data → `Result<Unit>` |
| `NetTcpReceive` | `Target`, `SocketTemp`, `MaxBytesTemp` | Receive → `Result<String>` |
| `NetTcpClose` | `Target`, `SocketTemp` | Close socket → `Result<Unit>` |

### Control Flow

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `Label` | `Name` | Define a jump target |
| `Jump` | `Target` | Unconditional jump to label |
| `JumpIfFalse` | `CondTemp`, `Target` | Jump to label if `CondTemp == 0` |
| `Return` | `Source` | Return value from function |

### Async / Task

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `CreateTask` | `Target`, `ClosureTemp`, `StateStructSize`, `CaptureCount` | Allocate task/state struct from closure |
| `CreateCompletedTask` | `Target`, `ResultTemp` | Allocate pre-completed task (state = -1) |
| `AwaitTask` | `Target`, `TaskTemp` | Await a sub-task inside a coroutine |
| `RunTask` | `Target`, `TaskTemp` | Synchronously drive a task to completion |
| `Suspend` | `StateStructTemp`, `NextState`, `AwaitedTaskTemp`, `SaveVars` | State machine suspend point |
| `Resume` | `StateStructTemp`, `ResultTemp`, `RestoreVars` | State machine resume point |

`AwaitTask` appears in the IR before the state machine transform. The transform
replaces each `AwaitTask` with a `Suspend`/`Resume` pair that saves and restores
live temps and locals across the await point.

**Task/state struct layout** (`TaskStructLayout`):

| Offset | Field | Description |
|--------|-------|-------------|
| 0 | `StateIndex` | Current state number (-1 = completed) |
| 8 | `CoroutineFn` | Pointer to coroutine function |
| 16 | `ResultSlot` | Result value / awaited task result |
| 24 | `AwaitedTask` | Pointer to sub-task being awaited |
| 32+ | Captures | Captured environment variables |
| 32+N*8+ | Live vars | Live variable slots across await points |

------------------------------------------------------------------------

## Lowering Overview

The `Lowering` class transforms the typed AST into IR:

1. **`Lowering.Lower(Expr)`** is the entry point. It walks the expression
   tree recursively, appending instructions to a flat list.

2. Each `LowerExpr()` call returns `(int Temp, TypeRef Type)` — the
   temporary holding the result and its inferred type.

3. **Lambdas** are lifted into separate `IrFunction` entries. Free
   variables are captured into a heap-allocated environment via
   `Alloc` + `StoreMemOffset`, then accessed inside the lambda body
   via `LoadEnv`.

4. **Pattern matching** generates a series of `GetAdtTag` checks,
   `JumpIfFalse` branches, and `GetAdtField` extractions, with labels
   for each arm and a join point after the match.

5. **Let bindings** allocate a local slot (`StoreLocal`) and make it
   available in the body scope (`LoadLocal`).

------------------------------------------------------------------------

## Backend Consumption

The LLVM backend (`LlvmCodegen`) processes each `IrFunction`:

1. Pre-creates LLVM `BasicBlock` entries for every `Label` instruction.
2. Iterates through instructions, calling `EmitInstruction()` which
   pattern-matches on the `IrInst` variant and emits corresponding
   LLVM IR builder calls.
3. Temps and locals are mapped to LLVM `alloca` stack slots.
4. The `Uses*` flags on `IrProgram` control which runtime helpers
   (print routines, string concatenation, etc.) are included.

------------------------------------------------------------------------

## Memory Layout Summary

| Structure | Layout | Size |
|-----------|--------|------|
| String | `[length:i64][bytes...]` | 8 + length |
| Closure | `[code_ptr:i64][env_ptr:i64]` | 16 |
| ADT | `[tag:i64][field0:i64]...[fieldN:i64]` | (1 + N) × 8 |
| Environment | `[capture0:i64][capture1:i64]...` | N × 8 |

All heap allocations come from a 4 MB static arena with a bump-pointer
allocator. There is no garbage collection or deallocation.
