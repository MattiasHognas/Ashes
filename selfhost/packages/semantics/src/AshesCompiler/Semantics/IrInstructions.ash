// Defines the complete flat instruction vocabulary consumed by the native backend.
//
// Invariants:
// - Temps and locals are function-scoped integer indices; labels are function-local control targets.
// - Operand order matches stage 0 so later text/parity formats can remain structural.
// - Runtime-managed and reuse flags are explicit compiler facts, never inferred again by codegen.
// - Coroutine save/restore records identify frame byte offsets separately from temp indices.

import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.Types
export (
    type IrTemp,
    type IrLocal,
    type EnvironmentDirectoryKind(..),
    type CopyOutPurpose(..),
    type ListHeadCopyKind(..),
    type IrFrameSave(..),
    type IrFrameRestore(..),
    type IrSwitchCase(..),
    type IrInstructionKind(..),
    type IrInstruction(..),
    value copyOutBigIntSize,
)

type alias IrTemp = Int

type alias IrLocal = Int

type EnvironmentDirectoryKind =
    | CurrentDirectory
    | ExecutableDirectory
    | TemporaryDirectory
    | CacheDirectory
    deriving {Eq, Show}

type CopyOutPurpose =
    | RcNormalization
    | ArenaScopeBoundary
    | ArenaCallBoundary
    | ArenaTcoCompaction
    | IndependentClone
    deriving {Eq, Show}

type ListHeadCopyKind =
    | InlineListHead
    | StringListHead
    | InnerListHead
    deriving {Eq, Show}

type IrFrameSave =
    | slotOffset: Int
    | sourceTemp: IrTemp
    deriving {Eq, Show}

type IrFrameRestore =
    | slotOffset: Int
    | targetTemp: IrTemp
    deriving {Eq, Show}

type IrSwitchCase =
    | tag: Int
    | label: Str
    deriving {Eq, Show}

let copyOutBigIntSize = -2

type IrInstructionKind =
    // Constants, locals, environments, arithmetic, and comparisons.
    | LoadConstInt(IrTemp, Int)
    | LoadConstFloat(IrTemp, Float)
    | LoadConstBool(IrTemp, Bool)
    | LoadConstStr(IrTemp, Str)
    | LoadProgramArgs(IrTemp)
    | LoadLocal(IrTemp, IrLocal)
    | StoreLocal(IrLocal, IrTemp)
    | LoadEnv(IrTemp, Int)
    | StoreMemOffset(IrTemp, Int, IrTemp)
    | LoadMemOffset(IrTemp, IrTemp, Int)
    | AddInt(IrTemp, IrTemp, IrTemp)
    | SubInt(IrTemp, IrTemp, IrTemp)
    | MulInt(IrTemp, IrTemp, IrTemp)
    | DivInt(IrTemp, IrTemp, IrTemp)
    | DivUInt(IrTemp, IrTemp, IrTemp)
    | AndInt(IrTemp, IrTemp, IrTemp)
    | OrInt(IrTemp, IrTemp, IrTemp)
    | XorInt(IrTemp, IrTemp, IrTemp)
    | ShlInt(IrTemp, IrTemp, IrTemp)
    | ShrInt(IrTemp, IrTemp, IrTemp)
    | AddFloat(IrTemp, IrTemp, IrTemp)
    | SubFloat(IrTemp, IrTemp, IrTemp)
    | MulFloat(IrTemp, IrTemp, IrTemp)
    | DivFloat(IrTemp, IrTemp, IrTemp)
    | CmpIntGt(IrTemp, IrTemp, IrTemp)
    | CmpIntGe(IrTemp, IrTemp, IrTemp)
    | CmpIntLt(IrTemp, IrTemp, IrTemp)
    | CmpIntLe(IrTemp, IrTemp, IrTemp)
    | CmpUIntGt(IrTemp, IrTemp, IrTemp)
    | CmpUIntGe(IrTemp, IrTemp, IrTemp)
    | CmpUIntLt(IrTemp, IrTemp, IrTemp)
    | CmpUIntLe(IrTemp, IrTemp, IrTemp)
    | CmpIntEq(IrTemp, IrTemp, IrTemp)
    | CmpIntNe(IrTemp, IrTemp, IrTemp)
    | CmpFloatGt(IrTemp, IrTemp, IrTemp)
    | CmpFloatGe(IrTemp, IrTemp, IrTemp)
    | CmpFloatLt(IrTemp, IrTemp, IrTemp)
    | CmpFloatLe(IrTemp, IrTemp, IrTemp)
    | CmpFloatEq(IrTemp, IrTemp, IrTemp)
    | CmpFloatNe(IrTemp, IrTemp, IrTemp)
    | IntToFloat(IrTemp, IrTemp)
    | FloatToInt(IrTemp, IrTemp)
    | FloatUnaryIntrinsic(IrTemp, IrTemp, Str)
    | CallLibm(IrTemp, Str, List(IrTemp))
    // Big integers, strings, and regex.
    | BigIntFromInt(IrTemp, IrTemp, Bool)
    | BigIntToString(IrTemp, IrTemp, Bool)
    | BigIntToInt(IrTemp, IrTemp, Bool)
    | BigIntFromString(IrTemp, IrTemp, Bool)
    | BigIntBinary(IrTemp, IrTemp, IrTemp, Str, Bool)
    | BigIntCompare(IrTemp, IrTemp, IrTemp)
    | CmpStrEq(IrTemp, IrTemp, IrTemp)
    | CmpStrNe(IrTemp, IrTemp, IrTemp)
    | ConcatStr(IrTemp, IrTemp, IrTemp, Bool)
    | ConcatStrTip(IrTemp, IrTemp, IrTemp, IrLocal, IrLocal, Bool)
    | RegexCompile(IrTemp, IrTemp)
    | RegexCompileError(IrTemp, IrTemp)
    | RegexFind(IrTemp, IrTemp, IrTemp, IrTemp)
    | RegexCaptures(IrTemp, IrTemp, IrTemp, IrTemp)
    | RegexSubstitute(IrTemp, IrTemp, IrTemp, IrTemp)
    // Closures, calls, allocation, and aggregate access.
    | MakeClosure(IrTemp, Str, IrTemp, Int, Bool, Bool, Bool)
    | MakeClosureStack(IrTemp, Str, IrTemp, Int, Bool, Bool)
    | LoadFuncAddr(IrTemp, Str)
    | CallClosure(IrTemp, IrTemp, IrTemp, IrTemp)
    | CallKnown(IrTemp, Str, IrTemp, IrTemp, IrTemp, Bool)
    | LoadArgumentOwnership(IrTemp)
    | Alloc(IrTemp, Int, Bool)
    | AllocStack(IrTemp, Int)
    | AllocAdt(IrTemp, Int, Int, Bool)
    | AllocAdtStack(IrTemp, Int, Int)
    | AllocAdtToSpace(IrTemp, Int, Int)
    | DropReuse(IrTemp, IrTemp, Int, Bool)
    | AllocReusing(IrTemp, Int, Int, IrTemp, Bool, Bool)
    | SetAdtField(IrTemp, Int, IrTemp)
    | SaveStackPointer(IrLocal)
    | RestoreStackPointer(IrLocal)
    | GetAdtTag(IrTemp, IrTemp)
    | GetAdtField(IrTemp, IrTemp, Int)
    // Console, file, environment, text, network, bytes, and process intrinsics.
    | PrintInt(IrTemp)
    | PrintStr(IrTemp)
    | PrintBool(IrTemp)
    | WriteStr(IrTemp)
    | WriteErrorStr(IrTemp, Bool)
    | ExitProcess(IrTemp)
    | WriteBufferedStr(IrTemp, Bool)
    | FlushStdout
    | ReadLine(IrTemp)
    | ReadExact(IrTemp, IrTemp)
    | ConsoleEnableRaw(IrTemp)
    | ConsoleRestore(IrTemp)
    | ConsolePoll(IrTemp, IrTemp)
    | MonotonicMillis(IrTemp)
    | TextByteLength(IrTemp, IrTemp)
    | FileReadText(IrTemp, IrTemp)
    | FileReadAllBytes(IrTemp, IrTemp)
    | FileMmap(IrTemp, IrTemp)
    | FileWriteText(IrTemp, IrTemp, IrTemp)
    | FileExists(IrTemp, IrTemp)
    | FileReplace(IrTemp, IrTemp, IrTemp)
    | FileMakeExecutable(IrTemp, IrTemp)
    | DirectoryEntries(IrTemp, IrTemp)
    | DirectoryCreateAll(IrTemp, IrTemp)
    | DirectoryRemoveTree(IrTemp, IrTemp)
    | EnvironmentDirectory(IrTemp, EnvironmentDirectoryKind)
    | EnvironmentGet(IrTemp, IrTemp)
    | FileOpen(IrTemp, IrTemp)
    | FileReadChunk(IrTemp, IrTemp, IrTemp)
    | FileReadLine(IrTemp, IrTemp)
    | FileClose(IrTemp, IrTemp)
    | TextUncons(IrTemp, IrTemp, Bool)
    | TextUnconsText(IrTemp, IrTemp, Bool)
    | RuneToText(IrTemp, IrTemp, Bool)
    | RuneFromInt(IrTemp, IrTemp, Bool)
    | TextParseInt(IrTemp, IrTemp, Bool)
    | TextParseFloat(IrTemp, IrTemp, Bool)
    | TextFromInt(IrTemp, IrTemp, Bool)
    | TextFromFloat(IrTemp, IrTemp, Bool)
    | TextFormatFloat(IrTemp, IrTemp, IrTemp, Bool)
    | TextToHex(IrTemp, IrTemp, Bool)
    | TextAsciiCase(IrTemp, IrTemp, Bool, Bool)
    | HttpGet(IrTemp, IrTemp)
    | HttpPost(IrTemp, IrTemp, IrTemp)
    | NetTcpConnect(IrTemp, IrTemp, IrTemp)
    | NetTcpSend(IrTemp, IrTemp, IrTemp)
    | NetTcpReceive(IrTemp, IrTemp, IrTemp)
    | NetTcpClose(IrTemp, IrTemp)
    | NetTcpListen(IrTemp, IrTemp)
    | NetTcpAccept(IrTemp, IrTemp)
    | BytesEmpty(IrTemp, Bool)
    | BytesSingleton(IrTemp, IrTemp, Bool)
    | BytesLength(IrTemp, IrTemp)
    | BytesGet(IrTemp, IrTemp, IrTemp)
    | BytesIndexOf(IrTemp, IrTemp, IrTemp, IrTemp)
    | BytesCompare(IrTemp, IrTemp, IrTemp)
    | BytesScanHash(IrTemp, IrTemp, IrTemp, IrTemp)
    | BytesSubText(IrTemp, IrTemp, IrTemp, IrTemp, Bool)
    | BytesSubView(IrTemp, IrTemp, IrTemp, IrTemp)
    | BytesAppend(IrTemp, IrTemp, IrTemp, Bool)
    | BytesAppendByte(IrTemp, IrTemp, IrTemp, Bool)
    | BytesAllocate(IrTemp, IrTemp, Bool)
    | BytesCopyRange(IrTemp, IrTemp, IrTemp, IrTemp, IrTemp, IrTemp, Bool, Bool)
    | BytesSet(IrTemp, IrTemp, IrTemp, IrTemp, Bool, Bool)
    | BytesSetU16Le(IrTemp, IrTemp, IrTemp, IrTemp, Bool, Bool)
    | BytesSetU32Le(IrTemp, IrTemp, IrTemp, IrTemp, Bool, Bool)
    | BytesSetU64Le(IrTemp, IrTemp, IrTemp, IrTemp, Bool, Bool)
    | BytesFromList(IrTemp, IrTemp, Bool)
    | BytesHash(IrTemp, IrTemp)
    | BytesU16Le(IrTemp, IrTemp, Bool)
    | BytesU32Le(IrTemp, IrTemp, Bool)
    | BytesU64Le(IrTemp, IrTemp, Bool)
    | BytesGetU16Le(IrTemp, IrTemp, IrTemp)
    | BytesGetU32Le(IrTemp, IrTemp, IrTemp)
    | BytesGetU64Le(IrTemp, IrTemp, IrTemp)
    | FileWriteBytes(IrTemp, IrTemp, IrTemp)
    | SpawnProcess(IrTemp, IrTemp, IrTemp)
    | ProcessWriteStdin(IrTemp, IrTemp, IrTemp)
    | ProcessReadStdoutLine(IrTemp, IrTemp)
    | ProcessReadStderrLine(IrTemp, IrTemp)
    | ProcessWaitForExit(IrTemp, IrTemp)
    | ProcessKill(IrTemp, IrTemp)
    // Ownership, arena lifetime, graph normalization, and FFI boundaries.
    | CleanupResource(IrTemp, Str, Maybe(ExternalFunctionAbi))
    | RcDrop(IrTemp, Str, IrLocal, Bool, Bool, Maybe(Str))
    | RcDup(IrTemp, IrTemp, Bool, Bool)
    | RcIsUnique(IrTemp, IrTemp)
    | Borrow(IrTemp, IrTemp)
    | TcoResetPending(Int, List(IrTemp), List(IrLocal))
    | SaveArenaState(IrLocal, IrLocal, Bool)
    | RestoreArenaState(IrLocal, IrLocal, IrLocal, Bool)
    | ReclaimArenaChunks(IrLocal, IrLocal, Bool)
    | CopyOutArena(IrTemp, IrTemp, Int, Bool, CopyOutPurpose, Maybe(SemanticType))
    | CopyOutArenaToSpace(IrTemp, IrTemp, Int)
    | CopyFixedInto(IrTemp, IrTemp, Int)
    | CopyStringIntoOrFresh(IrTemp, IrTemp, IrTemp)
    | CopyFixedIntoOrFresh(IrTemp, IrTemp, IrTemp, Int)
    | CopyOutList(IrTemp, IrTemp, ListHeadCopyKind, Bool, CopyOutPurpose)
    | CopyOutClosure(IrTemp, IrTemp, Bool, CopyOutPurpose)
    | CopyOutTcoListCell(IrTemp, IrTemp, ListHeadCopyKind, CopyOutPurpose)
    | ToCString(IrTemp, IrTemp)
    | AllocFfiOut(IrTemp, ExternalAbiType)
    | LoadFfiOut(IrTemp, IrTemp, ExternalAbiType)
    | CopyFfiString(IrTemp, IrTemp, ExternalAbiType)
    | CopyFfiBytes(IrTemp, IrTemp, IrTemp)
    | CallExternal(IrTemp, Str, Maybe(Str), List(IrTemp), List(ExternalAbiType), ExternalAbiType)
    // Tasks, structured concurrency, parallelism, and transformed coroutine operations.
    | CreateTask(IrTemp, IrTemp, Int, Int, Maybe(Str), Bool)
    | CreateCompletedTask(IrTemp, IrTemp)
    | AwaitTask(IrTemp, IrTemp)
    | RunTask(IrTemp, IrTemp)
    | SpawnTask(IrTemp, IrTemp)
    | CreateTaskScope(IrTemp, Bool)
    | CreateScopedTask(IrTemp, IrTemp, IrTemp)
    | ForkScopedTask(IrTemp, IrTemp, IrTemp)
    | JoinScopedTask(IrTemp, IrTemp)
    | ParallelFork(IrTemp, IrTemp)
    | ParallelJoin(IrTemp, IrTemp)
    | ParallelCleanup(IrTemp)
    | LoadParallelWorkerOverride(IrTemp)
    | StoreParallelWorkerOverride(IrTemp)
    | ParallelQueueStart(IrTemp, IrTemp, IrTemp, IrTemp)
    | ParallelQueueAwait(IrTemp, IrTemp)
    | ParallelQueueCleanup(IrTemp)
    | Suspend(IrTemp, Int, IrTemp, List(IrFrameSave))
    | Resume(IrTemp, IrTemp, List(IrFrameRestore))
    | AsyncSleep(IrTemp, IrTemp)
    | CreateTcpConnectTask(IrTemp, IrTemp, IrTemp)
    | CreateTcpSendTask(IrTemp, IrTemp, IrTemp)
    | CreateTcpReceiveTask(IrTemp, IrTemp, IrTemp)
    | CreateTcpCloseTask(IrTemp, IrTemp)
    | CreateTcpListenTask(IrTemp, IrTemp)
    | CreateForkWorkersTask(IrTemp, IrTemp, IrTemp)
    | SetDrainTimeout(IrTemp, IrTemp)
    | RequestServerStop(IrTemp)
    | CreateTcpAcceptTask(IrTemp, IrTemp)
    | CreateHttpGetTask(IrTemp, IrTemp)
    | CreateHttpPostTask(IrTemp, IrTemp, IrTemp)
    | CreateTlsConnectTask(IrTemp, IrTemp, IrTemp)
    | CreateTlsHandshakeTask(IrTemp, IrTemp, IrTemp)
    | CreateTlsServerHandshakeTask(IrTemp, IrTemp, IrTemp, IrTemp)
    | CreateTlsSendTask(IrTemp, IrTemp, IrTemp)
    | CreateTlsReceiveTask(IrTemp, IrTemp, IrTemp)
    | CreateTlsCloseTask(IrTemp, IrTemp)
    | AsyncAll(IrTemp, IrTemp)
    | AsyncRace(IrTemp, IrTemp)
    // Capabilities and control flow.
    | PanicStr(IrTemp)
    | LoadCapabilityHandler(IrTemp, Int)
    | StoreCapabilityHandler(Int, IrTemp)
    | Label(Str)
    | Jump(Str)
    | JumpIfFalse(IrTemp, Str)
    | SwitchTag(IrTemp, List(IrSwitchCase), Str)
    | Return(IrTemp)

type IrInstruction =
    | instruction: IrInstructionKind
    | location: Maybe(IrSourceLocation)
