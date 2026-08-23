// Validates structural and semantic invariants of lowered and optimized IR programs.
//
// Invariants:
// - Program entry function must have a non-empty label and hasEnvAndArgParams == false.
// - All function labels and string literal labels across the program must be unique.
// - Local and temp index spaces are non-negative and all referenced slots are strictly bounded.
// - All control flow branch targets must resolve to defined instruction labels within the same function.
// - String constants referenced by LoadConstStr must be declared in program string literals.
// - Coroutine state counts and struct sizes must be non-negative.
// - Local debug metadata entries must refer to valid local slot indices.

import Ashes.Collection.List.append
import Ashes.Collection.List.filter
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.Types
export (
    type IrValidationSeverity(..),
    type IrValidationIssue(..),
    type IrValidationReport(..),
    value validateIrProgram,
    value validateIrFunction,
    value isIrProgramValid,
    value assertValidIrProgram,
)

type IrValidationSeverity =
    | IrError
    | IrWarning
    deriving {Eq, Show}

type IrValidationIssue =
    | message: Str
    | functionLabel: Maybe(Str)
    | instructionIndex: Maybe(Int)
    | severity: IrValidationSeverity
    deriving {Eq, Show}

type IrValidationReport =
    | issues: List(IrValidationIssue)
    | isValid: Bool
    deriving {Eq, Show}

let validationError message functionLabel instructionIndex =
    IrValidationIssue(
        message = message,
        functionLabel = functionLabel,
        instructionIndex = instructionIndex,
        severity = IrError
    )

let validationWarning message functionLabel instructionIndex =
    IrValidationIssue(
        message = message,
        functionLabel = functionLabel,
        instructionIndex = instructionIndex,
        severity = IrWarning
    )

let recursive containsText target list =
    match list with
        | [] -> false
        | head :: tail ->
            if head == target
            then true
            else containsText(target)(tail)

let recursive findDuplicateTexts seen list =
    match list with
        | [] -> []
        | head :: tail ->
            if containsText(head)(seen)
            then head :: findDuplicateTexts(seen)(tail)
            else findDuplicateTexts(head :: seen)(tail)

let instructionDefinedLabel (kind: IrInstructionKind) =
    match kind with
        | Label(name) -> Some(name)
        | _ -> None

let instructionTargetLabels (kind: IrInstructionKind) =
    match kind with
        | Jump(target) -> [target]
        | JumpIfFalse(_, target) -> [target]
        | SwitchTag(_, cases, defaultTarget) ->
            defaultTarget :: map(given (case) ->
                match case with
                    | IrSwitchCase { label = label } -> label)(cases)
        | _ -> []

let instructionLocals (kind: IrInstructionKind) =
    match kind with
        | LoadLocal(_, slot) -> [slot]
        | StoreLocal(slot, _) -> [slot]
        | ConcatStrTip(_, _, _, slotA, slotB, _) -> [slotA, slotB]
        | SaveStackPointer(slot) -> [slot]
        | RestoreStackPointer(slot) -> [slot]
        | RcDrop(_, _, slot, _, _, _) -> [slot]
        | TcoResetPending(_, _, slots) -> slots
        | SaveArenaState(slotA, slotB, _) -> [slotA, slotB]
        | RestoreArenaState(slotA, slotB, slotC, _) -> [slotA, slotB, slotC]
        | ReclaimArenaChunks(slotA, slotB, _) -> [slotA, slotB]
        | _ -> []

let instructionTemps (kind: IrInstructionKind) =
    match kind with
        | LoadConstInt(t, _) -> [t]
        | LoadConstFloat(t, _) -> [t]
        | LoadConstBool(t, _) -> [t]
        | LoadConstStr(t, _) -> [t]
        | LoadProgramArgs(t) -> [t]
        | LoadLocal(t, _) -> [t]
        | StoreLocal(_, t) -> [t]
        | LoadEnv(t, _) -> [t]
        | StoreMemOffset(t1, _, t2) -> [t1, t2]
        | LoadMemOffset(t1, t2, _) -> [t1, t2]
        | AddInt(t1, t2, t3) -> [t1, t2, t3]
        | SubInt(t1, t2, t3) -> [t1, t2, t3]
        | MulInt(t1, t2, t3) -> [t1, t2, t3]
        | DivInt(t1, t2, t3) -> [t1, t2, t3]
        | DivUInt(t1, t2, t3) -> [t1, t2, t3]
        | AndInt(t1, t2, t3) -> [t1, t2, t3]
        | OrInt(t1, t2, t3) -> [t1, t2, t3]
        | XorInt(t1, t2, t3) -> [t1, t2, t3]
        | ShlInt(t1, t2, t3) -> [t1, t2, t3]
        | ShrInt(t1, t2, t3) -> [t1, t2, t3]
        | AddFloat(t1, t2, t3) -> [t1, t2, t3]
        | SubFloat(t1, t2, t3) -> [t1, t2, t3]
        | MulFloat(t1, t2, t3) -> [t1, t2, t3]
        | DivFloat(t1, t2, t3) -> [t1, t2, t3]
        | CmpIntGt(t1, t2, t3) -> [t1, t2, t3]
        | CmpIntGe(t1, t2, t3) -> [t1, t2, t3]
        | CmpIntLt(t1, t2, t3) -> [t1, t2, t3]
        | CmpIntLe(t1, t2, t3) -> [t1, t2, t3]
        | CmpUIntGt(t1, t2, t3) -> [t1, t2, t3]
        | CmpUIntGe(t1, t2, t3) -> [t1, t2, t3]
        | CmpUIntLt(t1, t2, t3) -> [t1, t2, t3]
        | CmpUIntLe(t1, t2, t3) -> [t1, t2, t3]
        | CmpIntEq(t1, t2, t3) -> [t1, t2, t3]
        | CmpIntNe(t1, t2, t3) -> [t1, t2, t3]
        | CmpFloatGt(t1, t2, t3) -> [t1, t2, t3]
        | CmpFloatGe(t1, t2, t3) -> [t1, t2, t3]
        | CmpFloatLt(t1, t2, t3) -> [t1, t2, t3]
        | CmpFloatLe(t1, t2, t3) -> [t1, t2, t3]
        | CmpFloatEq(t1, t2, t3) -> [t1, t2, t3]
        | CmpFloatNe(t1, t2, t3) -> [t1, t2, t3]
        | IntToFloat(t1, t2) -> [t1, t2]
        | FloatToInt(t1, t2) -> [t1, t2]
        | FloatUnaryIntrinsic(t1, t2, _) -> [t1, t2]
        | CallLibm(t1, _, args) -> t1 :: args
        | BigIntFromInt(t1, t2, _) -> [t1, t2]
        | BigIntToString(t1, t2, _) -> [t1, t2]
        | BigIntToInt(t1, t2, _) -> [t1, t2]
        | BigIntFromString(t1, t2, _) -> [t1, t2]
        | BigIntBinary(t1, t2, t3, _, _) -> [t1, t2, t3]
        | BigIntCompare(t1, t2, t3) -> [t1, t2, t3]
        | CmpStrEq(t1, t2, t3) -> [t1, t2, t3]
        | CmpStrNe(t1, t2, t3) -> [t1, t2, t3]
        | ConcatStr(t1, t2, t3, _) -> [t1, t2, t3]
        | ConcatStrTip(t1, t2, t3, _, _, _) -> [t1, t2, t3]
        | RegexCompile(t1, t2) -> [t1, t2]
        | RegexCompileError(t1, t2) -> [t1, t2]
        | RegexFind(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | RegexCaptures(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | RegexSubstitute(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | MakeClosure(t1, _, t2, _, _, _, _) -> [t1, t2]
        | MakeClosureStack(t1, _, t2, _, _, _) -> [t1, t2]
        | LoadFuncAddr(t, _) -> [t]
        | CallClosure(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | CallKnown(t1, _, t2, t3, t4, _) -> [t1, t2, t3, t4]
        | LoadArgumentOwnership(t) -> [t]
        | Alloc(t, _, _) -> [t]
        | AllocStack(t, _) -> [t]
        | AllocAdt(t, _, _, _) -> [t]
        | AllocAdtStack(t, _, _) -> [t]
        | AllocAdtToSpace(t, _, _) -> [t]
        | DropReuse(t1, t2, _, _) -> [t1, t2]
        | AllocReusing(t1, _, _, t2, _, _) -> [t1, t2]
        | SetAdtField(t1, _, t2) -> [t1, t2]
        | SaveStackPointer(_) -> []
        | RestoreStackPointer(_) -> []
        | GetAdtTag(t1, t2) -> [t1, t2]
        | GetAdtField(t1, t2, _) -> [t1, t2]
        | PrintInt(t) -> [t]
        | PrintStr(t) -> [t]
        | PrintBool(t) -> [t]
        | WriteStr(t) -> [t]
        | WriteErrorStr(t, _) -> [t]
        | ExitProcess(t) -> [t]
        | WriteBufferedStr(t, _) -> [t]
        | FlushStdout -> []
        | ReadLine(t) -> [t]
        | ReadExact(t1, t2) -> [t1, t2]
        | ConsoleEnableRaw(t) -> [t]
        | ConsoleRestore(t) -> [t]
        | ConsolePoll(t1, t2) -> [t1, t2]
        | MonotonicMillis(t) -> [t]
        | TextByteLength(t1, t2) -> [t1, t2]
        | FileReadText(t1, t2) -> [t1, t2]
        | FileReadAllBytes(t1, t2) -> [t1, t2]
        | FileMmap(t1, t2) -> [t1, t2]
        | FileWriteText(t1, t2, t3) -> [t1, t2, t3]
        | FileExists(t1, t2) -> [t1, t2]
        | FileReplace(t1, t2, t3) -> [t1, t2, t3]
        | FileMakeExecutable(t1, t2) -> [t1, t2]
        | DirectoryEntries(t1, t2) -> [t1, t2]
        | DirectoryCreateAll(t1, t2) -> [t1, t2]
        | DirectoryRemoveTree(t1, t2) -> [t1, t2]
        | EnvironmentDirectory(t, _) -> [t]
        | EnvironmentGet(t1, t2) -> [t1, t2]
        | FileOpen(t1, t2) -> [t1, t2]
        | FileReadChunk(t1, t2, t3) -> [t1, t2, t3]
        | FileReadLine(t1, t2) -> [t1, t2]
        | FileClose(t1, t2) -> [t1, t2]
        | TextUncons(t1, t2, _) -> [t1, t2]
        | TextUnconsText(t1, t2, _) -> [t1, t2]
        | RuneToText(t1, t2, _) -> [t1, t2]
        | RuneFromInt(t1, t2, _) -> [t1, t2]
        | TextParseInt(t1, t2, _) -> [t1, t2]
        | TextParseFloat(t1, t2, _) -> [t1, t2]
        | TextFromInt(t1, t2, _) -> [t1, t2]
        | TextFromFloat(t1, t2, _) -> [t1, t2]
        | TextFormatFloat(t1, t2, t3, _) -> [t1, t2, t3]
        | TextToHex(t1, t2, _) -> [t1, t2]
        | TextAsciiCase(t1, t2, _, _) -> [t1, t2]
        | HttpGet(t1, t2) -> [t1, t2]
        | HttpPost(t1, t2, t3) -> [t1, t2, t3]
        | NetTcpConnect(t1, t2, t3) -> [t1, t2, t3]
        | NetTcpSend(t1, t2, t3) -> [t1, t2, t3]
        | NetTcpReceive(t1, t2, t3) -> [t1, t2, t3]
        | NetTcpClose(t1, t2) -> [t1, t2]
        | NetTcpListen(t1, t2) -> [t1, t2]
        | NetTcpAccept(t1, t2) -> [t1, t2]
        | BytesEmpty(t, _) -> [t]
        | BytesSingleton(t1, t2, _) -> [t1, t2]
        | BytesLength(t1, t2) -> [t1, t2]
        | BytesGet(t1, t2, t3) -> [t1, t2, t3]
        | BytesIndexOf(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | BytesCompare(t1, t2, t3) -> [t1, t2, t3]
        | BytesScanHash(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | BytesSubText(t1, t2, t3, t4, _) -> [t1, t2, t3, t4]
        | BytesSubView(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | BytesAppend(t1, t2, t3, _) -> [t1, t2, t3]
        | BytesAppendByte(t1, t2, t3, _) -> [t1, t2, t3]
        | BytesAllocate(t1, t2, _) -> [t1, t2]
        | BytesCopyRange(t1, t2, t3, t4, t5, t6, _, _) -> [t1, t2, t3, t4, t5, t6]
        | BytesSet(t1, t2, t3, t4, _, _) -> [t1, t2, t3, t4]
        | BytesSetU16Le(t1, t2, t3, t4, _, _) -> [t1, t2, t3, t4]
        | BytesSetU32Le(t1, t2, t3, t4, _, _) -> [t1, t2, t3, t4]
        | BytesSetU64Le(t1, t2, t3, t4, _, _) -> [t1, t2, t3, t4]
        | BytesFromList(t1, t2, _) -> [t1, t2]
        | BytesHash(t1, t2) -> [t1, t2]
        | BytesU16Le(t1, t2, _) -> [t1, t2]
        | BytesU32Le(t1, t2, _) -> [t1, t2]
        | BytesU64Le(t1, t2, _) -> [t1, t2]
        | BytesGetU16Le(t1, t2, t3) -> [t1, t2, t3]
        | BytesGetU32Le(t1, t2, t3) -> [t1, t2, t3]
        | BytesGetU64Le(t1, t2, t3) -> [t1, t2, t3]
        | FileWriteBytes(t1, t2, t3) -> [t1, t2, t3]
        | SpawnProcess(t1, t2, t3) -> [t1, t2, t3]
        | ProcessWriteStdin(t1, t2, t3) -> [t1, t2, t3]
        | ProcessReadStdoutLine(t1, t2) -> [t1, t2]
        | ProcessReadStderrLine(t1, t2) -> [t1, t2]
        | ProcessWaitForExit(t1, t2) -> [t1, t2]
        | ProcessKill(t1, t2) -> [t1, t2]
        | CleanupResource(t, _, _) -> [t]
        | RcDrop(t, _, _, _, _, _) -> [t]
        | RcDup(t1, t2, _, _) -> [t1, t2]
        | RcIsUnique(t1, t2) -> [t1, t2]
        | Borrow(t1, t2) -> [t1, t2]
        | TcoResetPending(_, temps, _) -> temps
        | SaveArenaState(_, _, _) -> []
        | RestoreArenaState(_, _, _, _) -> []
        | ReclaimArenaChunks(_, _, _) -> []
        | CopyOutArena(t1, t2, _, _, _, _) -> [t1, t2]
        | CopyOutArenaToSpace(t1, t2, _) -> [t1, t2]
        | CopyFixedInto(t1, t2, _) -> [t1, t2]
        | CopyStringIntoOrFresh(t1, t2, t3) -> [t1, t2, t3]
        | CopyFixedIntoOrFresh(t1, t2, t3, _) -> [t1, t2, t3]
        | CopyOutList(t1, t2, _, _, _) -> [t1, t2]
        | CopyOutClosure(t1, t2, _, _) -> [t1, t2]
        | CopyOutTcoListCell(t1, t2, _, _) -> [t1, t2]
        | ToCString(t1, t2) -> [t1, t2]
        | AllocFfiOut(t, _) -> [t]
        | LoadFfiOut(t1, t2, _) -> [t1, t2]
        | CopyFfiString(t1, t2, _) -> [t1, t2]
        | CopyFfiBytes(t1, t2, t3) -> [t1, t2, t3]
        | CallExternal(t, _, _, args, _, _) -> t :: args
        | CreateTask(t1, t2, _, _, _, _) -> [t1, t2]
        | CreateCompletedTask(t1, t2) -> [t1, t2]
        | AwaitTask(t1, t2) -> [t1, t2]
        | RunTask(t1, t2) -> [t1, t2]
        | SpawnTask(t1, t2) -> [t1, t2]
        | CreateTaskScope(t, _) -> [t]
        | CreateScopedTask(t1, t2, t3) -> [t1, t2, t3]
        | ForkScopedTask(t1, t2, t3) -> [t1, t2, t3]
        | JoinScopedTask(t1, t2) -> [t1, t2]
        | ParallelFork(t1, t2) -> [t1, t2]
        | ParallelJoin(t1, t2) -> [t1, t2]
        | ParallelCleanup(t) -> [t]
        | LoadParallelWorkerOverride(t) -> [t]
        | StoreParallelWorkerOverride(t) -> [t]
        | ParallelQueueStart(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | ParallelQueueAwait(t1, t2) -> [t1, t2]
        | ParallelQueueCleanup(t) -> [t]
        | Suspend(t1, _, t2, saves) ->
            t1 :: t2 :: map(given (save) ->
                match save with
                    | IrFrameSave { sourceTemp = st } -> st)(saves)
        | Resume(t1, t2, restores) ->
            t1 :: t2 :: map(given (restore) ->
                match restore with
                    | IrFrameRestore { targetTemp = tt } -> tt)(restores)
        | AsyncSleep(t1, t2) -> [t1, t2]
        | CreateTcpConnectTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTcpSendTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTcpReceiveTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTcpCloseTask(t1, t2) -> [t1, t2]
        | CreateTcpListenTask(t1, t2) -> [t1, t2]
        | CreateForkWorkersTask(t1, t2, t3) -> [t1, t2, t3]
        | SetDrainTimeout(t1, t2) -> [t1, t2]
        | RequestServerStop(t1) -> [t1]
        | CreateTcpAcceptTask(t1, t2) -> [t1, t2]
        | CreateHttpGetTask(t1, t2) -> [t1, t2]
        | CreateHttpPostTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTlsConnectTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTlsHandshakeTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTlsServerHandshakeTask(t1, t2, t3, t4) -> [t1, t2, t3, t4]
        | CreateTlsSendTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTlsReceiveTask(t1, t2, t3) -> [t1, t2, t3]
        | CreateTlsCloseTask(t1, t2) -> [t1, t2]
        | AsyncAll(t1, t2) -> [t1, t2]
        | AsyncRace(t1, t2) -> [t1, t2]
        | PanicStr(t) -> [t]
        | LoadCapabilityHandler(t, _) -> [t]
        | StoreCapabilityHandler(_, t) -> [t]
        | Label(_) -> []
        | Jump(_) -> []
        | JumpIfFalse(t, _) -> [t]
        | SwitchTag(t, _, _) -> [t]
        | Return(t) -> [t]

let collectDefinedLabels instructions =
    (let recursive collect acc insts =
        match insts with
            | [] -> acc
            | IrInstruction { instruction = kind } :: tail ->
                match instructionDefinedLabel(kind) with
                    | Some(label) -> collect(label :: acc)(tail)
                    | None -> collect(acc)(tail)
    in collect([])(instructions))

let recursive validateInstructionLocals functionLabel instIndex localCount slots =
    match slots with
        | [] -> []
        | slot :: tail ->
            let rest = validateInstructionLocals(functionLabel)(instIndex)(localCount)(tail)
            in
                let outOfBounds =
                    if slot < 0
                    then true
                    else slot >= localCount
                in
                    if outOfBounds
                    then
                        validationError(
                            "local slot " + Ashes.Text.fromInt(slot) + " out of bounds [0, " + Ashes.Text.fromInt(localCount) + ")"
                        )(
                            Some(functionLabel)
                        )(
                            Some(instIndex)
                        ) :: rest
                    else rest

let recursive validateInstructionTemps functionLabel instIndex tempCount temps =
    match temps with
        | [] -> []
        | temp :: tail ->
            let rest = validateInstructionTemps(functionLabel)(instIndex)(tempCount)(tail)
            in
                if temp < 0
                then
                    if temp == -1
                    then rest
                    else
                        validationError(
                            "negative temp register " + Ashes.Text.fromInt(temp)
                        )(
                            Some(functionLabel)
                        )(
                            Some(instIndex)
                        ) :: rest
                else
                    if temp >= tempCount
                    then
                        validationError(
                            "temp register " + Ashes.Text.fromInt(temp) + " out of bounds [0, " + Ashes.Text.fromInt(tempCount) + ")"
                        )(
                            Some(functionLabel)
                        )(
                            Some(instIndex)
                        ) :: rest
                    else rest

let recursive validateInstructionTargets functionLabel instIndex definedLabels targets =
    match targets with
        | [] -> []
        | target :: tail ->
            let rest = validateInstructionTargets(functionLabel)(instIndex)(definedLabels)(tail)
            in
                if containsText(target)(definedLabels)
                then rest
                else
                    validationError(
                        "undefined jump target label '" + target + "'"
                    )(
                        Some(functionLabel)
                    )(
                        Some(instIndex)
                    ) :: rest

let validateStringLiteralReference functionLabel instIndex knownStringLabels (kind: IrInstructionKind) =
    match kind with
        | LoadConstStr(_, label) ->
            if label == ""
            then []
            else
                if containsText(label)(knownStringLabels)
                then []
                else
                    [
                        validationError(
                            "unknown string literal label '" + label + "'"
                        )(
                            Some(functionLabel)
                        )(
                            Some(instIndex)
                        )
                    ]
        | _ -> []

let validateSingleInstruction functionLabel instIndex localCount tempCount definedLabels knownStringLabels (inst: IrInstruction) =
    match inst with
        | IrInstruction { instruction = kind } ->
            let locals = instructionLocals(kind)
            in
                let temps = instructionTemps(kind)
                in
                    let targets = instructionTargetLabels(kind)
                    in
                        let localIssues = validateInstructionLocals(functionLabel)(instIndex)(localCount)(locals)
                        in
                            let tempIssues = validateInstructionTemps(functionLabel)(instIndex)(tempCount)(temps)
                            in
                                let targetIssues = validateInstructionTargets(functionLabel)(instIndex)(definedLabels)(targets)
                                in
                                    let strIssues = validateStringLiteralReference(functionLabel)(instIndex)(knownStringLabels)(kind)
                                    in append(localIssues)(append(tempIssues)(append(targetIssues)(strIssues)))

let recursive validateInstructions functionLabel instIndex localCount tempCount definedLabels knownStringLabels instructions =
    match instructions with
        | [] -> []
        | inst :: tail ->
            let currentIssues = validateSingleInstruction(functionLabel)(instIndex)(localCount)(tempCount)(definedLabels)(knownStringLabels)(inst)
            in
                let restIssues = validateInstructions(functionLabel)(instIndex + 1)(localCount)(tempCount)(definedLabels)(knownStringLabels)(tail)
                in append(currentIssues)(restIssues)

let validateCoroutineInfo functionLabel (coroutine: Maybe(CoroutineInfo)) =
    match coroutine with
        | None -> []
        | Some(CoroutineInfo { stateCount = sc, stateStructSize = sss, captureCount = cc }) ->
            let scIssue =
                if sc < 0
                then [validationError("negative coroutine state count " + Ashes.Text.fromInt(sc))(Some(functionLabel))(None)]
                else []
            in
                let sssIssue =
                    if sss < 0
                    then [validationError("negative coroutine state struct size " + Ashes.Text.fromInt(sss))(Some(functionLabel))(None)]
                    else []
                in
                    let ccIssue =
                        if cc < 0
                        then [validationError("negative coroutine capture count " + Ashes.Text.fromInt(cc))(Some(functionLabel))(None)]
                        else []
                    in append(scIssue)(append(sssIssue)(ccIssue))

let recursive validateDebugLocalNames functionLabel localCount localNames =
    match localNames with
        | [] -> []
        | (slot, name) :: tail ->
            let rest = validateDebugLocalNames(functionLabel)(localCount)(tail)
            in
                let outOfBounds =
                    if slot < 0
                    then true
                    else slot >= localCount
                in
                    if outOfBounds
                    then
                        validationError(
                            "debug local name slot " + Ashes.Text.fromInt(slot) + " out of bounds [0, " + Ashes.Text.fromInt(localCount) + ")"
                        )(
                            Some(functionLabel)
                        )(
                            None
                        ) :: rest
                    else rest

let recursive validateDebugLocalTypes functionLabel localCount localTypes =
    match localTypes with
        | [] -> []
        | (slot, _) :: tail ->
            let rest = validateDebugLocalTypes(functionLabel)(localCount)(tail)
            in
                let outOfBounds =
                    if slot < 0
                    then true
                    else slot >= localCount
                in
                    if outOfBounds
                    then
                        validationError(
                            "debug local type slot " + Ashes.Text.fromInt(slot) + " out of bounds [0, " + Ashes.Text.fromInt(localCount) + ")"
                        )(
                            Some(functionLabel)
                        )(
                            None
                        ) :: rest
                    else rest

let validateIrFunction stringLiterals (func: IrFunction) =
    match func with
        | IrFunction { label = label, instructions = instructions, localCount = localCount, tempCount = tempCount, coroutine = coroutine, localNames = localNames, localTypes = localTypes } ->
            let headerIssues =
                let labelIssue =
                    if label == ""
                    then [validationError("function label cannot be empty")(None)(None)]
                    else []
                in
                    let localCountIssue =
                        if localCount < 0
                        then [validationError("negative localCount " + Ashes.Text.fromInt(localCount))(Some(label))(None)]
                        else []
                    in
                        let tempCountIssue =
                            if tempCount < 0
                            then [validationError("negative tempCount " + Ashes.Text.fromInt(tempCount))(Some(label))(None)]
                            else []
                        in append(labelIssue)(append(localCountIssue)(tempCountIssue))
            in
                let definedLabels = collectDefinedLabels(instructions)
                in
                    let duplicateLabels = findDuplicateTexts([])(definedLabels)
                    in
                        let duplicateLabelIssues =
                            map(given (dup) -> validationError("duplicate instruction label '" + dup + "' in function '" + label + "'")(Some(label))(None))(duplicateLabels)
                        in
                            let knownStringLabels =
                                map(given (lit) ->
                                    match lit with
                                        | IrStringLiteral { label = l } -> l)(stringLiterals)
                            in
                                let instructionIssues = validateInstructions(label)(0)(localCount)(tempCount)(definedLabels)(knownStringLabels)(instructions)
                                in
                                    let coroutineIssues = validateCoroutineInfo(label)(coroutine)
                                    in
                                        let debugNameIssues = validateDebugLocalNames(label)(localCount)(localNames)
                                        in
                                            let debugTypeIssues = validateDebugLocalTypes(label)(localCount)(localTypes)
                                            in
                                                append(headerIssues)(
                                                    append(duplicateLabelIssues)(
                                                        append(instructionIssues)(
                                                            append(coroutineIssues)(append(debugNameIssues)(debugTypeIssues))
                                                        )
                                                    )
                                                )

let validateIrProgram (program: IrProgram) =
    match program with
        | IrProgram { entryFunction = entryFunction, functions = functions, stringLiterals = stringLiterals, capabilityHandlerGlobals = capGlobals } ->
            let entryIssues =
                match entryFunction with
                    | IrFunction { label = entryLabel, hasEnvAndArgParams = hasEnv } ->
                        let labelIssue =
                            if entryLabel == ""
                            then [validationError("entry function label cannot be empty")(None)(None)]
                            else []
                        in
                            let envIssue =
                                if hasEnv
                                then [validationError("entry function cannot have env and arg params")(Some(entryLabel))(None)]
                                else []
                            in append(labelIssue)(envIssue)
            in
                let allFunctions = entryFunction :: functions
                in
                    let functionLabels =
                        map(given (f) ->
                            match f with
                                | IrFunction { label = l } -> l)(allFunctions)
                    in
                        let duplicateFuncLabels = findDuplicateTexts([])(functionLabels)
                        in
                            let duplicateFuncIssues =
                                map(given (dup) -> validationError("duplicate function label '" + dup + "' in IR program")(Some(dup))(None))(duplicateFuncLabels)
                            in
                                let stringLabels =
                                    map(given (s) ->
                                        match s with
                                            | IrStringLiteral { label = l } -> l)(stringLiterals)
                                in
                                    let duplicateStringLabels = findDuplicateTexts([])(stringLabels)
                                    in
                                        let duplicateStringIssues =
                                            map(given (dup) -> validationError("duplicate string literal label '" + dup + "' in IR program")(None)(None))(duplicateStringLabels)
                                        in
                                            let capGlobalIssues =
                                                if capGlobals < 0
                                                then [validationError("negative capabilityHandlerGlobals " + Ashes.Text.fromInt(capGlobals))(None)(None)]
                                                else []
                                            in
                                                let recursive validateAllFunctions funcs =
                                                    match funcs with
                                                        | [] -> []
                                                        | func :: tail ->
                                                            let fIssues = validateIrFunction(stringLiterals)(func)
                                                            in
                                                                let tailIssues = validateAllFunctions(tail)
                                                                in append(fIssues)(tailIssues)
                                                in
                                                    let functionIssues = validateAllFunctions(allFunctions)
                                                    in
                                                        let allIssues =
                                                            append(entryIssues)(
                                                                append(duplicateFuncIssues)(
                                                                    append(duplicateStringIssues)(append(capGlobalIssues)(functionIssues))
                                                                )
                                                            )
                                                        in
                                                            IrValidationReport(
                                                                issues = allIssues,
                                                                isValid = length(allIssues) == 0
                                                            )

let isIrProgramValid (program: IrProgram) =
    match validateIrProgram(program) with
        | IrValidationReport { isValid = valid } -> valid

let assertValidIrProgram (program: IrProgram) =
    match validateIrProgram(program) with
        | IrValidationReport { isValid = true } -> Ok(program)
        | IrValidationReport { issues = issues } -> Error(issues)
