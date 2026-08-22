import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Test as test
import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.TaskStructLayout
import AshesCompiler.Semantics.Types
export (
    value runIrTests,
)

let coreInstructions =
    [
        LoadConstInt(0)(1),
        LoadConstFloat(0)(1.0),
        LoadConstBool(0)(true),
        LoadConstStr(0)("str_0"),
        LoadProgramArgs(0),
        LoadLocal(0)(0),
        StoreLocal(0)(0),
        LoadEnv(0)(0),
        StoreMemOffset(0)(8)(1),
        LoadMemOffset(0)(1)(8),
        AddInt(0)(1)(2),
        SubInt(0)(1)(2),
        MulInt(0)(1)(2),
        DivInt(0)(1)(2),
        DivUInt(0)(1)(2),
        AndInt(0)(1)(2),
        OrInt(0)(1)(2),
        XorInt(0)(1)(2),
        ShlInt(0)(1)(2),
        ShrInt(0)(1)(2),
        AddFloat(0)(1)(2),
        SubFloat(0)(1)(2),
        MulFloat(0)(1)(2),
        DivFloat(0)(1)(2),
        CmpIntGt(0)(1)(2),
        CmpIntGe(0)(1)(2),
        CmpIntLt(0)(1)(2),
        CmpIntLe(0)(1)(2),
        CmpUIntGt(0)(1)(2),
        CmpUIntGe(0)(1)(2),
        CmpUIntLt(0)(1)(2),
        CmpUIntLe(0)(1)(2),
        CmpIntEq(0)(1)(2),
        CmpIntNe(0)(1)(2),
        CmpFloatGt(0)(1)(2),
        CmpFloatGe(0)(1)(2),
        CmpFloatLt(0)(1)(2),
        CmpFloatLe(0)(1)(2),
        CmpFloatEq(0)(1)(2),
        CmpFloatNe(0)(1)(2),
        IntToFloat(0)(1),
        FloatToInt(0)(1),
        FloatUnaryIntrinsic(0)(1)("llvm.sqrt.f64"),
        CallLibm(0)("sin")([1]),
        BigIntFromInt(0)(1)(false),
        BigIntToString(0)(1)(true),
        BigIntToInt(0)(1)(true),
        BigIntFromString(0)(1)(true),
        BigIntBinary(0)(1)(2)("add")(true),
        BigIntCompare(0)(1)(2),
        CmpStrEq(0)(1)(2),
        CmpStrNe(0)(1)(2),
        ConcatStr(0)(1)(2)(true),
        ConcatStrTip(0)(1)(2)(0)(1)(true),
        RegexCompile(0)(1),
        RegexCompileError(0)(1),
        RegexFind(0)(1)(2)(3),
        RegexCaptures(0)(1)(2)(3),
        RegexSubstitute(0)(1)(2)(3),
        MakeClosure(0)("lambda_0")(1)(16)(true)(true)(true),
        MakeClosureStack(0)("lambda_0")(1)(16)(true)(true),
        LoadFuncAddr(0)("lambda_0"),
        CallClosure(0)(1)(2)(3),
        CallKnown(0)("lambda_0")(1)(2)(3)(false),
        LoadArgumentOwnership(0),
        Alloc(0)(16)(true),
        AllocStack(0)(16),
        AllocAdt(0)(1)(2)(true),
        AllocAdtStack(0)(1)(2),
        AllocAdtToSpace(0)(1)(2),
        DropReuse(0)(1)(2)(true),
        AllocReusing(0)(1)(2)(3)(true)(false),
        SetAdtField(0)(1)(2),
        SaveStackPointer(0),
        RestoreStackPointer(0),
        GetAdtTag(0)(1),
        GetAdtField(0)(1)(2)
    ]

let ioInstructions =
    [
        PrintInt(0),
        PrintStr(0),
        PrintBool(0),
        WriteStr(0),
        WriteErrorStr(0)(true),
        ExitProcess(0),
        WriteBufferedStr(0)(true),
        FlushStdout,
        ReadLine(0),
        ReadExact(0)(1),
        ConsoleEnableRaw(0),
        ConsoleRestore(0),
        ConsolePoll(0)(1),
        MonotonicMillis(0),
        TextByteLength(0)(1),
        FileReadText(0)(1),
        FileReadAllBytes(0)(1),
        FileMmap(0)(1),
        FileWriteText(0)(1)(2),
        FileExists(0)(1),
        FileReplace(0)(1)(2),
        FileMakeExecutable(0)(1),
        DirectoryEntries(0)(1),
        DirectoryCreateAll(0)(1),
        DirectoryRemoveTree(0)(1),
        EnvironmentDirectory(0)(CurrentDirectory),
        EnvironmentGet(0)(1),
        FileOpen(0)(1),
        FileReadChunk(0)(1)(2),
        FileReadLine(0)(1),
        FileClose(0)(1),
        TextUncons(0)(1)(true),
        TextUnconsText(0)(1)(true),
        RuneToText(0)(1)(true),
        RuneFromInt(0)(1)(true),
        TextParseInt(0)(1)(true),
        TextParseFloat(0)(1)(true),
        TextFromInt(0)(1)(true),
        TextFromFloat(0)(1)(true),
        TextFormatFloat(0)(1)(2)(true),
        TextToHex(0)(1)(true),
        TextAsciiCase(0)(1)(true)(true),
        HttpGet(0)(1),
        HttpPost(0)(1)(2),
        NetTcpConnect(0)(1)(2),
        NetTcpSend(0)(1)(2),
        NetTcpReceive(0)(1)(2),
        NetTcpClose(0)(1),
        NetTcpListen(0)(1),
        NetTcpAccept(0)(1),
        BytesEmpty(0)(true),
        BytesSingleton(0)(1)(true),
        BytesLength(0)(1),
        BytesGet(0)(1)(2),
        BytesIndexOf(0)(1)(2)(3),
        BytesCompare(0)(1)(2),
        BytesScanHash(0)(1)(2)(3),
        BytesSubText(0)(1)(2)(3)(true),
        BytesSubView(0)(1)(2)(3),
        BytesAppend(0)(1)(2)(true),
        BytesAppendByte(0)(1)(2)(true),
        BytesAllocate(0)(1)(true),
        BytesCopyRange(0)(1)(2)(3)(4)(5)(true)(true),
        BytesSet(0)(1)(2)(3)(true)(true),
        BytesSetU16Le(0)(1)(2)(3)(true)(true),
        BytesSetU32Le(0)(1)(2)(3)(true)(true),
        BytesSetU64Le(0)(1)(2)(3)(true)(true),
        BytesFromList(0)(1)(true),
        BytesHash(0)(1),
        BytesU16Le(0)(1)(true),
        BytesU32Le(0)(1)(true),
        BytesU64Le(0)(1)(true),
        BytesGetU16Le(0)(1)(2),
        BytesGetU32Le(0)(1)(2),
        BytesGetU64Le(0)(1)(2),
        FileWriteBytes(0)(1)(2),
        SpawnProcess(0)(1)(2),
        ProcessWriteStdin(0)(1)(2),
        ProcessReadStdoutLine(0)(1),
        ProcessReadStderrLine(0)(1),
        ProcessWaitForExit(0)(1),
        ProcessKill(0)(1)
    ]

let ownershipInstructions =
    [
        CleanupResource(0)("FileHandle")(None),
        RcDrop(0)("List(Int)")(1)(true)(true)(Some("drop_list_int")),
        RcDup(0)(1)(true)(true),
        RcIsUnique(0)(1),
        Borrow(0)(1),
        TcoResetPending(1)([0, 1])([2]),
        SaveArenaState(0)(1)(true),
        RestoreArenaState(0)(1)(2)(true),
        ReclaimArenaChunks(1)(2)(true),
        CopyOutArena(0)(1)(16)(true)(RcNormalization)(Some(SemInt)),
        CopyOutArenaToSpace(0)(1)(16),
        CopyFixedInto(0)(1)(16),
        CopyStringIntoOrFresh(0)(1)(2),
        CopyFixedIntoOrFresh(0)(1)(2)(16),
        CopyOutList(0)(1)(StringListHead)(true)(ArenaScopeBoundary),
        CopyOutClosure(0)(1)(true)(ArenaCallBoundary),
        CopyOutTcoListCell(0)(1)(InnerListHead)(ArenaTcoCompaction),
        ToCString(0)(1),
        AllocFfiOut(0)(ExternalAbiOpaque("Handle")),
        LoadFfiOut(0)(1)(ExternalAbiOpaque("Handle")),
        None
        |> ExternalAbiNativeString(false)(ExternalNativeStringBorrowed)
        |> CopyFfiString(0)(1),
        CopyFfiBytes(0)(1)(2),
        CallExternal(0)("native")(None)([1])([ExternalAbiInt])(ExternalAbiVoid)
    ]

let taskInstructions =
    [
        CreateTask(0)(1)(176)(2)(Some("drop_frame"))(true),
        CreateCompletedTask(0)(1),
        AwaitTask(0)(1),
        RunTask(0)(1),
        SpawnTask(0)(1),
        CreateTaskScope(0)(true),
        CreateScopedTask(0)(1)(2),
        ForkScopedTask(0)(1)(2),
        JoinScopedTask(0)(1),
        ParallelFork(0)(1),
        ParallelJoin(0)(1),
        ParallelCleanup(0),
        LoadParallelWorkerOverride(0),
        StoreParallelWorkerOverride(0),
        ParallelQueueStart(0)(1)(2)(3),
        ParallelQueueAwait(0)(1),
        ParallelQueueCleanup(0),
        Suspend(0)(1)(2)([IrFrameSave(slotOffset = 160, sourceTemp = 3)]),
        Resume(0)(1)([IrFrameRestore(slotOffset = 160, targetTemp = 3)]),
        AsyncSleep(0)(1),
        CreateTcpConnectTask(0)(1)(2),
        CreateTcpSendTask(0)(1)(2),
        CreateTcpReceiveTask(0)(1)(2),
        CreateTcpCloseTask(0)(1),
        CreateTcpListenTask(0)(1),
        CreateForkWorkersTask(0)(1)(2),
        SetDrainTimeout(0)(1),
        RequestServerStop(0),
        CreateTcpAcceptTask(0)(1),
        CreateHttpGetTask(0)(1),
        CreateHttpPostTask(0)(1)(2),
        CreateTlsConnectTask(0)(1)(2),
        CreateTlsHandshakeTask(0)(1)(2),
        CreateTlsServerHandshakeTask(0)(1)(2)(3),
        CreateTlsSendTask(0)(1)(2),
        CreateTlsReceiveTask(0)(1)(2),
        CreateTlsCloseTask(0)(1),
        AsyncAll(0)(1),
        AsyncRace(0)(1)
    ]

let controlInstructions =
    [
        PanicStr(0),
        LoadCapabilityHandler(0)(1),
        StoreCapabilityHandler(1)(0),
        Label("case_0"),
        Jump("done"),
        JumpIfFalse(0)("case_1"),
        SwitchTag(0)([IrSwitchCase(tag = 1, label = "case_1")])("default"),
        Return(0)
    ]

let expectCompleteInstructionInventory unit =
    unit
    |> (given (_) ->
        coreInstructions
        |> length
        |> test.assertEqual(77))
    |> (given (_) ->
        ioInstructions
        |> length
        |> test.assertEqual(82))
    |> (given (_) ->
        ownershipInstructions
        |> length
        |> test.assertEqual(23))
    |> (given (_) ->
        taskInstructions
        |> length
        |> test.assertEqual(39))
    |> (given (_) ->
        controlInstructions
        |> length
        |> test.assertEqual(8))

let sourceOrigin =
    SourceFunctionOrigin(
        functionSourceName = "map",
        functionQualifiedName = Some("Ashes.Collection.List.map"),
        declarationLocation = Some(IrSourceLocation(
            filePath = "/lib/Ashes/Collection.List.ash",
            line = 20,
            column = 5
        )),
        declarationOffset = 412
    )

let liftedOrigin =
    IrFunctionOrigin(
        generatedLabel = "map_lambda_1",
        originKind = ClosureHelperOrigin,
        sourceOrigin = Some(sourceOrigin),
        parentGeneratedLabel = Some("map"),
        compilerOwner = None,
        stableDiscriminator = Some("curried-layer:1"),
        generationLocation = None
    )

let unlocatedInstruction value =
    IrInstruction(
        instruction = value,
        location = None
    )

let awaitInstruction =
    IrInstruction(
        instruction = AwaitTask(0)(1),
        location = Some(IrSourceLocation(
            filePath = "/lib/Ashes/Collection.List.ash",
            line = 22,
            column = 9
        ))
    )

let coroutineFunction =
    IrFunction(
        label = "map_lambda_1",
        instructions = [
            awaitInstruction,
            unlocatedInstruction(Return(0))
        ],
        localCount = 2,
        tempCount = 2,
        hasEnvAndArgParams = true,
        coroutine = Some(CoroutineInfo(
            stateCount = 2,
            stateStructSize = 176,
            captureCount = 1
        )),
        localNames = [(0, "environment"), (1, "argument")],
        localTypes = [(1, SemList(SemInt))],
        origin = Some(liftedOrigin),
        lifetimesPlaced = true
    )

let modelProgram =
    IrProgram(
        entryFunction = coroutineFunction,
        functions = [],
        stringLiterals = [IrStringLiteral(label = "str_0", value = "done")],
        externalFunctions = [],
        externalOpaqueTypes = ["FileHandle"],
        usesPrintInt = false,
        usesPrintStr = true,
        usesPrintBool = false,
        usesConcatStr = false,
        usesClosures = true,
        usesAsync = true,
        capabilityHandlerGlobals = 2,
        traitEvidence = emptyTraitEvidenceAnnotations
    )

let programEntryFunction value =
    match value with
        | IrProgram { entryFunction = entryFunction } -> entryFunction

let functionOrigin value =
    match value with
        | IrFunction { origin = origin } -> origin

let functionCoroutine value =
    match value with
        | IrFunction { coroutine = coroutine } -> coroutine

let firstInstructionLocation value =
    match value with
        | IrFunction { instructions = IrInstruction { location = location } :: _rest } -> location
        | IrFunction { instructions = [] } -> None

let programStringLiterals value =
    match value with
        | IrProgram { stringLiterals = literals } -> literals

let programExternalOpaqueTypes value =
    match value with
        | IrProgram { externalOpaqueTypes = opaqueTypes } -> opaqueTypes

let programUsesAsync value =
    match value with
        | IrProgram { usesAsync = usesAsync } -> usesAsync

let programCapabilityHandlerGlobals value =
    match value with
        | IrProgram { capabilityHandlerGlobals = count } -> count

let expectProgramAndOriginMetadata unit =
    unit
    |> (given (_) ->
        modelProgram
        |> programEntryFunction
        |> functionOrigin
        |> test.assertEqual(Some(liftedOrigin)))
    |> (given (_) ->
        modelProgram
        |> programEntryFunction
        |> functionCoroutine
        |> test.assertEqual(Some(CoroutineInfo(
            stateCount = 2,
            stateStructSize = 176,
            captureCount = 1
        ))))
    |> (given (_) ->
        coroutineFunction
        |> firstInstructionLocation
        |> test.assertEqual(Some(IrSourceLocation(
            filePath = "/lib/Ashes/Collection.List.ash",
            line = 22,
            column = 9
        ))))
    |> (given (_) ->
        modelProgram
        |> programStringLiterals
        |> test.assertEqual([IrStringLiteral(label = "str_0", value = "done")]))
    |> (given (_) ->
        modelProgram
        |> programExternalOpaqueTypes
        |> test.assertEqual(["FileHandle"]))
    |> (given (_) ->
        modelProgram
        |> programUsesAsync
        |> test.assertEqual(true))
    |> (given (_) ->
        modelProgram
        |> programCapabilityHandlerGlobals
        |> test.assertEqual(2))

let taskHeaderSize layout =
    match layout with
        | TaskStructLayout { headerSize = headerSize } -> headerSize

let expectCoroutineRuntimeMetadata unit =
    unit
    |> (given (_) ->
        taskStructLayout
        |> taskHeaderSize
        |> test.assertEqual(160))
    |> (given (_) ->
        [
            CompletedTaskState,
            TlsServerHandshakeTaskState,
            AllCompositeTaskState,
            RaceCompositeTaskState,
            ScopeCompositeTaskState
        ]
        |> map(taskStateCode)
        |> test.assertEqual([-1, -24, -40, -41, -42]))
    |> (given (_) ->
        [
            NoTaskWait,
            SocketReadTaskWait,
            SocketWriteTaskWait,
            TlsReadTaskWait,
            TlsWriteTaskWait,
            TimerTaskWait
        ]
        |> map(taskWaitCode)
        |> test.assertEqual([0, 1, 2, 3, 4, 5]))

let runIrTests unit =
    unit
    |> expectCompleteInstructionInventory
    |> expectProgramAndOriginMetadata
    |> expectCoroutineRuntimeMetadata
    |> (given (_) -> Ashes.IO.print("all self-hosted IR model tests passed"))
