# IR Reference

This document is the authoritative reference for the Ashes intermediate
representation (IR). The IR is a flat, register-based instruction set
defined in `Ashes.Semantics/Ir.cs`. The `Lowering` pass converts the
typed AST into an `IrProgram`, which the LLVM backend consumes.

---

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
| `CapabilityHandlerGlobals` | `int` | Number of declared capabilities (one handler-evidence global each) |

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

Referenced by `LoadConstStr`. The backend emits these as read-only globals
using the ordinary String payload layout with the view bit set.

---

## Registers and Locals

Instructions use integer indices to address values:

- **Temporaries** (`Target`, `Source`, `Left`, `Right`) — virtual
  registers allocated per-function by `NewTemp()`.
- **Locals** (`Slot`) — stack slots allocated by `NewLocal()` for
  named bindings.

Each instruction that produces a value writes to a `Target` temporary.
Each instruction that consumes values reads from `Source`, `Left`,
`Right`, or named parameter temporaries.

---

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
| `Alloc` | `Target`, `SizeBytes`, `RuntimeManaged` | Allocate a raw payload in the scoped arena or, when runtime-managed, behind an RC header |
| `SaveArenaState` | cursor/end slots | Record a scoped-region watermark |
| `RestoreArenaState` | cursor/end/pre-restore slots | Reset to a saved watermark |
| `ReclaimArenaChunks` | saved/pre-restore end slots | Return abandoned region chunks |

### Ownership and Lifetime

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `Borrow` | `Target`, `SourceTemp` | Create a non-owning compiler-tracked alias |
| `RcDup` | `Target`, `SourceTemp`, `RuntimeManaged` | Split ownership; increments the count for an RC value |
| `RcDrop` | `SourceTemp`, `TypeName`, `OwnerSlot`, `RuntimeManaged` | End one ordinary ownership path; runtime-managed forms perform type-directed RC release |
| `RcIsUnique` | `Target`, `SourceTemp` | Test whether an RC value has count 1 |
| `CleanupResource` | `SourceTemp`, `TypeName` | Deterministically close/reap a language resource; distinct from ordinary RC |

`PerceusLifetimePlacement` consumes the `OwnerSlot` provenance on lexical
anchors and places drops after last use or at dead branch entry. Constructor,
match, closure, and TCO lowering emit additional shape-aware ownership
operations. `RuntimeManaged: false` marks a compiler fact used by a scoped or
specialized region; it is not an instruction to read an RC header.

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
| `MakeClosure` | `Target`, `FuncLabel`, `EnvPtrTemp`, `EnvSizeBytes`, ownership flags | Allocate a closure payload, optionally behind an RC header |
| `CallClosure` | `Target`, `ClosureTemp`, `ArgTemp`, `RuntimeManagedArgumentFlagTemp` | Call closure with an optional retained-RC argument ownership flag |

A closure payload is 32 bytes:
`[code, env, packed_env_size_and_ownership, dropper]`. The packed word uses bit 63
for runtime-managed result ownership, bit 62 for RC-argument adoption, and the
low 62 bits for the environment size.
The dropper releases moved resources or RC captures. Supported captured
ordinary graphs also have code-label metadata for normalizing the complete
environment when a closure crosses into RC ownership. `CallClosure` loads the
code and environment pointers and calls `code(env, arg, owns_arg)`. A normalizing
direct-parameter entry adopts a transferred RC root when `owns_arg` is set and
otherwise performs the defensive arena-to-RC graph copy. The caller retains a
non-fresh root before transfer; fresh owned results can transfer their existing
reference. Curried parameters captured in closure environments cannot consume
this direct-argument flag.

### Algebraic Data Types (ADTs)

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `AllocAdt` | `Target`, `Tag`, `FieldCount`, `RuntimeManaged` | Allocate ADT payload `[tag, fields...]`, optionally behind an RC header |
| `DropReuse` | `Target`, `SourceTemp`, `FieldCount`, `RuntimeManaged` | Consume a dead cell into a compatible reuse token, or return null after decrementing a shared RC cell |
| `AllocReusing` | `Target`, `Tag`, `FieldCount`, `TokenTemp`, `RuntimeManaged` | Overwrite a compatible token; runtime null falls back to fresh RC allocation |
| `SetAdtField` | `Ptr`, `FieldIndex`, `Source` | `*(Ptr + 8 + Index*8) = Source` |
| `GetAdtTag` | `Target`, `Ptr` | `Target = *(Ptr + 0)` |
| `GetAdtField` | `Target`, `Ptr`, `FieldIndex` | `Target = *(Ptr + 8 + Index*8)` |

ADT values are heap-allocated cells. The first 8 bytes hold an integer
tag identifying the variant. Each field occupies 8 bytes. Total size is
`(1 + FieldCount) * 8` bytes.

### Graph Normalization and Region Copies

`CopyOutArena`, `CopyOutList`, `CopyOutClosure`, and
`CopyOutTcoListCell` all carry a required `CopyOutPurpose`:

| Purpose | Contract |
|---|---|
| `RcNormalization` | Construct an independently owned RC graph |
| `ArenaScopeBoundary` | Preserve scheduler/capability state across a scope reset |
| `ArenaCallBoundary` | Preserve scheduler/capability state across a call reset |
| `ArenaTcoCompaction` | Preserve live state at a region-managed TCO edge |
| `IndependentClone` | Explicit deep copy, worker publication, or reuse defense |

`AllocAdtToSpace` and `CopyOutArenaToSpace` are separate instructions for
the persistent `Map`/`HashMap` specialization and do not represent general
ordinary-value lifetime.

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

### Text Parsing

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `TextUncons` | `Target`, `TextTemp` | Split front scalar → `Maybe((Str, Str))` |
| `TextParseInt` | `Target`, `TextTemp` | Parse decimal integer → `Result<Int>` |
| `TextParseFloat` | `Target`, `TextTemp` | Parse decimal float → `Result<Float>` |

These instructions return the existing `Maybe` and `Result` ADTs.

### Text Formatting

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `TextFromInt` | `Target`, `ValueTemp` | Format integer → `Str` |
| `TextFromFloat` | `Target`, `ValueTemp` | Format finite float → `Str` |
| `TextToHex` | `Target`, `ValueTemp` | Format integer as hexadecimal → `Str` |

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

### Capabilities

Handler evidence for capabilities: one module global per declared capability
(`__ashes_capability_handler_<i>`, created when `IrProgram.CapabilityHandlerGlobals > 0`) holds a pointer
to the innermost installed handler frame for that capability, 0 when none. See
[Architecture](architecture.md) for the frame layout and perform/handle sequences.

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `LoadCapabilityHandler` | `Target`, `CapabilityIndex` | Load the capability's current handler frame pointer |
| `StoreCapabilityHandler` | `CapabilityIndex`, `Source` | Store a handler frame pointer into the capability global |

### Async / Task

| Instruction | Fields | Description |
|-------------|--------|-------------|
| `CreateTask` | `Target`, `ClosureTemp`, `StateStructSize`, `CaptureCount` | Allocate task/state struct from closure |
| `CreateCompletedTask` | `Target`, `ResultTemp` | Allocate pre-completed task (state = -1) |
| `AwaitTask` | `Target`, `TaskTemp` | Await a sub-task inside a coroutine |
| `RunTask` | `Target`, `TaskTemp` | Synchronously drive a task to completion |
| `AsyncSleep` | `Target`, `MillisecondsTemp` | Create a sleep task (state = -2) |
| `Suspend` | `StateStructTemp`, `NextState`, `AwaitedTaskTemp`, `SaveVars` | State machine suspend point |
| `Resume` | `StateStructTemp`, `ResultTemp`, `RestoreVars` | State machine resume point |

`AwaitTask` appears in the IR before the state machine transform. The transform
replaces each `AwaitTask` with a `Suspend`/`Resume` pair that saves and restores
live temps and locals across the await point.

**Task/state struct layout** (`TaskStructLayout`):

| Offset | Field | Description |
|--------|-------|-------------|
| 0-40 | core | State, coroutine, result, awaited task, task link, sleep duration |
| 48-88 | leaf wait | Two I/O arguments, wait kind/handle, and two wait scratch slots |
| 96 | `FrameSizeBytes` | Full frame size including captures and live slots |
| 104-112 | private arena | Detached root cursor and end |
| 120-136 | scheduler links | Ready-next, waiter, and arena owner |
| 144 | `LoopResetOk` | Whether an async TCO restart may reset its region |
| 152+ | captures/live vars | Captures followed by variables live across suspension |

---

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

---

## Backend Consumption

The LLVM backend (`LlvmCodegen`) processes each `IrFunction`:

1. Pre-creates LLVM `BasicBlock` entries for every `Label` instruction.
2. Iterates through instructions, calling `EmitInstruction()` which
   pattern-matches on the `IrInst` variant and emits corresponding
   LLVM IR builder calls.
3. Temps and locals are mapped to LLVM `alloca` stack slots.
4. The `Uses*` flags on `IrProgram` control which runtime helpers
   (print routines, string concatenation, etc.) are included.

---

## Memory Layout Summary

| Structure | Payload addressed by value pointer |
|-----------|--------------------------------------|
| String / Bytes | `[length_and_view_flag:i64][bytes...]` |
| BigInt | `[sign_and_limb_count:i64][limbs...]` |
| List cons | `[head:i64][tail:i64]`; nil is zero |
| Closure | `[code:i64][env:i64][packed_env_size_and_ownership:i64][dropper:i64]` |
| ADT / record | `[tag:i64][field0:i64]...[fieldN:i64]` |
| Tuple / environment | `[word0:i64][word1:i64]...` |

Runtime-managed values have
`[reference_count:i64][allocation_size:i64]` immediately before the
payload. Small RC cells use a dense per-thread region plus exact-size free-list
reuse; large cells use direct OS allocation. Scoped arena instructions remain
for proven scratch and explicit scheduler/specialized regions. See the
[architecture memory model](architecture.md#memory-model) for ownership and
boundary invariants.
