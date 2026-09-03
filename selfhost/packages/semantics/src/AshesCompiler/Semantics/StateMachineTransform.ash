// Transforms linear async coroutine IR into a state machine with suspend, resume, and state dispatch.
//
// Invariants:
// - Await points split coroutine instructions into numbered states.
// - Live temps and locals across suspend points are saved to and restored from the task state struct.
// - State 0 runs from entry; state indices >= 1 enter through resume prologues.
// - Structured parallelism fork/join groups must not span across await points.
// - Negative state codes indicate terminal or scheduler-handled states.

import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
export (
    type StateMachineResult(..),
    value transformStateMachine,
    value findAwaitPositions,
    value computeLiveTempsAcrossAwaits,
    value computeLiveLocalsAcrossAwaits,
    value assignStateStructSlots,
    value computeMaxBodyTemp,
    value getDefinedTemps,
    value getUsedTemps,
    value getAllTemps,
    value getWrittenLocalSlots,
    value getReadLocalSlots,
)

type StateMachineResult =
    | instructions: List(IrInstruction)
    | stateCount: Int
    | stateStructSize: Int
    | maxTemp: Int
    | savedTempOffsets: List((IrTemp, Int))
    | savedLocalOffsets: List((IrLocal, Int))

let taskStateIndexOffset = 0

let taskResultSlotOffset = 16

let taskAwaitedTaskOffset = 24

let taskHeaderSize = 160

let recursive listLength list =
    match list with
        | [] -> 0
        | _ :: tail -> 1 + listLength(tail)

let recursive listContains item list =
    match list with
        | [] -> false
        | head :: tail ->
            if head == item
            then true
            else listContains(item)(tail)

let recursive listGet (index: Int) list =
    match list with
        | [] -> None
        | head :: tail ->
            if index == 0
            then Some(head)
            else listGet(index - 1)(tail)

let recursive insertUniqueInt item list =
    match list with
        | [] -> [item]
        | head :: tail ->
            if item < head
            then item :: list
            else
                if item == head
                then list
                else head :: insertUniqueInt(item)(tail)

let recursive sortUniqueInts list =
    match list with
        | [] -> []
        | head :: tail -> insertUniqueInt(head)(sortUniqueInts(tail))

let recursive unionUniqueInts left right = sortUniqueInts(append(left)(right))

let recursive intersectUniqueInts left right =
    match left with
        | [] -> []
        | head :: tail ->
            if listContains(head)(right)
            then head :: intersectUniqueInts(tail)(right)
            else intersectUniqueInts(tail)(right)

let recursive removeInt target list =
    match list with
        | [] -> []
        | head :: tail ->
            if head == target
            then removeInt(target)(tail)
            else head :: removeInt(target)(tail)

let recursive lookupOffset key map =
    match map with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupOffset(key)(tail)

let getDefinedTemps inst =
    match inst with
        | LoadConstInt(t, _) -> [t]
        | LoadConstFloat(t, _) -> [t]
        | LoadConstBool(t, _) -> [t]
        | LoadConstStr(t, _) -> [t]
        | LoadProgramArgs(t) -> [t]
        | LoadLocal(t, _) -> [t]
        | LoadEnv(t, _) -> [t]
        | LoadMemOffset(t, _, _) -> [t]
        | AddInt(t, _, _) -> [t]
        | SubInt(t, _, _) -> [t]
        | MulInt(t, _, _) -> [t]
        | DivInt(t, _, _) -> [t]
        | DivUInt(t, _, _) -> [t]
        | AndInt(t, _, _) -> [t]
        | OrInt(t, _, _) -> [t]
        | XorInt(t, _, _) -> [t]
        | ShlInt(t, _, _) -> [t]
        | ShrInt(t, _, _) -> [t]
        | AddFloat(t, _, _) -> [t]
        | SubFloat(t, _, _) -> [t]
        | MulFloat(t, _, _) -> [t]
        | DivFloat(t, _, _) -> [t]
        | CmpIntGt(t, _, _) -> [t]
        | CmpIntGe(t, _, _) -> [t]
        | CmpIntLt(t, _, _) -> [t]
        | CmpIntLe(t, _, _) -> [t]
        | CmpUIntGt(t, _, _) -> [t]
        | CmpUIntGe(t, _, _) -> [t]
        | CmpUIntLt(t, _, _) -> [t]
        | CmpUIntLe(t, _, _) -> [t]
        | CmpIntEq(t, _, _) -> [t]
        | CmpIntNe(t, _, _) -> [t]
        | CmpFloatGt(t, _, _) -> [t]
        | CmpFloatGe(t, _, _) -> [t]
        | CmpFloatLt(t, _, _) -> [t]
        | CmpFloatLe(t, _, _) -> [t]
        | CmpFloatEq(t, _, _) -> [t]
        | CmpFloatNe(t, _, _) -> [t]
        | IntToFloat(t, _) -> [t]
        | FloatToInt(t, _) -> [t]
        | FloatUnaryIntrinsic(t, _, _) -> [t]
        | CallLibm(t, _, _) -> [t]
        | BigIntFromInt(t, _, _) -> [t]
        | BigIntToString(t, _, _) -> [t]
        | BigIntToInt(t, _, _) -> [t]
        | BigIntFromString(t, _, _) -> [t]
        | BigIntBinary(t, _, _, _, _) -> [t]
        | BigIntCompare(t, _, _) -> [t]
        | CmpStrEq(t, _, _) -> [t]
        | CmpStrNe(t, _, _) -> [t]
        | ConcatStr(t, _, _, _) -> [t]
        | ConcatStrTip(t, _, _, _, _, _) -> [t]
        | ConcatStrN(t, _, _) -> [t]
        | RegexCompile(t, _) -> [t]
        | RegexCompileError(t, _) -> [t]
        | RegexFind(t, _, _, _) -> [t]
        | RegexCaptures(t, _, _, _) -> [t]
        | RegexSubstitute(t, _, _, _) -> [t]
        | MakeClosure(t, _, _, _, _, _, _) -> [t]
        | MakeClosureStack(t, _, _, _, _, _) -> [t]
        | LoadFuncAddr(t, _) -> [t]
        | CallClosure(t, _, _, _) -> [t]
        | CallKnown(t, _, _, _, _, _) -> [t]
        | LoadArgumentOwnership(t) -> [t]
        | Alloc(t, _, _) -> [t]
        | AllocStack(t, _) -> [t]
        | AllocAdt(t, _, _, _, _) -> [t]
        | AllocAdtStack(t, _, _, _) -> [t]
        | AllocAdtToSpace(t, _, _, _) -> [t]
        | DropReuse(t, _, _, _) -> [t]
        | AllocReusing(t, _, _, tok, _, _, _) -> [tok]
        | GetAdtTag(t, _) -> [t]
        | GetAdtField(t, _, _, _) -> [t]
        | ReadLine(t) -> [t]
        | ReadExact(t, _) -> [t]
        | MonotonicMillis(t) -> [t]
        | TextByteLength(t, _) -> [t]
        | FileReadText(t, _) -> [t]
        | FileReadAllBytes(t, _) -> [t]
        | FileMmap(t, _) -> [t]
        | FileWriteText(t, _, _) -> [t]
        | FileExists(t, _) -> [t]
        | FileReplace(t, _, _) -> [t]
        | FileMakeExecutable(t, _) -> [t]
        | DirectoryEntries(t, _) -> [t]
        | DirectoryCreateAll(t, _) -> [t]
        | DirectoryRemoveTree(t, _) -> [t]
        | EnvironmentDirectory(t, _) -> [t]
        | EnvironmentGet(t, _) -> [t]
        | FileOpen(t, _) -> [t]
        | FileReadChunk(t, _, _) -> [t]
        | FileReadLine(t, _) -> [t]
        | FileClose(t, _) -> [t]
        | TextUncons(t, _, _) -> [t]
        | TextUnconsText(t, _, _) -> [t]
        | RuneToText(t, _, _) -> [t]
        | RuneFromInt(t, _, _) -> [t]
        | TextParseInt(t, _, _) -> [t]
        | TextParseFloat(t, _, _) -> [t]
        | TextFromInt(t, _, _) -> [t]
        | TextFromFloat(t, _, _) -> [t]
        | TextFormatFloat(t, _, _, _) -> [t]
        | TextToHex(t, _, _) -> [t]
        | TextAsciiCase(t, _, _, _) -> [t]
        | HttpGet(t, _) -> [t]
        | HttpPost(t, _, _) -> [t]
        | NetTcpConnect(t, _, _) -> [t]
        | NetTcpSend(t, _, _) -> [t]
        | NetTcpReceive(t, _, _) -> [t]
        | NetTcpClose(t, _) -> [t]
        | NetTcpListen(t, _) -> [t]
        | NetTcpAccept(t, _) -> [t]
        | BytesEmpty(t, _) -> [t]
        | BytesSingleton(t, _, _) -> [t]
        | BytesLength(t, _) -> [t]
        | BytesGet(t, _, _) -> [t]
        | BytesIndexOf(t, _, _, _) -> [t]
        | BytesCompare(t, _, _) -> [t]
        | BytesScanHash(t, _, _, _) -> [t]
        | BytesSubText(t, _, _, _, _) -> [t]
        | BytesSubView(t, _, _, _) -> [t]
        | BytesAppend(t, _, _, _) -> [t]
        | BytesAppendByte(t, _, _, _) -> [t]
        | BytesAllocate(t, _, _) -> [t]
        | BytesCopyRange(t, _, _, _, _, _, _, _) -> [t]
        | BytesSet(t, _, _, _, _, _) -> [t]
        | BytesSetU16Le(t, _, _, _, _, _) -> [t]
        | BytesSetU32Le(t, _, _, _, _, _) -> [t]
        | BytesSetU64Le(t, _, _, _, _, _) -> [t]
        | BytesFromList(t, _, _) -> [t]
        | BytesHash(t, _) -> [t]
        | BytesU16Le(t, _, _) -> [t]
        | BytesU32Le(t, _, _) -> [t]
        | BytesU64Le(t, _, _) -> [t]
        | BytesGetU16Le(t, _, _) -> [t]
        | BytesGetU32Le(t, _, _) -> [t]
        | BytesGetU64Le(t, _, _) -> [t]
        | FileWriteBytes(t, _, _) -> [t]
        | SpawnProcess(t, _, _) -> [t]
        | ProcessWriteStdin(t, _, _) -> [t]
        | ProcessReadStdoutLine(t, _) -> [t]
        | ProcessReadStderrLine(t, _) -> [t]
        | ProcessWaitForExit(t, _) -> [t]
        | ProcessKill(t, _) -> [t]
        | RcDup(t, _, _, _) -> [t]
        | RcIsUnique(t, _) -> [t]
        | Borrow(t, _) -> [t]
        | CopyOutArena(dest, _, _, _, _, _) -> [dest]
        | CopyOutArenaToSpace(dest, _, _) -> [dest]
        | CopyFixedInto(dest, _, _) -> [dest]
        | CopyStringIntoOrFresh(dest, _, _) -> [dest]
        | CopyFixedIntoOrFresh(dest, _, _, _) -> [dest]
        | CopyOutList(dest, _, _, _, _) -> [dest]
        | CopyOutClosure(dest, _, _, _) -> [dest]
        | CopyOutTcoListCell(dest, _, _, _) -> [dest]
        | ToCString(t, _) -> [t]
        | AllocFfiOut(t, _) -> [t]
        | LoadFfiOut(t, _, _) -> [t]
        | CopyFfiString(t, _, _) -> [t]
        | CopyFfiBytes(t, _, _) -> [t]
        | CallExternal(t, _, _, _, _, _) -> [t]
        | CreateTask(t, _, _, _, _, _) -> [t]
        | CreateCompletedTask(t, _) -> [t]
        | AwaitTask(t, _) -> [t]
        | RunTask(t, _) -> [t]
        | SpawnTask(t, _) -> [t]
        | CreateTaskScope(t, _) -> [t]
        | CreateScopedTask(t, _, _) -> [t]
        | ForkScopedTask(t, _, _) -> [t]
        | JoinScopedTask(t, _) -> [t]
        | ParallelFork(desc, _) -> [desc]
        | ParallelJoin(res, _) -> [res]
        | LoadParallelWorkerOverride(t) -> [t]
        | ParallelQueueStart(desc, _, _, _) -> [desc]
        | ParallelQueueAwait(res, _) -> [res]
        | AsyncSleep(t, _) -> [t]
        | CreateTcpConnectTask(t, _, _) -> [t]
        | CreateTcpSendTask(t, _, _) -> [t]
        | CreateTcpReceiveTask(t, _, _) -> [t]
        | CreateTcpCloseTask(t, _) -> [t]
        | CreateTcpListenTask(t, _) -> [t]
        | CreateForkWorkersTask(t, _, _) -> [t]
        | SetDrainTimeout(t, _) -> [t]
        | RequestServerStop(t) -> [t]
        | CreateTcpAcceptTask(t, _) -> [t]
        | CreateHttpGetTask(t, _) -> [t]
        | CreateHttpPostTask(t, _, _) -> [t]
        | CreateTlsConnectTask(t, _, _) -> [t]
        | CreateTlsHandshakeTask(t, _, _) -> [t]
        | CreateTlsServerHandshakeTask(t, _, _, _) -> [t]
        | CreateTlsSendTask(t, _, _) -> [t]
        | CreateTlsReceiveTask(t, _, _) -> [t]
        | CreateTlsCloseTask(t, _) -> [t]
        | AsyncAll(t, _) -> [t]
        | AsyncRace(t, _) -> [t]
        | LoadCapabilityHandler(t, _) -> [t]
        | _ -> []

let getUsedTemps inst =
    match inst with
        | StoreLocal(_, s) -> [s]
        | StoreMemOffset(basePtr, _, s) -> [basePtr, s]
        | LoadMemOffset(_, basePtr, _) -> [basePtr]
        | AddInt(_, l, r) -> [l, r]
        | SubInt(_, l, r) -> [l, r]
        | MulInt(_, l, r) -> [l, r]
        | DivInt(_, l, r) -> [l, r]
        | DivUInt(_, l, r) -> [l, r]
        | AndInt(_, l, r) -> [l, r]
        | OrInt(_, l, r) -> [l, r]
        | XorInt(_, l, r) -> [l, r]
        | ShlInt(_, l, r) -> [l, r]
        | ShrInt(_, l, r) -> [l, r]
        | AddFloat(_, l, r) -> [l, r]
        | SubFloat(_, l, r) -> [l, r]
        | MulFloat(_, l, r) -> [l, r]
        | DivFloat(_, l, r) -> [l, r]
        | CmpIntGt(_, l, r) -> [l, r]
        | CmpIntGe(_, l, r) -> [l, r]
        | CmpIntLt(_, l, r) -> [l, r]
        | CmpIntLe(_, l, r) -> [l, r]
        | CmpUIntGt(_, l, r) -> [l, r]
        | CmpUIntGe(_, l, r) -> [l, r]
        | CmpUIntLt(_, l, r) -> [l, r]
        | CmpUIntLe(_, l, r) -> [l, r]
        | CmpIntEq(_, l, r) -> [l, r]
        | CmpIntNe(_, l, r) -> [l, r]
        | CmpFloatGt(_, l, r) -> [l, r]
        | CmpFloatGe(_, l, r) -> [l, r]
        | CmpFloatLt(_, l, r) -> [l, r]
        | CmpFloatLe(_, l, r) -> [l, r]
        | CmpFloatEq(_, l, r) -> [l, r]
        | CmpFloatNe(_, l, r) -> [l, r]
        | IntToFloat(_, v) -> [v]
        | FloatToInt(_, v) -> [v]
        | FloatUnaryIntrinsic(_, v, _) -> [v]
        | CallLibm(_, _, args) -> args
        | BigIntFromInt(_, v, _) -> [v]
        | BigIntToString(_, v, _) -> [v]
        | BigIntToInt(_, v, _) -> [v]
        | BigIntFromString(_, v, _) -> [v]
        | BigIntBinary(_, l, r, _, _) -> [l, r]
        | BigIntCompare(_, l, r) -> [l, r]
        | CmpStrEq(_, l, r) -> [l, r]
        | CmpStrNe(_, l, r) -> [l, r]
        | ConcatStr(_, l, r, _) -> [l, r]
        | ConcatStrTip(_, l, r, _, _, _) -> [l, r]
        | ConcatStrN(_, parts, _) -> parts
        | RegexCompile(_, p) -> [p]
        | RegexCompileError(_, p) -> [p]
        | RegexFind(_, c, s, st) -> [c, s, st]
        | RegexCaptures(_, c, s, st) -> [c, s, st]
        | RegexSubstitute(_, c, s, rep) -> [c, s, rep]
        | MakeClosure(_, _, envPtr, _, _, _, _) -> [envPtr]
        | MakeClosureStack(_, _, envPtr, _, _, _) -> [envPtr]
        | CallClosure(_, c, a, flag) ->
            if flag >= 0
            then [c, a, flag]
            else [c, a]
        | CallKnown(_, _, env, a, flag, _) ->
            if flag >= 0
            then [env, a, flag]
            else [env, a]
        | DropReuse(_, s, _, _) -> [s]
        | AllocReusing(_, _, _, tok, _, _, _) -> [tok]
        | SetAdtField(ptr, _, s, _) -> [ptr, s]
        | GetAdtTag(_, ptr) -> [ptr]
        | GetAdtField(_, ptr, _, _) -> [ptr]
        | PrintInt(s) -> [s]
        | PrintStr(s) -> [s]
        | PrintBool(s) -> [s]
        | WriteStr(s) -> [s]
        | WriteErrorStr(s, _) -> [s]
        | ExitProcess(s) -> [s]
        | WriteBufferedStr(s, _) -> [s]
        | ReadExact(_, c) -> [c]
        | ConsolePoll(_, toTemp) -> [toTemp]
        | TextByteLength(_, t) -> [t]
        | FileReadText(_, p) -> [p]
        | FileReadAllBytes(_, p) -> [p]
        | FileMmap(_, p) -> [p]
        | FileWriteText(_, p, txt) -> [p, txt]
        | FileExists(_, p) -> [p]
        | FileReplace(_, src, dst) -> [src, dst]
        | FileMakeExecutable(_, p) -> [p]
        | DirectoryEntries(_, p) -> [p]
        | DirectoryCreateAll(_, p) -> [p]
        | DirectoryRemoveTree(_, p) -> [p]
        | EnvironmentGet(_, n) -> [n]
        | FileOpen(_, p) -> [p]
        | FileReadChunk(_, h, c) -> [h, c]
        | FileReadLine(_, h) -> [h]
        | FileClose(_, h) -> [h]
        | TextUncons(_, t, _) -> [t]
        | TextUnconsText(_, t, _) -> [t]
        | RuneToText(_, r, _) -> [r]
        | RuneFromInt(_, i, _) -> [i]
        | TextParseInt(_, t, _) -> [t]
        | TextParseFloat(_, t, _) -> [t]
        | TextFromInt(_, v, _) -> [v]
        | TextFromFloat(_, v, _) -> [v]
        | TextFormatFloat(_, v, dec, _) -> [v, dec]
        | TextToHex(_, v, _) -> [v]
        | TextAsciiCase(_, s, _, _) -> [s]
        | HttpGet(_, url) -> [url]
        | HttpPost(_, url, body) -> [url, body]
        | NetTcpConnect(_, h, p) -> [h, p]
        | NetTcpSend(_, sock, txt) -> [sock, txt]
        | NetTcpReceive(_, sock, maxB) -> [sock, maxB]
        | NetTcpClose(_, sock) -> [sock]
        | NetTcpListen(_, p) -> [p]
        | NetTcpAccept(_, sock) -> [sock]
        | BytesSingleton(_, b, _) -> [b]
        | BytesLength(_, b) -> [b]
        | BytesGet(_, b, idx) -> [b, idx]
        | BytesIndexOf(_, b, needle, from) -> [b, needle, from]
        | BytesCompare(_, l, r) -> [l, r]
        | BytesScanHash(_, b, needle, from) -> [b, needle, from]
        | BytesSubText(_, b, st, len, _) -> [b, st, len]
        | BytesSubView(_, b, st, len) -> [b, st, len]
        | BytesAppend(_, l, r, _) -> [l, r]
        | BytesAppendByte(_, b, by, _) -> [b, by]
        | BytesAllocate(_, len, _) -> [len]
        | BytesCopyRange(_, b, off, src, srcOff, len, _, _) -> [b, off, src, srcOff, len]
        | BytesSet(_, b, off, val, _, _) -> [b, off, val]
        | BytesSetU16Le(_, b, off, val, _, _) -> [b, off, val]
        | BytesSetU32Le(_, b, off, val, _, _) -> [b, off, val]
        | BytesSetU64Le(_, b, off, val, _, _) -> [b, off, val]
        | BytesFromList(_, lst, _) -> [lst]
        | BytesHash(_, b) -> [b]
        | BytesU16Le(_, val, _) -> [val]
        | BytesU32Le(_, val, _) -> [val]
        | BytesU64Le(_, val, _) -> [val]
        | BytesGetU16Le(_, b, off) -> [b, off]
        | BytesGetU32Le(_, b, off) -> [b, off]
        | BytesGetU64Le(_, b, off) -> [b, off]
        | FileWriteBytes(_, p, b) -> [p, b]
        | SpawnProcess(_, exe, args) -> [exe, args]
        | ProcessWriteStdin(_, proc, txt) -> [proc, txt]
        | ProcessReadStdoutLine(_, proc) -> [proc]
        | ProcessReadStderrLine(_, proc) -> [proc]
        | ProcessWaitForExit(_, proc) -> [proc]
        | ProcessKill(_, proc) -> [proc]
        | CleanupResource(s, _, _) -> [s]
        | RcDrop(s, _, _, _, _, _) -> [s]
        | RcDup(_, s, _, _) -> [s]
        | RcIsUnique(_, s) -> [s]
        | Borrow(_, s) -> [s]
        | TcoResetPending(_, usedTemps, _) -> usedTemps
        | CopyOutArena(_, src, _, _, _, _) -> [src]
        | CopyOutArenaToSpace(_, src, _) -> [src]
        | CopyFixedInto(_, src, _) -> [src]
        | CopyStringIntoOrFresh(oldBlob, src, _) -> [oldBlob, src]
        | CopyFixedIntoOrFresh(oldBlob, src, _, _) -> [oldBlob, src]
        | CopyOutList(_, src, _, _, _) -> [src]
        | CopyOutClosure(_, src, _, _) -> [src]
        | CopyOutTcoListCell(_, src, _, _) -> [src]
        | ToCString(_, s) -> [s]
        | LoadFfiOut(_, slot, _) -> [slot]
        | CopyFfiString(_, ptr, _) -> [ptr]
        | CopyFfiBytes(_, ptr, len) -> [ptr, len]
        | CallExternal(_, _, _, args, _, _) -> args
        | CreateTask(_, closure, _, _, _, _) -> [closure]
        | CreateCompletedTask(_, res) -> [res]
        | AwaitTask(_, task) -> [task]
        | RunTask(_, task) -> [task]
        | SpawnTask(_, task) -> [task]
        | CreateScopedTask(_, parent, scopeTemp) -> [parent, scopeTemp]
        | ForkScopedTask(_, owner, task) -> [owner, task]
        | JoinScopedTask(_, taskHandle) -> [taskHandle]
        | ParallelFork(_, rightClosure) -> [rightClosure]
        | ParallelJoin(_, desc) -> [desc]
        | ParallelCleanup(desc) -> [desc]
        | StoreParallelWorkerOverride(s) -> [s]
        | ParallelQueueStart(_, fClosure, combineClosure, list) -> [fClosure, combineClosure, list]
        | ParallelQueueAwait(_, desc) -> [desc]
        | ParallelQueueCleanup(desc) -> [desc]
        | Suspend(stateStruct, _, task, _) -> [stateStruct, task]
        | Resume(stateStruct, _, _) -> [stateStruct]
        | AsyncSleep(_, ms) -> [ms]
        | CreateTcpConnectTask(_, h, p) -> [h, p]
        | CreateTcpSendTask(_, s, txt) -> [s, txt]
        | CreateTcpReceiveTask(_, s, maxB) -> [s, maxB]
        | CreateTcpCloseTask(_, s) -> [s]
        | CreateTcpListenTask(_, p) -> [p]
        | CreateForkWorkersTask(_, p, count) -> [p, count]
        | SetDrainTimeout(_, ms) -> [ms]
        | RequestServerStop(s) -> [s]
        | CreateTcpAcceptTask(_, s) -> [s]
        | CreateHttpGetTask(_, url) -> [url]
        | CreateHttpPostTask(_, url, body) -> [url, body]
        | CreateTlsConnectTask(_, h, p) -> [h, p]
        | CreateTlsHandshakeTask(_, s, h) -> [s, h]
        | CreateTlsServerHandshakeTask(_, s, cert, key) -> [s, cert, key]
        | CreateTlsSendTask(_, ssl, txt) -> [ssl, txt]
        | CreateTlsReceiveTask(_, ssl, maxB) -> [ssl, maxB]
        | CreateTlsCloseTask(_, ssl) -> [ssl]
        | AsyncAll(_, lst) -> [lst]
        | AsyncRace(_, lst) -> [lst]
        | PanicStr(s) -> [s]
        | StoreCapabilityHandler(_, s) -> [s]
        | JumpIfFalse(cond, _) -> [cond]
        | SwitchTag(tag, _, _) -> [tag]
        | Return(s) -> [s]
        | _ -> []

let getAllTemps inst = append(getDefinedTemps(inst))(getUsedTemps(inst))

let getWrittenLocalSlots inst =
    match inst with
        | StoreLocal(slot, _) -> [slot]
        | ConcatStrTip(_, _, _, rStart, rEnd, _) -> [rStart, rEnd]
        | SaveStackPointer(slot) -> [slot]
        | SaveArenaState(cSlot, endSlot, _) -> [cSlot, endSlot]
        | RestoreArenaState(pSlot, _, _, _) -> [pSlot]
        | _ -> []

let getReadLocalSlots inst =
    match inst with
        | LoadLocal(_, slot) -> [slot]
        | ConcatStrTip(_, _, _, rStart, rEnd, _) -> [rStart, rEnd]
        | RestoreStackPointer(slot) -> [slot]
        | RestoreArenaState(_, cSlot, endSlot, _) -> [cSlot, endSlot]
        | ReclaimArenaChunks(savedEnd, preRestoreEnd, _) -> [savedEnd, preRestoreEnd]
        | TcoResetPending(_, _, readLocals) -> readLocals
        | _ -> []

let recursive findAwaitPositionsAux instructions index acc =
    match instructions with
        | [] -> reverse(acc)
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | AwaitTask(_, _) -> findAwaitPositionsAux(tail)(index + 1)(index :: acc)
                | _ -> findAwaitPositionsAux(tail)(index + 1)(acc)

let findAwaitPositions instructions = findAwaitPositionsAux(instructions)(0)([])

let recursive computeDefinedBefore instructions limit index acc =
    if index >= limit
    then acc
    else
        match instructions with
            | [] -> acc
            | IrInstruction { instruction = inst } :: tail -> computeDefinedBefore(tail)(limit)(index + 1)(unionUniqueInts(acc)(getDefinedTemps(inst)))

let recursive computeUsedAfter instructions startIndex index acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            if index > startIndex
            then computeUsedAfter(tail)(startIndex)(index + 1)(unionUniqueInts(acc)(getUsedTemps(inst)))
            else computeUsedAfter(tail)(startIndex)(index + 1)(acc)

let recursive computeLiveTempsAcrossAwaitsAux instructions awaitPositions =
    match awaitPositions with
        | [] -> []
        | awaitPos :: tail ->
            let definedBefore = computeDefinedBefore(instructions)(awaitPos)(0)([])
            in
                let usedAfter = computeUsedAfter(instructions)(awaitPos)(0)([])
                in
                    let live = removeInt(0)(intersectUniqueInts(definedBefore)(usedAfter))
                    in live :: computeLiveTempsAcrossAwaitsAux(instructions)(tail)

let computeLiveTempsAcrossAwaits instructions awaitPositions = computeLiveTempsAcrossAwaitsAux(instructions)(awaitPositions)

let recursive collectLabelPositions instructions index acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            match inst with
                | Label(name) -> collectLabelPositions(tail)(index + 1)((name, index) :: acc)
                | _ -> collectLabelPositions(tail)(index + 1)(acc)

let recursive checkBackwardJumps instructions labelPositions index =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = inst } :: tail ->
            let target =
                match inst with
                    | Jump(t) -> Some(t)
                    | JumpIfFalse(_, t) -> Some(t)
                    | _ -> None
            in
                match target with
                    | Some(t) ->
                        match lookupOffset(t)(labelPositions) with
                            | Some(pos) ->
                                if pos <= index
                                then true
                                else checkBackwardJumps(tail)(labelPositions)(index + 1)
                            | None -> checkBackwardJumps(tail)(labelPositions)(index + 1)
                    | None -> checkBackwardJumps(tail)(labelPositions)(index + 1)

let hasBackwardJump instructions =
    (let labelPositions = collectLabelPositions(instructions)(0)([])
    in checkBackwardJumps(instructions)(labelPositions)(0))

let recursive computeWrittenLocalsBefore instructions limit index acc =
    if index >= limit
    then acc
    else
        match instructions with
            | [] -> acc
            | IrInstruction { instruction = inst } :: tail -> computeWrittenLocalsBefore(tail)(limit)(index + 1)(unionUniqueInts(acc)(getWrittenLocalSlots(inst)))

let recursive computeReadLocalsAfter instructions startIndex index acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail ->
            if index > startIndex
            then computeReadLocalsAfter(tail)(startIndex)(index + 1)(unionUniqueInts(acc)(getReadLocalSlots(inst)))
            else computeReadLocalsAfter(tail)(startIndex)(index + 1)(acc)

let recursive computeAllWrittenLocals instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail -> computeAllWrittenLocals(tail)(unionUniqueInts(acc)(getWrittenLocalSlots(inst)))

let recursive computeAllReadLocals instructions acc =
    match instructions with
        | [] -> acc
        | IrInstruction { instruction = inst } :: tail -> computeAllReadLocals(tail)(unionUniqueInts(acc)(getReadLocalSlots(inst)))

let recursive replicateValue (count: Int) item =
    if count <= 0
    then []
    else item :: replicateValue(count - 1)(item)

let recursive computeLiveLocalsAcrossAwaitsAux instructions awaitPositions =
    match awaitPositions with
        | [] -> []
        | awaitPos :: tail ->
            let writtenBefore = computeWrittenLocalsBefore(instructions)(awaitPos)(0)([])
            in
                let readAfter = computeReadLocalsAfter(instructions)(awaitPos)(0)([])
                in
                    let live = removeInt(1)(removeInt(0)(intersectUniqueInts(writtenBefore)(readAfter)))
                    in live :: computeLiveLocalsAcrossAwaitsAux(instructions)(tail)

let computeLiveLocalsAcrossAwaits instructions awaitPositions hasBackEdge =
    if hasBackEdge
    then
        let writtenAnywhere = computeAllWrittenLocals(instructions)([])
        in
            let readAnywhere = computeAllReadLocals(instructions)([])
            in
                let liveAnywhere = removeInt(1)(removeInt(0)(intersectUniqueInts(writtenAnywhere)(readAnywhere)))
                in replicateValue(listLength(awaitPositions))(liveAnywhere)
    else computeLiveLocalsAcrossAwaitsAux(instructions)(awaitPositions)

let recursive unionAllLists lists acc =
    match lists with
        | [] -> acc
        | head :: tail -> unionAllLists(tail)(unionUniqueInts(acc)(head))

let recursive assignOffsets items baseOffset step =
    match items with
        | [] -> []
        | head :: tail -> (head, baseOffset) :: assignOffsets(tail)(baseOffset + step)(step)

let assignStateStructSlots liveAcross liveLocalsAcross captureCount =
    (let allLiveTemps = unionAllLists(liveAcross)([])
    in
        let allLiveLocals = unionAllLists(liveLocalsAcross)([])
        in
            let liveVarBaseOffset = taskHeaderSize + captureCount * 8
            in
                let tempOffsets = assignOffsets(allLiveTemps)(liveVarBaseOffset)(8)
                in
                    let localSaveBaseOffset = liveVarBaseOffset + listLength(allLiveTemps) * 8
                    in
                        let localOffsets = assignOffsets(allLiveLocals)(localSaveBaseOffset)(8)
                        in
                            let totalSize = localSaveBaseOffset + listLength(allLiveLocals) * 8
                            in (totalSize, tempOffsets, localOffsets))

let recursive computeMaxBodyTempAux instructions maxSoFar =
    match instructions with
        | [] -> maxSoFar
        | IrInstruction { instruction = inst } :: tail ->
            let temps = getAllTemps(inst)
            in
                let recursive checkTemps ts currentMax =
                    match ts with
                        | [] -> currentMax
                        | t :: rest ->
                            if t > currentMax
                            then checkTemps(rest)(t)
                            else checkTemps(rest)(currentMax)
                in
                    let newMax = checkTemps(temps)(maxSoFar)
                    in computeMaxBodyTempAux(tail)(newMax)

let computeMaxBodyTemp instructions = computeMaxBodyTempAux(instructions)(0)

let assertParallelForkJoinWithinSegment instructions = Unit

let adjustLoadEnvForStateStruct inst stateStructTemp captureCount =
    match inst with
        | IrInstruction { instruction = instKind, location = loc } ->
            match instKind with
                | LoadEnv(target, index) ->
                    IrInstruction(
                        instruction = LoadMemOffset(target)(stateStructTemp)(taskHeaderSize + index * 8),
                        location = loc
                    )
                | _ -> inst

let recursive takeUpTo instructions (limit: Int) (index: Int) acc =
    if index >= limit
    then (reverse(acc), instructions)
    else
        match instructions with
            | [] -> (reverse(acc), [])
            | head :: tail -> takeUpTo(tail)(limit)(index + 1)(head :: acc)

let recursive splitAtAwaitsAux instructions awaitPositions (lastPos: Int) =
    match awaitPositions with
        | [] -> [instructions]
        | pos :: tail ->
            let countToTake = pos - lastPos + 1
            in
                match takeUpTo(instructions)(countToTake)(0)([]) with
                    | (segment, rest) -> segment :: splitAtAwaitsAux(rest)(tail)(pos + 1)

let splitAtAwaits instructions awaitPositions = splitAtAwaitsAux(instructions)(awaitPositions)(0)

let recursive emitSingleStateBodyAux instructions stateStructTemp statusTemp captureCount currentTemp acc =
    match instructions with
        | [] -> (reverse(acc), currentTemp)
        | IrInstruction { instruction = instKind, location = loc } :: tail ->
            match instKind with
                | Return(source) ->
                    let storeRes =
                        IrInstruction(
                            instruction = StoreMemOffset(stateStructTemp)(taskResultSlotOffset)(source),
                            location = loc
                        )
                    in
                        let completedTemp = currentTemp + 1
                        in
                            let loadCompleted =
                                IrInstruction(
                                    instruction = LoadConstInt(completedTemp)(-1),
                                    location = loc
                                )
                            in
                                let storeCompleted =
                                    IrInstruction(
                                        instruction = StoreMemOffset(stateStructTemp)(taskStateIndexOffset)(completedTemp),
                                        location = loc
                                    )
                                in
                                    let loadStatus =
                                        IrInstruction(
                                            instruction = LoadConstInt(statusTemp)(1),
                                            location = loc
                                        )
                                    in
                                        let returnStatus =
                                            IrInstruction(
                                                instruction = Return(statusTemp),
                                                location = loc
                                            )
                                        in
                                            let newAcc = returnStatus :: loadStatus :: storeCompleted :: loadCompleted :: storeRes :: acc
                                            in emitSingleStateBodyAux(tail)(stateStructTemp)(statusTemp)(captureCount)(completedTemp)(newAcc)
                | _ ->
                    let adjusted =
                        adjustLoadEnvForStateStruct(
                            IrInstruction(instruction = instKind, location = loc)
                        )(stateStructTemp)(captureCount)
                    in emitSingleStateBodyAux(tail)(stateStructTemp)(statusTemp)(captureCount)(currentTemp)(adjusted :: acc)

let emitSingleStateBody instructions stateStructTemp statusTemp captureCount startTemp = emitSingleStateBodyAux(instructions)(stateStructTemp)(statusTemp)(captureCount)(startTemp)([])

let recursive emitDispatchChain stateLabels (stateIndex: Int) (totalCount: Int) stateIdxTemp currentTemp acc =
    if stateIndex >= totalCount
    then
        match listGet(0)(stateLabels) with
            | Some(state0Label) -> (reverse(IrInstruction(instruction = Jump(state0Label), location = None) :: acc), currentTemp)
            | None -> (reverse(acc), currentTemp)
    else
        let cmpTemp = currentTemp + 1
        in
            let constTemp = currentTemp + 2
            in
                let nextTemp = currentTemp + 2
                in
                    let loadConst = IrInstruction(instruction = LoadConstInt(constTemp)(stateIndex), location = None)
                    in
                        let cmpInst = IrInstruction(instruction = CmpIntEq(cmpTemp)(stateIdxTemp)(constTemp), location = None)
                        in
                            let falseTarget =
                                if stateIndex + 1 < totalCount
                                then "__dispatch_" + Ashes.Text.fromInt(stateIndex + 1)
                                else
                                    match listGet(0)(stateLabels) with
                                        | Some(lbl) -> lbl
                                        | None -> "__state_0"
                            in
                                let jumpFalse = IrInstruction(instruction = JumpIfFalse(cmpTemp)(falseTarget), location = None)
                                in
                                    let currentLabel =
                                        match listGet(stateIndex)(stateLabels) with
                                            | Some(lbl) -> lbl
                                            | None -> "__state_" + Ashes.Text.fromInt(stateIndex)
                                    in
                                        let jumpState = IrInstruction(instruction = Jump(currentLabel), location = None)
                                        in
                                            let newAcc = jumpState :: jumpFalse :: cmpInst :: loadConst :: acc
                                            in
                                                let newAccWithLabel =
                                                    if stateIndex + 1 < totalCount
                                                    then IrInstruction(instruction = Label("__dispatch_" + Ashes.Text.fromInt(stateIndex + 1)), location = None) :: newAcc
                                                    else newAcc
                                                in emitDispatchChain(stateLabels)(stateIndex + 1)(totalCount)(stateIdxTemp)(nextTemp)(newAccWithLabel)

let emitStateDispatch stateLabels stateIdxTemp startTemp =
    (let count = listLength(stateLabels)
    in emitDispatchChain(stateLabels)(1)(count)(stateIdxTemp)(startTemp)([]))

let recursive restoreLiveTemps temps tempToSlotOffset stateStructTemp clearedConst acc =
    match temps with
        | [] -> (reverse(acc), [])
        | t :: tail ->
            match lookupOffset(t)(tempToSlotOffset) with
                | Some(offset) ->
                    let loadInst = IrInstruction(instruction = LoadMemOffset(t)(stateStructTemp)(offset), location = None)
                    in
                        let clearInst = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(offset)(clearedConst), location = None)
                        in
                            match restoreLiveTemps(tail)(tempToSlotOffset)(stateStructTemp)(clearedConst)(clearInst :: loadInst :: acc) with
                                | (restInsts, restRestores) -> (restInsts, IrFrameRestore(slotOffset = offset, targetTemp = t) :: restRestores)
                | None -> restoreLiveTemps(tail)(tempToSlotOffset)(stateStructTemp)(clearedConst)(acc)

let recursive restoreLiveLocals locals localToSlotOffset stateStructTemp clearedConst currentTemp acc =
    match locals with
        | [] -> (reverse(acc), currentTemp)
        | local :: tail ->
            match lookupOffset(local)(localToSlotOffset) with
                | Some(offset) ->
                    let loadTemp = currentTemp + 1
                    in
                        let loadInst = IrInstruction(instruction = LoadMemOffset(loadTemp)(stateStructTemp)(offset), location = None)
                        in
                            let clearInst = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(offset)(clearedConst), location = None)
                            in
                                let storeInst = IrInstruction(instruction = StoreLocal(local)(loadTemp), location = None)
                                in restoreLiveLocals(tail)(localToSlotOffset)(stateStructTemp)(clearedConst)(loadTemp)(storeInst :: clearInst :: loadInst :: acc)
                | None -> restoreLiveLocals(tail)(localToSlotOffset)(stateStructTemp)(clearedConst)(currentTemp)(acc)

let emitResumePrologue instructions awaitPositions liveAcross liveLocalsAcross tempToSlotOffset localToSlotOffset stateStructTemp (stateIdx: Int) startTemp =
    (let clearedConst = startTemp + 1
    in
        let loadCleared = IrInstruction(instruction = LoadConstInt(clearedConst)(0), location = None)
        in
            let liveTempsAtThisPoint =
                match listGet(stateIdx - 1)(liveAcross) with
                    | Some(ts) -> ts
                    | None -> []
            in
                match restoreLiveTemps(liveTempsAtThisPoint)(tempToSlotOffset)(stateStructTemp)(clearedConst)([]) with
                    | (restoreTempInsts, restoreFrames) ->
                        let liveLocalsAtThisPoint =
                            match listGet(stateIdx - 1)(liveLocalsAcross) with
                                | Some(ls) -> ls
                                | None -> []
                        in
                            match restoreLiveLocals(liveLocalsAtThisPoint)(localToSlotOffset)(stateStructTemp)(clearedConst)(clearedConst)([]) with
                                | (restoreLocalInsts, tempAfterLocals) ->
                                    let awaitInstTarget =
                                        match listGet(stateIdx - 1)(awaitPositions) with
                                            | Some(pos) ->
                                                match listGet(pos)(instructions) with
                                                    | Some(IrInstruction { instruction = instKind }) ->
                                                        match instKind with
                                                            | AwaitTask(target, _) -> target
                                                            | _ -> 0
                                                    | _ -> 0
                                            | None -> 0
                                    in
                                        let loadResult = IrInstruction(instruction = LoadMemOffset(awaitInstTarget)(stateStructTemp)(taskResultSlotOffset), location = None)
                                        in
                                            let resumeInst = IrInstruction(instruction = Resume(stateStructTemp)(awaitInstTarget)(restoreFrames), location = None)
                                            in
                                                let allInsts = append([loadCleared])(append(restoreTempInsts)(append(restoreLocalInsts)([loadResult, resumeInst])))
                                                in (allInsts, tempAfterLocals))

let recursive saveLiveTemps temps tempToSlotOffset stateStructTemp acc =
    match temps with
        | [] -> (reverse(acc), [])
        | t :: tail ->
            match lookupOffset(t)(tempToSlotOffset) with
                | Some(offset) ->
                    let storeInst = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(offset)(t), location = None)
                    in
                        match saveLiveTemps(tail)(tempToSlotOffset)(stateStructTemp)(storeInst :: acc) with
                            | (restInsts, restSaves) -> (restInsts, IrFrameSave(slotOffset = offset, sourceTemp = t) :: restSaves)
                | None -> saveLiveTemps(tail)(tempToSlotOffset)(stateStructTemp)(acc)

let recursive saveLiveLocals locals localToSlotOffset stateStructTemp currentTemp acc =
    match locals with
        | [] -> (reverse(acc), currentTemp)
        | local :: tail ->
            match lookupOffset(local)(localToSlotOffset) with
                | Some(offset) ->
                    let loadTemp = currentTemp + 1
                    in
                        let loadInst = IrInstruction(instruction = LoadLocal(loadTemp)(local), location = None)
                        in
                            let storeInst = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(offset)(loadTemp), location = None)
                            in saveLiveLocals(tail)(localToSlotOffset)(stateStructTemp)(loadTemp)(storeInst :: loadInst :: acc)
                | None -> saveLiveLocals(tail)(localToSlotOffset)(stateStructTemp)(currentTemp)(acc)

let emitSuspendAtAwait awaitTask (stateIdx: Int) liveAcross liveLocalsAcross tempToSlotOffset localToSlotOffset stateStructTemp statusTemp startTemp loc =
    (let liveTempsAtThisPoint =
        match listGet(stateIdx)(liveAcross) with
            | Some(ts) -> ts
            | None -> []
    in
        match saveLiveTemps(liveTempsAtThisPoint)(tempToSlotOffset)(stateStructTemp)([]) with
            | (saveTempInsts, saveFrames) ->
                let liveLocalsAtThisPoint =
                    match listGet(stateIdx)(liveLocalsAcross) with
                        | Some(ls) -> ls
                        | None -> []
                in
                    match saveLiveLocals(liveLocalsAtThisPoint)(localToSlotOffset)(stateStructTemp)(startTemp)([]) with
                        | (saveLocalInsts, tempAfterLocals) ->
                            let taskTemp =
                                match awaitTask with
                                    | AwaitTask(_, tt) -> tt
                                    | _ -> 0
                            in
                                let storeAwaited = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(taskAwaitedTaskOffset)(taskTemp), location = loc)
                                in
                                    let nextStateConst = tempAfterLocals + 1
                                    in
                                        let loadNextState = IrInstruction(instruction = LoadConstInt(nextStateConst)(stateIdx + 1), location = loc)
                                        in
                                            let storeNextState = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(taskStateIndexOffset)(nextStateConst), location = loc)
                                            in
                                                let suspendInst = IrInstruction(instruction = Suspend(stateStructTemp)(stateIdx + 1)(taskTemp)(saveFrames), location = loc)
                                                in
                                                    let loadStatus = IrInstruction(instruction = LoadConstInt(statusTemp)(0), location = loc)
                                                    in
                                                        let returnStatus = IrInstruction(instruction = Return(statusTemp), location = loc)
                                                        in
                                                            let allInsts = append(saveTempInsts)(append(saveLocalInsts)([storeAwaited, loadNextState, storeNextState, suspendInst, loadStatus, returnStatus]))
                                                            in (allInsts, nextStateConst))

let recursive emitStateSegmentAux segment (stateIdx: Int) liveAcross liveLocalsAcross tempToSlotOffset localToSlotOffset stateStructTemp statusTemp captureCount currentTemp acc =
    match segment with
        | [] -> (reverse(acc), currentTemp)
        | IrInstruction { instruction = instKind, location = loc } :: tail ->
            match instKind with
                | AwaitTask(target, taskTemp) ->
                    match emitSuspendAtAwait(
                        AwaitTask(target)(taskTemp)
                    )(stateIdx)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(currentTemp)(loc) with
                        | (suspendInsts, tempAfterSuspend) ->
                            let newAcc = append(reverse(suspendInsts))(acc)
                            in emitStateSegmentAux(tail)(stateIdx)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(captureCount)(tempAfterSuspend)(newAcc)
                | Return(source) ->
                    let storeRes = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(taskResultSlotOffset)(source), location = loc)
                    in
                        match match lookupOffset(source)(tempToSlotOffset) with
                            | Some(transferredOffset) ->
                                let clearedConst = currentTemp + 1
                                in
                                    let loadCleared = IrInstruction(instruction = LoadConstInt(clearedConst)(0), location = loc)
                                    in
                                        let storeCleared = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(transferredOffset)(clearedConst), location = loc)
                                        in ([loadCleared, storeCleared], clearedConst)
                            | None -> ([], currentTemp) with
                            | (clearInsts, tempAfterClear) ->
                                let completedConst = tempAfterClear + 1
                                in
                                    let loadCompleted = IrInstruction(instruction = LoadConstInt(completedConst)(-1), location = loc)
                                    in
                                        let storeCompleted = IrInstruction(instruction = StoreMemOffset(stateStructTemp)(taskStateIndexOffset)(completedConst), location = loc)
                                        in
                                            let loadStatus = IrInstruction(instruction = LoadConstInt(statusTemp)(1), location = loc)
                                            in
                                                let returnStatus = IrInstruction(instruction = Return(statusTemp), location = loc)
                                                in
                                                    let epilogueInsts = append([storeRes])(append(clearInsts)([loadCompleted, storeCompleted, loadStatus, returnStatus]))
                                                    in
                                                        let newAcc = append(reverse(epilogueInsts))(acc)
                                                        in emitStateSegmentAux(tail)(stateIdx)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(captureCount)(completedConst)(newAcc)
                | _ ->
                    let adjusted =
                        adjustLoadEnvForStateStruct(
                            IrInstruction(instruction = instKind, location = loc)
                        )(stateStructTemp)(captureCount)
                    in emitStateSegmentAux(tail)(stateIdx)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(captureCount)(currentTemp)(adjusted :: acc)

let emitStateSegment segment stateIdx liveAcross liveLocalsAcross tempToSlotOffset localToSlotOffset stateStructTemp statusTemp captureCount startTemp = emitStateSegmentAux(segment)(stateIdx)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(captureCount)(startTemp)([])

let recursive generateStateLabels (count: Int) (index: Int) =
    if index >= count
    then []
    else "__state_" + Ashes.Text.fromInt(index) :: generateStateLabels(count)(index + 1)

let recursive emitAllStates segments (stateIdx: Int) stateCount instructions awaitPositions liveAcross liveLocalsAcross tempToSlotOffset localToSlotOffset stateStructTemp statusTemp captureCount currentTemp stateLabels acc =
    match segments with
        | [] -> (reverse(acc), currentTemp)
        | segment :: restSegments ->
            let stateLabel =
                match listGet(stateIdx)(stateLabels) with
                    | Some(lbl) -> lbl
                    | None -> "__state_" + Ashes.Text.fromInt(stateIdx)
            in
                let labelInst = IrInstruction(instruction = Label(stateLabel), location = None)
                in
                    match if stateIdx > 0
                    then
                        emitResumePrologue(
                            instructions
                        )(awaitPositions)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(stateIdx)(currentTemp)
                    else ([], currentTemp) with
                        | (prologueInsts, tempAfterPrologue) ->
                            match emitStateSegment(
                                segment
                            )(stateIdx)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(captureCount)(tempAfterPrologue) with
                                | (segmentInsts, tempAfterSegment) ->
                                    let stateInsts = append([labelInst])(append(prologueInsts)(segmentInsts))
                                    in
                                        let newAcc = append(reverse(stateInsts))(acc)
                                        in
                                            emitAllStates(
                                                restSegments
                                            )(stateIdx + 1)(stateCount)(instructions)(awaitPositions)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(captureCount)(tempAfterSegment)(stateLabels)(newAcc)

let emitMultiStateBody instructions awaitPositions liveAcross liveLocalsAcross tempToSlotOffset localToSlotOffset stateCount stateStructTemp stateIdxTemp statusTemp captureCount startTemp =
    (let loadStateIdx = IrInstruction(instruction = LoadMemOffset(stateIdxTemp)(stateStructTemp)(taskStateIndexOffset), location = None)
    in
        let stateLabels = generateStateLabels(stateCount)(0)
        in
            match emitStateDispatch(stateLabels)(stateIdxTemp)(startTemp) with
                | (dispatchInsts, tempAfterDispatch) ->
                    let segments = splitAtAwaits(instructions)(awaitPositions)
                    in
                        match emitAllStates(
                            segments
                        )(0)(stateCount)(instructions)(awaitPositions)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateStructTemp)(statusTemp)(captureCount)(tempAfterDispatch)(stateLabels)([]) with
                            | (stateInsts, tempAfterStates) ->
                                let headerAndStates = append([loadStateIdx])(append(dispatchInsts)(stateInsts))
                                in (headerAndStates, tempAfterStates))

let transformStateMachine instructions captureCount =
    (let _ = assertParallelForkJoinWithinSegment(instructions)
    in
        let awaitPositions = findAwaitPositions(instructions)
        in
            let stateCount = listLength(awaitPositions) + 1
            in
                let liveAcross = computeLiveTempsAcrossAwaits(instructions)(awaitPositions)
                in
                    let hasBackEdge = hasBackwardJump(instructions)
                    in
                        let liveLocalsAcross = computeLiveLocalsAcrossAwaits(instructions)(awaitPositions)(hasBackEdge)
                        in
                            match assignStateStructSlots(liveAcross)(liveLocalsAcross)(captureCount) with
                                | (stateStructSize, tempToSlotOffset, localToSlotOffset) ->
                                    let maxBodyTemp = computeMaxBodyTemp(instructions)
                                    in
                                        let stateStructTemp = maxBodyTemp + 1
                                        in
                                            let stateIdxTemp = maxBodyTemp + 2
                                            in
                                                let statusTemp = maxBodyTemp + 3
                                                in
                                                    let startTemp = maxBodyTemp + 4
                                                    in
                                                        let loadStateStruct = IrInstruction(instruction = LoadLocal(stateStructTemp)(0), location = None)
                                                        in
                                                            match awaitPositions with
                                                                | [] ->
                                                                    match emitSingleStateBody(instructions)(stateStructTemp)(statusTemp)(captureCount)(startTemp) with
                                                                        | (singleBodyInsts, finalMaxTemp) ->
                                                                            let resultInstructions = loadStateStruct :: singleBodyInsts
                                                                            in
                                                                                StateMachineResult(
                                                                                    instructions = resultInstructions,
                                                                                    stateCount = stateCount,
                                                                                    stateStructSize = stateStructSize,
                                                                                    maxTemp = finalMaxTemp,
                                                                                    savedTempOffsets = tempToSlotOffset,
                                                                                    savedLocalOffsets = localToSlotOffset
                                                                                )
                                                                | _ ->
                                                                    match emitMultiStateBody(
                                                                        instructions
                                                                    )(awaitPositions)(liveAcross)(liveLocalsAcross)(tempToSlotOffset)(localToSlotOffset)(stateCount)(stateStructTemp)(stateIdxTemp)(statusTemp)(captureCount)(startTemp) with
                                                                        | (multiBodyInsts, finalMaxTemp) ->
                                                                            let resultInstructions = loadStateStruct :: multiBodyInsts
                                                                            in
                                                                                StateMachineResult(
                                                                                    instructions = resultInstructions,
                                                                                    stateCount = stateCount,
                                                                                    stateStructSize = stateStructSize,
                                                                                    maxTemp = finalMaxTemp,
                                                                                    savedTempOffsets = tempToSlotOffset,
                                                                                    savedLocalOffsets = localToSlotOffset
                                                                                ))
