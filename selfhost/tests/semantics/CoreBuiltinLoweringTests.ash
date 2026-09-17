import Ashes.IO
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
        (CoreFfiCopyBytes, binaryArguments, binaryTypes),
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
            match emitCoreBuiltin(kind)(false)(20)(arguments)(argumentTypes) with
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
        match emitCoreBuiltin(CoreRegexFind)(false)(10)(ternaryArguments)(ternaryTypes) with
            | CoreBuiltinEmission { instructions = RegexFind(10, 1, 2, 3) :: [], error = None } -> Unit
            | _ -> test.fail("regex find did not emit its stage-0 instruction"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreTlsConnect)(false)(10)(binaryArguments)(binaryTypes) with
            | CoreBuiltinEmission { instructions = CreateTlsConnectTask(10, 1, 2) :: [], error = None } -> Unit
            | _ -> test.fail("TLS connect did not emit its task instruction"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreTcpForkWorkers)(false)(10)(binaryArguments)(binaryTypes) with
            | CoreBuiltinEmission { instructions = CreateForkWorkersTask(10, 2, 1) :: [], error = None } -> Unit
            | _ -> test.fail("TCP worker lowering did not preserve the stage-0 port/count operand order"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreBytesFromText)(false)(10)(unaryArguments)(unaryTypes) with
            | CoreBuiltinEmission { instructions = [], nextTemp = 10, error = None } as emission ->
                match emission with
                    | CoreBuiltinEmission { result = CoreBuiltinTemp(1) } -> Unit
                    | _ -> test.fail("Bytes.fromText returned the wrong source temp")
            | _ -> test.fail("Bytes.fromText was not lowered as its zero-cost identity"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreUIntFromInt)(false)(10)(unaryArguments)(unaryTypes) with
            | CoreBuiltinEmission { nextTemp = 12, result = CoreBuiltinTemp(11), error = None } as emission ->
                match emission with
                    | CoreBuiltinEmission { instructions = LoadConstInt(10, 255) :: AndInt(11, 1, 10) :: [] } -> Unit
                    | _ -> test.fail("UInt.fromInt emitted the wrong masking instructions")
            | _ -> test.fail("UInt.fromInt did not retain its stage-0 u8 mask"))
    |> (given (_) ->
        match emitCoreBuiltin(CoreFfiCopyBytes)(false)(10)(binaryArguments)(binaryTypes) with
            | CoreBuiltinEmission { instructions = CopyFfiBytes(10, 1, 2) :: [], nextTemp = 11, result = CoreBuiltinTemp(10), error = None } -> Unit
            | _ -> test.fail("Ffi.copyBytes did not lower to one CopyFfiBytes over its pointer and length"))

let expectArityFailure unit =
    match emitCoreBuiltin(CoreRegexFind)(false)(10)(unaryArguments)(unaryTypes) with
        | CoreBuiltinEmission { error = Some(_) } -> unit
        | _ -> test.fail("builtin lowering accepted the wrong arity")

let recursive builtinSchemeIn (layouts: List(CoreBuiltinLayout)) (moduleName: Str) (memberName: Str) =
    match layouts with
        | [] -> None
        | CoreBuiltinLayout { moduleName = candidateModule, memberName = candidateMember, scheme = scheme } :: rest ->
            if candidateModule == moduleName && candidateMember == memberName
            then Some(scheme)
            else builtinSchemeIn(rest)(moduleName)(memberName)

let builtinScheme (moduleName: Str) (memberName: Str) = builtinSchemeIn(standardBuiltinLayouts)(moduleName)(memberName)

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
        match coreBuiltinKind("Ashes.Internal")("deepCopy") with
            | Some(CoreInternalDeepCopy) -> Unit
            | _ -> test.fail("the deep copy intrinsic is absent from the core builtin registry"))
    |> (given (_) ->
        match coreBuiltinKind("Ashes.Task")("run") with
            | None -> Unit
            | _ -> test.fail("the builtin registry consumed an operation owned by async lowering"))
    |> (given (_) ->
        match coreBuiltinKind("Ashes.Ffi")("copyBytes") with
            | Some(CoreFfiCopyBytes) -> Unit
            | _ -> test.fail("the foreign byte copy is absent from the core builtin registry"))
    |> (given (_) ->
        if isIntrinsicBuiltinModule("Ashes.Ffi")
        then Unit
        else test.fail("Ashes.Ffi is not an intrinsic builtin module, so a project using it would look for a shipped source"))
    |> (given (_) ->
        match builtinScheme("Ashes.Ffi")("copyBytes") with
            | Some(TypeScheme { body = SemFunction(SemPointer(SemUInt(8)), SemFunction(SemUInt(64), SemNamed(_, "Result", SemString :: SemBytes :: []), _row), _outerRow) }) -> Unit
            | _ -> test.fail("Ffi.copyBytes is not typed as *u8 -> u64 -> Result(Str, Bytes)"))

// Every member `coreBuiltinKind` dispatches needs a `standardBuiltinLayouts` entry too: the kind
// alone lowers the call, but `lowerCoreQualifiedVariable` reaches the kind only through the layout,
// and a member with no layout falls through to record field access and fails as
// `UnknownLoweringBinding("<module>.<member>")`. The two tables drifted once — the math, regex,
// rune, socket and TLS members had kinds and no layouts — so this pins one member from each module
// that was missing.
let expectEveryBuiltinKindHasALayout unit =
    unit
    |> (given (_) ->
        match builtinScheme("Ashes.Internal.Regex")("compileRaw") with
            | Some(TypeScheme { body = SemFunction(SemString, SemInt, None) }) -> Unit
            | _ -> test.fail("Regex.compileRaw is not typed as Str -> Int"))
    |> (given (_) ->
        match builtinScheme("Ashes.Internal.Regex")("capturesFrom") with
            | Some(TypeScheme { body = SemFunction(SemInt, SemFunction(SemString, SemFunction(SemInt, SemNamed(_, "Maybe", SemList(SemNamed(_, "Maybe", SemString :: [])) :: []), _), _), _) }) -> Unit
            | _ -> test.fail("Regex.capturesFrom is not typed as Int -> Str -> Int -> Maybe(List(Maybe(Str)))"))
    |> (given (_) ->
        match builtinScheme("Ashes.Number.Math")("sqrt") with
            | Some(TypeScheme { body = SemFunction(SemFloat, SemFloat, None) }) -> Unit
            | _ -> test.fail("Math.sqrt is not typed as Float -> Float"))
    |> (given (_) ->
        match builtinScheme("Ashes.Number.Math")("truncToInt") with
            | Some(TypeScheme { body = SemFunction(SemFloat, SemInt, None) }) -> Unit
            | _ -> test.fail("Math.truncToInt is not typed as Float -> Int"))
    |> (given (_) ->
        match builtinScheme("Ashes.Number.Math")("hypot") with
            | Some(TypeScheme { body = SemFunction(SemFloat, SemFunction(SemFloat, SemFloat, None), None) }) -> Unit
            | _ -> test.fail("Math.hypot is not typed as Float -> Float -> Float"))
    |> (given (_) ->
        match builtinScheme("Ashes.Number.Math")("toFloat") with
            | Some(TypeScheme { body = SemFunction(SemInt, SemFloat, None) }) -> Unit
            | _ -> test.fail("Math.toFloat is not typed as Int -> Float"))
    |> (given (_) ->
        match builtinScheme("Ashes.Rune")("fromInt") with
            | Some(TypeScheme { body = SemFunction(SemInt, SemNamed(_, "Maybe", SemRune :: []), None) }) -> Unit
            | _ -> test.fail("Rune.fromInt is not typed as Int -> Maybe(Rune)"))
    |> (given (_) ->
        match builtinScheme("Ashes.IO")("readExact") with
            | Some(TypeScheme { body = SemFunction(SemInt, SemNamed(_, "Result", SemString :: SemString :: []), None) }) -> Unit
            | _ -> test.fail("IO.readExact is not typed as Int -> Result(Str, Str)"))
    |> (given (_) ->
        match builtinScheme("Ashes.Net.Tcp")("send") with
            | Some(TypeScheme { body = SemFunction(SemNamed(_, "Socket", []), SemFunction(SemString, SemNamed(_, "Task", SemString :: SemInt :: []), None), None) }) -> Unit
            | _ -> test.fail("Tcp.send is not typed as Socket -> Str -> Task(Str, Int)"))
    |> (given (_) ->
        match builtinScheme("Ashes.Net.Tcp.Server")("listen") with
            | Some(TypeScheme { body = SemFunction(SemInt, SemNamed(_, "Task", SemString :: SemNamed(_, "Socket", []) :: []), None) }) -> Unit
            | _ -> test.fail("Tcp.Server.listen is not typed as Int -> Task(Str, Socket)"))
    |> (given (_) ->
        match builtinScheme("Ashes.Net.Tls")("close") with
            | Some(TypeScheme { body = SemFunction(SemNamed(_, "TlsSocket", []), SemNamed(_, "Task", SemString :: SemNamed(_, "Unit", []) :: []), None) }) -> Unit
            | _ -> test.fail("Tls.close is not typed as TlsSocket -> Task(Str, Unit)"))
    |> (given (_) ->
        match builtinScheme("Ashes.Net.Tls.Server")("handshake") with
            | Some(TypeScheme { body = SemFunction(SemNamed(_, "Socket", []), SemFunction(SemString, SemFunction(SemString, SemNamed(_, "Task", SemString :: SemNamed(_, "TlsSocket", []) :: []), None), None), None) }) -> Unit
            | _ -> test.fail("Tls.Server.handshake is not typed as Socket -> Str -> Str -> Task(Str, TlsSocket)"))
    |> (given (_) ->
        match builtinScheme("Ashes.Net.Http")("get") with
            | Some(TypeScheme { body = SemFunction(SemString, SemNamed(_, "Task", SemString :: SemString :: []), None) }) -> Unit
            | _ -> test.fail("Http.get is not typed as Str -> Task(Str, Str)"))

let runCoreBuiltinLoweringTests unit =
    unit
    |> expectBuiltinCases(builtinCases)
    |> expectRepresentativeInstructions
    |> expectArityFailure
    |> expectBuiltinRegistry
    |> expectEveryBuiltinKindHasALayout
    |> (given (_) -> Ashes.IO.print("all self-hosted core builtin lowering tests passed"))
