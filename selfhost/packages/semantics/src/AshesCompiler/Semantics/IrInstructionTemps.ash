// `mapInstructionTemps` rewrites every temp operand of one instruction, definitions included,
// with one arm per `IrInstructionKind` constructor so a kind can never be silently skipped: the
// optimizer's Borrow/RcDup elision remaps the uses of an erased temp through it, and an arm left
// out would leave a dangling operand behind for codegen to trip over. The arms are derived
// mechanically from the type declaration: every `IrTemp` field passes through `f`, a
// `List(IrTemp)` maps `f` over its elements, frame save/restore records rewrite their temp field,
// and every other field is carried unchanged. Adding a constructor to `IrInstructionKind` without
// an arm here is a compile error, which is the point.
import Ashes.Collection.List.map
import AshesCompiler.Semantics.IrInstructions
export (
    value mapInstructionTemps,
    value mapInstructionLocals,
)

let mapFrameSaveTemps f saves =
    Ashes.Collection.List.map((given (save: IrFrameSave) -> save with sourceTemp = f(save.sourceTemp)))(saves)

let mapFrameRestoreTemps f restores =
    Ashes.Collection.List.map((given (restore: IrFrameRestore) -> restore with targetTemp = f(restore.targetTemp)))(restores)

let mapInstructionTemps f (kind: IrInstructionKind) =
    match kind with
        | LoadConstInt(T0, n0) ->
            LoadConstInt(f(T0))(n0)
        | LoadConstFloat(T0, fl0) ->
            LoadConstFloat(f(T0))(fl0)
        | LoadConstBool(T0, b0) ->
            LoadConstBool(f(T0))(b0)
        | LoadConstStr(T0, s0) ->
            LoadConstStr(f(T0))(s0)
        | LoadProgramArgs(T0) ->
            T0
            |> f
            |> LoadProgramArgs
        | LoadLocal(T0, l0) ->
            LoadLocal(f(T0))(l0)
        | StoreLocal(l0, T0) ->
            T0
            |> f
            |> StoreLocal(l0)
        | LoadEnv(T0, n0) ->
            LoadEnv(f(T0))(n0)
        | StoreMemOffset(T0, n0, T1) ->
            T1
            |> f
            |> StoreMemOffset(f(T0))(n0)
        | LoadMemOffset(T0, T1, n0) ->
            LoadMemOffset(f(T0))(f(T1))(n0)
        | AddInt(T0, T1, T2) ->
            T2
            |> f
            |> AddInt(f(T0))(f(T1))
        | SubInt(T0, T1, T2) ->
            T2
            |> f
            |> SubInt(f(T0))(f(T1))
        | MulInt(T0, T1, T2) ->
            T2
            |> f
            |> MulInt(f(T0))(f(T1))
        | DivInt(T0, T1, T2) ->
            T2
            |> f
            |> DivInt(f(T0))(f(T1))
        | DivUInt(T0, T1, T2) ->
            T2
            |> f
            |> DivUInt(f(T0))(f(T1))
        | AndInt(T0, T1, T2) ->
            T2
            |> f
            |> AndInt(f(T0))(f(T1))
        | OrInt(T0, T1, T2) ->
            T2
            |> f
            |> OrInt(f(T0))(f(T1))
        | XorInt(T0, T1, T2) ->
            T2
            |> f
            |> XorInt(f(T0))(f(T1))
        | ShlInt(T0, T1, T2) ->
            T2
            |> f
            |> ShlInt(f(T0))(f(T1))
        | ShrInt(T0, T1, T2) ->
            T2
            |> f
            |> ShrInt(f(T0))(f(T1))
        | AddFloat(T0, T1, T2) ->
            T2
            |> f
            |> AddFloat(f(T0))(f(T1))
        | SubFloat(T0, T1, T2) ->
            T2
            |> f
            |> SubFloat(f(T0))(f(T1))
        | MulFloat(T0, T1, T2) ->
            T2
            |> f
            |> MulFloat(f(T0))(f(T1))
        | DivFloat(T0, T1, T2) ->
            T2
            |> f
            |> DivFloat(f(T0))(f(T1))
        | CmpIntGt(T0, T1, T2) ->
            T2
            |> f
            |> CmpIntGt(f(T0))(f(T1))
        | CmpIntGe(T0, T1, T2) ->
            T2
            |> f
            |> CmpIntGe(f(T0))(f(T1))
        | CmpIntLt(T0, T1, T2) ->
            T2
            |> f
            |> CmpIntLt(f(T0))(f(T1))
        | CmpIntLe(T0, T1, T2) ->
            T2
            |> f
            |> CmpIntLe(f(T0))(f(T1))
        | CmpUIntGt(T0, T1, T2) ->
            T2
            |> f
            |> CmpUIntGt(f(T0))(f(T1))
        | CmpUIntGe(T0, T1, T2) ->
            T2
            |> f
            |> CmpUIntGe(f(T0))(f(T1))
        | CmpUIntLt(T0, T1, T2) ->
            T2
            |> f
            |> CmpUIntLt(f(T0))(f(T1))
        | CmpUIntLe(T0, T1, T2) ->
            T2
            |> f
            |> CmpUIntLe(f(T0))(f(T1))
        | CmpIntEq(T0, T1, T2) ->
            T2
            |> f
            |> CmpIntEq(f(T0))(f(T1))
        | CmpIntNe(T0, T1, T2) ->
            T2
            |> f
            |> CmpIntNe(f(T0))(f(T1))
        | CmpFloatGt(T0, T1, T2) ->
            T2
            |> f
            |> CmpFloatGt(f(T0))(f(T1))
        | CmpFloatGe(T0, T1, T2) ->
            T2
            |> f
            |> CmpFloatGe(f(T0))(f(T1))
        | CmpFloatLt(T0, T1, T2) ->
            T2
            |> f
            |> CmpFloatLt(f(T0))(f(T1))
        | CmpFloatLe(T0, T1, T2) ->
            T2
            |> f
            |> CmpFloatLe(f(T0))(f(T1))
        | CmpFloatEq(T0, T1, T2) ->
            T2
            |> f
            |> CmpFloatEq(f(T0))(f(T1))
        | CmpFloatNe(T0, T1, T2) ->
            T2
            |> f
            |> CmpFloatNe(f(T0))(f(T1))
        | IntToFloat(T0, T1) ->
            T1
            |> f
            |> IntToFloat(f(T0))
        | FloatToInt(T0, T1) ->
            T1
            |> f
            |> FloatToInt(f(T0))
        | FloatUnaryIntrinsic(T0, T1, s0) ->
            FloatUnaryIntrinsic(f(T0))(f(T1))(s0)
        | CallLibm(T0, s0, L0) ->
            L0
            |> Ashes.Collection.List.map(f)
            |> CallLibm(f(T0))(s0)
        | BigIntFromInt(T0, T1, b0) ->
            BigIntFromInt(f(T0))(f(T1))(b0)
        | BigIntToString(T0, T1, b0) ->
            BigIntToString(f(T0))(f(T1))(b0)
        | BigIntToInt(T0, T1, b0) ->
            BigIntToInt(f(T0))(f(T1))(b0)
        | BigIntFromString(T0, T1, b0) ->
            BigIntFromString(f(T0))(f(T1))(b0)
        | BigIntBinary(T0, T1, T2, s0, b0) ->
            BigIntBinary(f(T0))(f(T1))(f(T2))(s0)(b0)
        | BigIntCompare(T0, T1, T2) ->
            T2
            |> f
            |> BigIntCompare(f(T0))(f(T1))
        | CmpStrEq(T0, T1, T2) ->
            T2
            |> f
            |> CmpStrEq(f(T0))(f(T1))
        | CmpStrNe(T0, T1, T2) ->
            T2
            |> f
            |> CmpStrNe(f(T0))(f(T1))
        | ConcatStr(T0, T1, T2, b0) ->
            ConcatStr(f(T0))(f(T1))(f(T2))(b0)
        | ConcatStrTip(T0, T1, T2, l0, l1, b0) ->
            ConcatStrTip(f(T0))(f(T1))(f(T2))(l0)(l1)(b0)
        | ConcatStrN(T0, L0, b0) ->
            ConcatStrN(f(T0))(Ashes.Collection.List.map(f)(L0))(b0)
        | RegexCompile(T0, T1) ->
            T1
            |> f
            |> RegexCompile(f(T0))
        | RegexCompileError(T0, T1) ->
            T1
            |> f
            |> RegexCompileError(f(T0))
        | RegexFind(T0, T1, T2, T3) ->
            T3
            |> f
            |> RegexFind(f(T0))(f(T1))(f(T2))
        | RegexCaptures(T0, T1, T2, T3) ->
            T3
            |> f
            |> RegexCaptures(f(T0))(f(T1))(f(T2))
        | RegexSubstitute(T0, T1, T2, T3) ->
            T3
            |> f
            |> RegexSubstitute(f(T0))(f(T1))(f(T2))
        | MakeClosure(T0, s0, T1, n0, b0, b1, b2) ->
            MakeClosure(f(T0))(s0)(f(T1))(n0)(b0)(b1)(b2)
        | MakeClosureStack(T0, s0, T1, n0, b0, b1) ->
            MakeClosureStack(f(T0))(s0)(f(T1))(n0)(b0)(b1)
        | LoadFuncAddr(T0, s0) ->
            LoadFuncAddr(f(T0))(s0)
        | CallClosure(T0, T1, T2, T3) ->
            T3
            |> f
            |> CallClosure(f(T0))(f(T1))(f(T2))
        | CallKnown(T0, s0, T1, T2, T3, b0) ->
            CallKnown(f(T0))(s0)(f(T1))(f(T2))(f(T3))(b0)
        | LoadArgumentOwnership(T0) ->
            T0
            |> f
            |> LoadArgumentOwnership
        | Alloc(T0, n0, b0) ->
            Alloc(f(T0))(n0)(b0)
        | AllocStack(T0, n0) ->
            AllocStack(f(T0))(n0)
        | AllocAdt(T0, n0, n1, b0, b1) ->
            AllocAdt(f(T0))(n0)(n1)(b0)(b1)
        | AllocAdtStack(T0, n0, n1, b0) ->
            AllocAdtStack(f(T0))(n0)(n1)(b0)
        | AllocAdtToSpace(T0, n0, n1, b0) ->
            AllocAdtToSpace(f(T0))(n0)(n1)(b0)
        | DropReuse(T0, T1, n0, b0) ->
            DropReuse(f(T0))(f(T1))(n0)(b0)
        | AllocReusing(T0, n0, n1, T1, b0, b1, b2) ->
            AllocReusing(f(T0))(n0)(n1)(f(T1))(b0)(b1)(b2)
        | SetAdtField(T0, n0, T1, b0) ->
            SetAdtField(f(T0))(n0)(f(T1))(b0)
        | SaveStackPointer(l0) -> SaveStackPointer(l0)
        | RestoreStackPointer(l0) -> RestoreStackPointer(l0)
        | GetAdtTag(T0, T1) ->
            T1
            |> f
            |> GetAdtTag(f(T0))
        | GetAdtField(T0, T1, n0, b0) ->
            GetAdtField(f(T0))(f(T1))(n0)(b0)
        | PrintInt(T0) ->
            T0
            |> f
            |> PrintInt
        | PrintStr(T0) ->
            T0
            |> f
            |> PrintStr
        | PrintBool(T0) ->
            T0
            |> f
            |> PrintBool
        | WriteStr(T0) ->
            T0
            |> f
            |> WriteStr
        | WriteErrorStr(T0, b0) ->
            WriteErrorStr(f(T0))(b0)
        | ExitProcess(T0) ->
            T0
            |> f
            |> ExitProcess
        | WriteBufferedStr(T0, b0) ->
            WriteBufferedStr(f(T0))(b0)
        | FlushStdout -> FlushStdout
        | ReadLine(T0) ->
            T0
            |> f
            |> ReadLine
        | ReadExact(T0, T1) ->
            T1
            |> f
            |> ReadExact(f(T0))
        | ConsoleEnableRaw(T0) ->
            T0
            |> f
            |> ConsoleEnableRaw
        | ConsoleRestore(T0) ->
            T0
            |> f
            |> ConsoleRestore
        | ConsolePoll(T0, T1) ->
            T1
            |> f
            |> ConsolePoll(f(T0))
        | MonotonicMillis(T0) ->
            T0
            |> f
            |> MonotonicMillis
        | TextByteLength(T0, T1) ->
            T1
            |> f
            |> TextByteLength(f(T0))
        | FileReadText(T0, T1) ->
            T1
            |> f
            |> FileReadText(f(T0))
        | FileReadAllBytes(T0, T1) ->
            T1
            |> f
            |> FileReadAllBytes(f(T0))
        | FileMmap(T0, T1) ->
            T1
            |> f
            |> FileMmap(f(T0))
        | FileWriteText(T0, T1, T2) ->
            T2
            |> f
            |> FileWriteText(f(T0))(f(T1))
        | FileExists(T0, T1) ->
            T1
            |> f
            |> FileExists(f(T0))
        | FileReplace(T0, T1, T2) ->
            T2
            |> f
            |> FileReplace(f(T0))(f(T1))
        | FileMakeExecutable(T0, T1) ->
            T1
            |> f
            |> FileMakeExecutable(f(T0))
        | DirectoryEntries(T0, T1) ->
            T1
            |> f
            |> DirectoryEntries(f(T0))
        | DirectoryCreateAll(T0, T1) ->
            T1
            |> f
            |> DirectoryCreateAll(f(T0))
        | DirectoryRemoveTree(T0, T1) ->
            T1
            |> f
            |> DirectoryRemoveTree(f(T0))
        | EnvironmentDirectory(T0, ed0) ->
            EnvironmentDirectory(f(T0))(ed0)
        | EnvironmentGet(T0, T1) ->
            T1
            |> f
            |> EnvironmentGet(f(T0))
        | FileOpen(T0, T1) ->
            T1
            |> f
            |> FileOpen(f(T0))
        | FileReadChunk(T0, T1, T2) ->
            T2
            |> f
            |> FileReadChunk(f(T0))(f(T1))
        | FileReadLine(T0, T1) ->
            T1
            |> f
            |> FileReadLine(f(T0))
        | FileClose(T0, T1) ->
            T1
            |> f
            |> FileClose(f(T0))
        | TextUncons(T0, T1, b0) ->
            TextUncons(f(T0))(f(T1))(b0)
        | TextUnconsText(T0, T1, b0) ->
            TextUnconsText(f(T0))(f(T1))(b0)
        | RuneToText(T0, T1, b0) ->
            RuneToText(f(T0))(f(T1))(b0)
        | RuneFromInt(T0, T1, b0) ->
            RuneFromInt(f(T0))(f(T1))(b0)
        | TextParseInt(T0, T1, b0) ->
            TextParseInt(f(T0))(f(T1))(b0)
        | TextParseFloat(T0, T1, b0) ->
            TextParseFloat(f(T0))(f(T1))(b0)
        | TextFromInt(T0, T1, b0) ->
            TextFromInt(f(T0))(f(T1))(b0)
        | TextFromFloat(T0, T1, b0) ->
            TextFromFloat(f(T0))(f(T1))(b0)
        | TextFormatFloat(T0, T1, T2, b0) ->
            TextFormatFloat(f(T0))(f(T1))(f(T2))(b0)
        | TextToHex(T0, T1, b0) ->
            TextToHex(f(T0))(f(T1))(b0)
        | TextAsciiCase(T0, T1, b0, b1) ->
            TextAsciiCase(f(T0))(f(T1))(b0)(b1)
        | HttpGet(T0, T1) ->
            T1
            |> f
            |> HttpGet(f(T0))
        | HttpPost(T0, T1, T2) ->
            T2
            |> f
            |> HttpPost(f(T0))(f(T1))
        | NetTcpConnect(T0, T1, T2) ->
            T2
            |> f
            |> NetTcpConnect(f(T0))(f(T1))
        | NetTcpSend(T0, T1, T2) ->
            T2
            |> f
            |> NetTcpSend(f(T0))(f(T1))
        | NetTcpReceive(T0, T1, T2) ->
            T2
            |> f
            |> NetTcpReceive(f(T0))(f(T1))
        | NetTcpClose(T0, T1) ->
            T1
            |> f
            |> NetTcpClose(f(T0))
        | NetTcpListen(T0, T1) ->
            T1
            |> f
            |> NetTcpListen(f(T0))
        | NetTcpAccept(T0, T1) ->
            T1
            |> f
            |> NetTcpAccept(f(T0))
        | BytesEmpty(T0, b0) ->
            BytesEmpty(f(T0))(b0)
        | BytesSingleton(T0, T1, b0) ->
            BytesSingleton(f(T0))(f(T1))(b0)
        | BytesLength(T0, T1) ->
            T1
            |> f
            |> BytesLength(f(T0))
        | BytesGet(T0, T1, T2) ->
            T2
            |> f
            |> BytesGet(f(T0))(f(T1))
        | BytesIndexOf(T0, T1, T2, T3) ->
            T3
            |> f
            |> BytesIndexOf(f(T0))(f(T1))(f(T2))
        | BytesCompare(T0, T1, T2) ->
            T2
            |> f
            |> BytesCompare(f(T0))(f(T1))
        | BytesScanHash(T0, T1, T2, T3) ->
            T3
            |> f
            |> BytesScanHash(f(T0))(f(T1))(f(T2))
        | BytesSubText(T0, T1, T2, T3, b0) ->
            BytesSubText(f(T0))(f(T1))(f(T2))(f(T3))(b0)
        | BytesSubView(T0, T1, T2, T3) ->
            T3
            |> f
            |> BytesSubView(f(T0))(f(T1))(f(T2))
        | BytesAppend(T0, T1, T2, b0) ->
            BytesAppend(f(T0))(f(T1))(f(T2))(b0)
        | BytesAppendByte(T0, T1, T2, b0) ->
            BytesAppendByte(f(T0))(f(T1))(f(T2))(b0)
        | BytesAllocate(T0, T1, b0) ->
            BytesAllocate(f(T0))(f(T1))(b0)
        | BytesCopyRange(T0, T1, T2, T3, T4, T5, b0, b1) ->
            BytesCopyRange(f(T0))(f(T1))(f(T2))(f(T3))(f(T4))(f(T5))(b0)(b1)
        | BytesSet(T0, T1, T2, T3, b0, b1) ->
            BytesSet(f(T0))(f(T1))(f(T2))(f(T3))(b0)(b1)
        | BytesSetU16Le(T0, T1, T2, T3, b0, b1) ->
            BytesSetU16Le(f(T0))(f(T1))(f(T2))(f(T3))(b0)(b1)
        | BytesSetU32Le(T0, T1, T2, T3, b0, b1) ->
            BytesSetU32Le(f(T0))(f(T1))(f(T2))(f(T3))(b0)(b1)
        | BytesSetU64Le(T0, T1, T2, T3, b0, b1) ->
            BytesSetU64Le(f(T0))(f(T1))(f(T2))(f(T3))(b0)(b1)
        | BytesFromList(T0, T1, b0, b1) ->
            BytesFromList(f(T0))(f(T1))(b0)(b1)
        | BytesHash(T0, T1) ->
            T1
            |> f
            |> BytesHash(f(T0))
        | BytesU16Le(T0, T1, b0) ->
            BytesU16Le(f(T0))(f(T1))(b0)
        | BytesU32Le(T0, T1, b0) ->
            BytesU32Le(f(T0))(f(T1))(b0)
        | BytesU64Le(T0, T1, b0) ->
            BytesU64Le(f(T0))(f(T1))(b0)
        | BytesGetU16Le(T0, T1, T2) ->
            T2
            |> f
            |> BytesGetU16Le(f(T0))(f(T1))
        | BytesGetU32Le(T0, T1, T2) ->
            T2
            |> f
            |> BytesGetU32Le(f(T0))(f(T1))
        | BytesGetU64Le(T0, T1, T2) ->
            T2
            |> f
            |> BytesGetU64Le(f(T0))(f(T1))
        | FileWriteBytes(T0, T1, T2) ->
            T2
            |> f
            |> FileWriteBytes(f(T0))(f(T1))
        | SpawnProcess(T0, T1, T2) ->
            T2
            |> f
            |> SpawnProcess(f(T0))(f(T1))
        | ProcessWriteStdin(T0, T1, T2) ->
            T2
            |> f
            |> ProcessWriteStdin(f(T0))(f(T1))
        | ProcessReadStdoutLine(T0, T1) ->
            T1
            |> f
            |> ProcessReadStdoutLine(f(T0))
        | ProcessReadStderrLine(T0, T1) ->
            T1
            |> f
            |> ProcessReadStderrLine(f(T0))
        | ProcessWaitForExit(T0, T1) ->
            T1
            |> f
            |> ProcessWaitForExit(f(T0))
        | ProcessKill(T0, T1) ->
            T1
            |> f
            |> ProcessKill(f(T0))
        | CleanupResource(T0, s0, MA0) ->
            CleanupResource(f(T0))(s0)(MA0)
        | RcDrop(T0, s0, l0, b0, b1, MS0) ->
            RcDrop(f(T0))(s0)(l0)(b0)(b1)(MS0)
        | RcDup(T0, T1, b0, b1) ->
            RcDup(f(T0))(f(T1))(b0)(b1)
        | RcIsUnique(T0, T1) ->
            T1
            |> f
            |> RcIsUnique(f(T0))
        | Borrow(T0, T1) ->
            T1
            |> f
            |> Borrow(f(T0))
        | TcoResetPending(n0, L0, LL0) ->
            TcoResetPending(n0)(Ashes.Collection.List.map(f)(L0))(LL0)
        | SaveArenaState(l0, l1, b0) -> SaveArenaState(l0)(l1)(b0)
        | RestoreArenaState(l0, l1, l2, b0) -> RestoreArenaState(l0)(l1)(l2)(b0)
        | ReclaimArenaChunks(l0, l1, b0) -> ReclaimArenaChunks(l0)(l1)(b0)
        | CopyOutArena(T0, T1, n0, b0, cp0, MT0) ->
            CopyOutArena(f(T0))(f(T1))(n0)(b0)(cp0)(MT0)
        | CopyOutArenaToSpace(T0, T1, n0) ->
            CopyOutArenaToSpace(f(T0))(f(T1))(n0)
        | CopyFixedInto(T0, T1, n0) ->
            CopyFixedInto(f(T0))(f(T1))(n0)
        | CopyStringIntoOrFresh(T0, T1, T2) ->
            T2
            |> f
            |> CopyStringIntoOrFresh(f(T0))(f(T1))
        | CopyFixedIntoOrFresh(T0, T1, T2, n0) ->
            CopyFixedIntoOrFresh(f(T0))(f(T1))(f(T2))(n0)
        | CopyOutList(T0, T1, hc0, b0, cp0) ->
            CopyOutList(f(T0))(f(T1))(hc0)(b0)(cp0)
        | CopyOutClosure(T0, T1, b0, cp0) ->
            CopyOutClosure(f(T0))(f(T1))(b0)(cp0)
        | CopyOutTcoListCell(T0, T1, hc0, cp0) ->
            CopyOutTcoListCell(f(T0))(f(T1))(hc0)(cp0)
        | ToCString(T0, T1) ->
            T1
            |> f
            |> ToCString(f(T0))
        | AllocFfiOut(T0, ea0) ->
            AllocFfiOut(f(T0))(ea0)
        | LoadFfiOut(T0, T1, ea0) ->
            LoadFfiOut(f(T0))(f(T1))(ea0)
        | CopyFfiString(T0, T1, ea0) ->
            CopyFfiString(f(T0))(f(T1))(ea0)
        | CopyFfiBytes(T0, T1, T2) ->
            T2
            |> f
            |> CopyFfiBytes(f(T0))(f(T1))
        | CallExternal(T0, s0, MS0, L0, EA0, ea0) ->
            CallExternal(f(T0))(s0)(MS0)(Ashes.Collection.List.map(f)(L0))(EA0)(ea0)
        | CreateTask(T0, T1, n0, n1, MS0, b0) ->
            CreateTask(f(T0))(f(T1))(n0)(n1)(MS0)(b0)
        | CreateCompletedTask(T0, T1) ->
            T1
            |> f
            |> CreateCompletedTask(f(T0))
        | AwaitTask(T0, T1) ->
            T1
            |> f
            |> AwaitTask(f(T0))
        | RunTask(T0, T1) ->
            T1
            |> f
            |> RunTask(f(T0))
        | SpawnTask(T0, T1) ->
            T1
            |> f
            |> SpawnTask(f(T0))
        | CreateTaskScope(T0, b0) ->
            CreateTaskScope(f(T0))(b0)
        | CreateScopedTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateScopedTask(f(T0))(f(T1))
        | ForkScopedTask(T0, T1, T2) ->
            T2
            |> f
            |> ForkScopedTask(f(T0))(f(T1))
        | JoinScopedTask(T0, T1) ->
            T1
            |> f
            |> JoinScopedTask(f(T0))
        | ParallelFork(T0, T1) ->
            T1
            |> f
            |> ParallelFork(f(T0))
        | ParallelJoin(T0, T1) ->
            T1
            |> f
            |> ParallelJoin(f(T0))
        | ParallelCleanup(T0) ->
            T0
            |> f
            |> ParallelCleanup
        | LoadParallelWorkerOverride(T0) ->
            T0
            |> f
            |> LoadParallelWorkerOverride
        | StoreParallelWorkerOverride(T0) ->
            T0
            |> f
            |> StoreParallelWorkerOverride
        | ParallelQueueStart(T0, T1, T2, T3) ->
            T3
            |> f
            |> ParallelQueueStart(f(T0))(f(T1))(f(T2))
        | ParallelQueueAwait(T0, T1) ->
            T1
            |> f
            |> ParallelQueueAwait(f(T0))
        | ParallelQueueCleanup(T0) ->
            T0
            |> f
            |> ParallelQueueCleanup
        | Suspend(T0, n0, T1, FS0) ->
            FS0
            |> mapFrameSaveTemps(f)
            |> Suspend(f(T0))(n0)(f(T1))
        | Resume(T0, T1, FR0) ->
            FR0
            |> mapFrameRestoreTemps(f)
            |> Resume(f(T0))(f(T1))
        | AsyncSleep(T0, T1) ->
            T1
            |> f
            |> AsyncSleep(f(T0))
        | CreateTcpConnectTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateTcpConnectTask(f(T0))(f(T1))
        | CreateTcpSendTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateTcpSendTask(f(T0))(f(T1))
        | CreateTcpReceiveTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateTcpReceiveTask(f(T0))(f(T1))
        | CreateTcpCloseTask(T0, T1) ->
            T1
            |> f
            |> CreateTcpCloseTask(f(T0))
        | CreateTcpListenTask(T0, T1) ->
            T1
            |> f
            |> CreateTcpListenTask(f(T0))
        | CreateForkWorkersTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateForkWorkersTask(f(T0))(f(T1))
        | SetDrainTimeout(T0, T1) ->
            T1
            |> f
            |> SetDrainTimeout(f(T0))
        | RequestServerStop(T0) ->
            T0
            |> f
            |> RequestServerStop
        | CreateTcpAcceptTask(T0, T1) ->
            T1
            |> f
            |> CreateTcpAcceptTask(f(T0))
        | CreateHttpGetTask(T0, T1) ->
            T1
            |> f
            |> CreateHttpGetTask(f(T0))
        | CreateHttpPostTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateHttpPostTask(f(T0))(f(T1))
        | CreateTlsConnectTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateTlsConnectTask(f(T0))(f(T1))
        | CreateTlsHandshakeTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateTlsHandshakeTask(f(T0))(f(T1))
        | CreateTlsServerHandshakeTask(T0, T1, T2, T3) ->
            T3
            |> f
            |> CreateTlsServerHandshakeTask(f(T0))(f(T1))(f(T2))
        | CreateTlsSendTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateTlsSendTask(f(T0))(f(T1))
        | CreateTlsReceiveTask(T0, T1, T2) ->
            T2
            |> f
            |> CreateTlsReceiveTask(f(T0))(f(T1))
        | CreateTlsCloseTask(T0, T1) ->
            T1
            |> f
            |> CreateTlsCloseTask(f(T0))
        | AsyncAll(T0, T1) ->
            T1
            |> f
            |> AsyncAll(f(T0))
        | AsyncRace(T0, T1) ->
            T1
            |> f
            |> AsyncRace(f(T0))
        | PanicStr(T0) ->
            T0
            |> f
            |> PanicStr
        | LoadCapabilityHandler(T0, n0) ->
            LoadCapabilityHandler(f(T0))(n0)
        | StoreCapabilityHandler(n0, T0) ->
            T0
            |> f
            |> StoreCapabilityHandler(n0)
        | Label(s0) -> Label(s0)
        | Jump(s0) -> Jump(s0)
        | JumpIfFalse(T0, s0) ->
            JumpIfFalse(f(T0))(s0)
        | SwitchTag(T0, SC0, s0) ->
            SwitchTag(f(T0))(SC0)(s0)
        | Return(T0) ->
            T0
            |> f
            |> Return

// `mapInstructionLocals` rewrites every local slot operand of one instruction, the arms
// covering exactly the constructors that carry an `IrLocal` (an owner slot of -1 passes through
// `f` like any other value, so `f` decides what a missing owner maps to); every other kind is
// carried unchanged. A loop retiring an unused flag slot renumbers the function's locals through
// it.
let mapInstructionLocals f (kind: IrInstructionKind) =
    match kind with
        | LoadLocal(T0, l0) ->
            l0
            |> f
            |> LoadLocal(T0)
        | StoreLocal(l0, T0) ->
            StoreLocal(f(l0))(T0)
        | ConcatStrTip(T0, T1, T2, l0, l1, b0) ->
            ConcatStrTip(T0)(T1)(T2)(f(l0))(f(l1))(b0)
        | SaveStackPointer(l0) ->
            l0
            |> f
            |> SaveStackPointer
        | RestoreStackPointer(l0) ->
            l0
            |> f
            |> RestoreStackPointer
        | RcDrop(T0, s0, l0, b0, b1, m0) ->
            RcDrop(T0)(s0)(f(l0))(b0)(b1)(m0)
        | TcoResetPending(n0, Ts0, Ls0) ->
            Ls0
            |> map(f)
            |> TcoResetPending(n0)(Ts0)
        | SaveArenaState(l0, l1, b0) ->
            SaveArenaState(f(l0))(f(l1))(b0)
        | RestoreArenaState(l0, l1, l2, b0) ->
            RestoreArenaState(f(l0))(f(l1))(f(l2))(b0)
        | ReclaimArenaChunks(l0, l1, b0) ->
            ReclaimArenaChunks(f(l0))(f(l1))(b0)
        | other -> other
