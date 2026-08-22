import Ashes.Test as test
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.Types
export (
    value runCoreBuiltinLoweringTests,
)

let unaryArguments = [1]

let unaryTypes = [SemInt]

let binaryArguments = [1, 2]

let binaryTypes = [SemInt, SemInt]

let ternaryArguments = [1, 2, 3]

let ternaryTypes = [SemInt, SemInt, SemInt]

let builtinCases =
    [
        (CoreProgramArgs, [], []),
        (CorePrint, unaryArguments, unaryTypes),
        (CorePanic, unaryArguments, unaryTypes),
        (CoreWrite, unaryArguments, unaryTypes),
        (CoreWriteBytes, unaryArguments, unaryTypes),
        (CoreWriteLine, unaryArguments, unaryTypes),
        (CoreWriteError(false), unaryArguments, unaryTypes),
        (CoreExit, unaryArguments, unaryTypes),
        (CoreWriteBuffered(true), unaryArguments, unaryTypes),
        (CoreFlushStdout, unaryArguments, unaryTypes),
        (CoreReadLine, unaryArguments, unaryTypes),
        (CoreReadExact, unaryArguments, unaryTypes),
        (CoreConsoleEnableRaw, unaryArguments, unaryTypes),
        (CoreConsoleRestore, unaryArguments, unaryTypes),
        (CoreConsolePoll, unaryArguments, unaryTypes),
        (CoreMonotonicMillis, unaryArguments, unaryTypes),
        (CoreTextByteLength, unaryArguments, unaryTypes),
        (CoreFileReadText, unaryArguments, unaryTypes),
        (CoreFileReadAllBytes, unaryArguments, unaryTypes),
        (CoreFileMmap, unaryArguments, unaryTypes),
        (CoreFileWriteText, binaryArguments, binaryTypes),
        (CoreFileWriteBytes, binaryArguments, binaryTypes),
        (CoreFileExists, unaryArguments, unaryTypes),
        (CoreFileReplace, binaryArguments, binaryTypes),
        (CoreFileMakeExecutable, unaryArguments, unaryTypes),
        (CoreFileOpen, unaryArguments, unaryTypes),
        (CoreFileReadChunk, binaryArguments, binaryTypes),
        (CoreFileReadLine, unaryArguments, unaryTypes),
        (CoreFileClose, unaryArguments, unaryTypes),
        (CoreDirectoryEntries, unaryArguments, unaryTypes),
        (CoreDirectoryCreateAll, unaryArguments, unaryTypes),
        (CoreDirectoryRemoveTree, unaryArguments, unaryTypes),
        (CoreEnvironmentDirectory(CurrentDirectory), unaryArguments, unaryTypes),
        (CoreEnvironmentGet, unaryArguments, unaryTypes),
        (CoreTextUncons(false), unaryArguments, unaryTypes),
        (CoreTextUncons(true), unaryArguments, unaryTypes),
        (CoreRuneToText, unaryArguments, unaryTypes),
        (CoreRuneToInt, unaryArguments, unaryTypes),
        (CoreRuneFromInt, unaryArguments, unaryTypes),
        (CoreRuneIsAsciiLetter, unaryArguments, unaryTypes),
        (CoreRuneIsAsciiDigit, unaryArguments, unaryTypes),
        (CoreRuneIsAsciiWhiteSpace, unaryArguments, unaryTypes),
        (CoreTextParseInt, unaryArguments, unaryTypes),
        (CoreTextParseFloat, unaryArguments, unaryTypes),
        (CoreTextFromInt, unaryArguments, unaryTypes),
        (CoreTextFromFloat, unaryArguments, unaryTypes),
        (CoreTextFormatFloat, binaryArguments, binaryTypes),
        (CoreTextToHex, unaryArguments, unaryTypes),
        (CoreTextAsciiCase(false), unaryArguments, unaryTypes),
        (CoreMathToFloat, unaryArguments, unaryTypes),
        (CoreMathFloatUnary("sqrt"), unaryArguments, unaryTypes),
        (CoreMathFloatToInt(None), unaryArguments, unaryTypes),
        (CoreMathFloatToInt(Some("floor")), unaryArguments, unaryTypes),
        (CoreMathLibm("pow"), binaryArguments, binaryTypes),
        (CoreBigIntFromInt, unaryArguments, unaryTypes),
        (CoreBigIntToString, unaryArguments, unaryTypes),
        (CoreBigIntToInt, unaryArguments, unaryTypes),
        (CoreBigIntFromString, unaryArguments, unaryTypes),
        (CoreBigIntBinary("add"), binaryArguments, binaryTypes),
        (CoreBigIntCompare, binaryArguments, binaryTypes),
        (CoreRegexCompile, unaryArguments, unaryTypes),
        (CoreRegexCompileError, unaryArguments, unaryTypes),
        (CoreRegexFind, ternaryArguments, ternaryTypes),
        (CoreRegexCaptures, ternaryArguments, ternaryTypes),
        (CoreRegexSubstitute, ternaryArguments, ternaryTypes),
        (CoreHttpGet, unaryArguments, unaryTypes),
        (CoreHttpPost, binaryArguments, binaryTypes),
        (CoreTcpConnect, binaryArguments, binaryTypes),
        (CoreTcpSend, binaryArguments, binaryTypes),
        (CoreTcpReceive, binaryArguments, binaryTypes),
        (CoreTcpClose, unaryArguments, unaryTypes),
        (CoreTcpListen, unaryArguments, unaryTypes),
        (CoreTcpAccept, unaryArguments, unaryTypes),
        (CoreTcpForkWorkers, binaryArguments, binaryTypes),
        (CoreTcpSetDrainTimeout, unaryArguments, unaryTypes),
        (CoreTlsConnect, binaryArguments, binaryTypes),
        (CoreTlsSend, binaryArguments, binaryTypes),
        (CoreTlsReceive, binaryArguments, binaryTypes),
        (CoreTlsClose, unaryArguments, unaryTypes),
        (CoreTlsServerHandshake, ternaryArguments, ternaryTypes),
        (CoreBytesEmpty, unaryArguments, unaryTypes),
        (CoreBytesSingleton, unaryArguments, unaryTypes),
        (CoreBytesLength, unaryArguments, unaryTypes),
        (CoreBytesGet, binaryArguments, binaryTypes),
        (CoreBytesIndexOf, ternaryArguments, ternaryTypes),
        (CoreBytesCompare, binaryArguments, binaryTypes),
        (CoreBytesScanHash, ternaryArguments, ternaryTypes),
        (CoreBytesSubText, ternaryArguments, ternaryTypes),
        (CoreBytesSubView, ternaryArguments, ternaryTypes),
        (CoreBytesAppend, binaryArguments, binaryTypes),
        (CoreBytesAppendByte, binaryArguments, binaryTypes),
        (CoreBytesAllocate, unaryArguments, unaryTypes),
        (CoreBytesCopyRange, [1, 2, 3, 4, 5], [SemInt, SemInt, SemInt, SemInt, SemInt]),
        (CoreBytesSet, ternaryArguments, ternaryTypes),
        (CoreBytesSetU16Le, ternaryArguments, ternaryTypes),
        (CoreBytesSetU32Le, ternaryArguments, ternaryTypes),
        (CoreBytesSetU64Le, ternaryArguments, ternaryTypes),
        (CoreBytesFromList, unaryArguments, unaryTypes),
        (CoreBytesFromText, unaryArguments, unaryTypes),
        (CoreBytesHash, unaryArguments, unaryTypes),
        (CoreBytesU16Le, unaryArguments, unaryTypes),
        (CoreBytesU32Le, unaryArguments, unaryTypes),
        (CoreBytesU64Le, unaryArguments, unaryTypes),
        (CoreBytesGetU16Le, binaryArguments, binaryTypes),
        (CoreBytesGetU32Le, binaryArguments, binaryTypes),
        (CoreBytesGetU64Le, binaryArguments, binaryTypes),
        (CoreUIntToInt, unaryArguments, unaryTypes),
        (CoreUIntFromInt, unaryArguments, unaryTypes),
        (CoreSpawnProcess, binaryArguments, binaryTypes),
        (CoreProcessWriteStdin, binaryArguments, binaryTypes),
        (CoreProcessReadStdoutLine, unaryArguments, unaryTypes),
        (CoreProcessReadStderrLine, unaryArguments, unaryTypes),
        (CoreProcessWaitForExit, unaryArguments, unaryTypes),
        (CoreProcessKill, unaryArguments, unaryTypes)
    ]

let expectBuiltinCase case unit =
    match case with
        | (kind, arguments, argumentTypes) ->
            match emitCoreBuiltin(kind)(20)(arguments)(argumentTypes) with
                | CoreBuiltinEmission { error = None } -> unit
                | CoreBuiltinEmission { error = Some(error) } -> test.fail("builtin lowering failed: " + error)

let recursive expectBuiltinCases cases unit =
    match cases with
        | [] -> unit
        | head :: tail ->
            unit
            |> expectBuiltinCase(head)
            |> expectBuiltinCases(tail)

let expectRepresentativeInstructions unit =
    unit
    |> (given (_) ->
        match emitCoreBuiltin(CoreRegexFind)(10)(ternaryArguments)(ternaryTypes) with
            | CoreBuiltinEmission { instructions = RegexFind(10, 1, 2, 3) :: [], error = None } -> Unit
            | _ -> test.fail("regex find did not emit its stage-0 instruction"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreTlsConnect)(10)(binaryArguments)(binaryTypes) with
            | CoreBuiltinEmission { instructions = CreateTlsConnectTask(10, 1, 2) :: [], error = None } -> Unit
            | _ -> test.fail("TLS connect did not emit its task instruction"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreTcpForkWorkers)(10)(binaryArguments)(binaryTypes) with
            | CoreBuiltinEmission { instructions = CreateForkWorkersTask(10, 2, 1) :: [], error = None } -> Unit
            | _ -> test.fail("TCP worker lowering did not preserve the stage-0 port/count operand order"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreBytesFromText)(10)(unaryArguments)(unaryTypes) with
            | CoreBuiltinEmission { instructions = [], nextTemp = 10, error = None } as emission ->
                match emission with
                    | CoreBuiltinEmission { result = CoreBuiltinTemp(1) } -> Unit
                    | _ -> test.fail("Bytes.fromText returned the wrong source temp")
            | _ -> test.fail("Bytes.fromText was not lowered as its zero-cost identity"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreUIntFromInt)(10)(unaryArguments)(unaryTypes) with
            | CoreBuiltinEmission { nextTemp = 12, result = CoreBuiltinTemp(11), error = None } as emission ->
                match emission with
                    | CoreBuiltinEmission { instructions = LoadConstInt(10, 255) :: AndInt(11, 1, 10) :: [] } -> Unit
                    | _ -> test.fail("UInt.fromInt emitted the wrong masking instructions")
            | _ -> test.fail("UInt.fromInt did not retain its stage-0 u8 mask"))

let expectArityFailure unit =
    match emitCoreBuiltin(CoreRegexFind)(10)(unaryArguments)(unaryTypes) with
        | CoreBuiltinEmission { error = Some(_) } -> unit
        | _ -> test.fail("builtin lowering accepted the wrong arity")

let expectBuiltinRegistry unit =
    unit
    |> (given (_) ->
        match coreBuiltinKind("Ashes.IO")("args") with
            | Some(CoreProgramArgs) -> Unit
            | _ -> test.fail("program arguments are absent from the core builtin registry"))
    |> (given (_) ->
        match coreBuiltinKind("Ashes.Number.Math")("ln") with
            | Some(CoreMathLibm("log")) -> Unit
            | _ -> test.fail("the math registry did not retain the stage-0 libm symbol"))
    |> (given (_) ->
        match coreBuiltinKind("Ashes.Internal.Regex")("capturesFrom") with
            | Some(CoreRegexCaptures) -> Unit
            | _ -> test.fail("regex captures are absent from the core builtin registry"))
    |> (given (_) ->
        match coreBuiltinKind("Ashes.Task")("run") with
            | None -> Unit
            | _ -> test.fail("the builtin registry consumed an operation owned by async lowering"))

let runCoreBuiltinLoweringTests unit =
    unit
    |> expectBuiltinCases(builtinCases)
    |> expectRepresentativeInstructions
    |> expectArityFailure
    |> expectBuiltinRegistry
    |> (given (_) -> Ashes.IO.print("all self-hosted core builtin lowering tests passed"))
