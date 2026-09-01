// Describes stage-0 builtin operations and emits their pre-ownership IR shapes.
//
// Invariants:
// - Layouts carry schemes from the stitched semantic environment; this module never invents symbol IDs.
// - Arguments arrive in strict source order and instruction operands retain the stage-0 order.
// - Fresh aggregate results are marked non-runtime-managed until ownership placement classifies them.
// - FFI, evidence, ownership-copy, parallel, and general async operations belong to later milestones.

import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.Types
export (
    type CoreBuiltinKind(..),
    type CoreBuiltinLayout(..),
    type CoreBuiltinEmissionResult(..),
    type CoreBuiltinEmission(..),
    value coreBuiltinKind,
    value isIntrinsicBuiltinModule,
    value standardBuiltinLayouts,
    value reservedBuiltinTypeVariableCount,
    value emitCoreBuiltin,
)

type CoreBuiltinKind =
    | CoreProgramArgs
    | CorePrint
    | CorePanic
    | CoreWrite
    | CoreWriteBytes
    | CoreWriteLine
    | CoreWriteError(Bool)
    | CoreExit
    | CoreWriteBuffered(Bool)
    | CoreFlushStdout
    | CoreReadLine
    | CoreReadExact
    | CoreConsoleEnableRaw
    | CoreConsoleRestore
    | CoreConsolePoll
    | CoreMonotonicMillis
    | CoreTextByteLength
    | CoreFileReadText
    | CoreFileReadAllBytes
    | CoreFileMmap
    | CoreFileWriteText
    | CoreFileWriteBytes
    | CoreFileExists
    | CoreFileReplace
    | CoreFileMakeExecutable
    | CoreFileOpen
    | CoreFileReadChunk
    | CoreFileReadLine
    | CoreFileClose
    | CoreDirectoryEntries
    | CoreDirectoryCreateAll
    | CoreDirectoryRemoveTree
    | CoreEnvironmentDirectory(EnvironmentDirectoryKind)
    | CoreEnvironmentGet
    | CoreTextUncons(Bool)
    | CoreRuneToText
    | CoreRuneToInt
    | CoreRuneFromInt
    | CoreRuneIsAsciiLetter
    | CoreRuneIsAsciiDigit
    | CoreRuneIsAsciiWhiteSpace
    | CoreTextParseInt
    | CoreTextParseFloat
    | CoreTextFromInt
    | CoreTextFromFloat
    | CoreTextFormatFloat
    | CoreTextToHex
    | CoreTextAsciiCase(Bool)
    | CoreMathToFloat
    | CoreMathFloatUnary(Str)
    | CoreMathFloatToInt(Maybe(Str))
    | CoreMathLibm(Str)
    | CoreBigIntFromInt
    | CoreBigIntToString
    | CoreBigIntToInt
    | CoreBigIntFromString
    | CoreBigIntBinary(Str)
    | CoreBigIntCompare
    | CoreRegexCompile
    | CoreRegexCompileError
    | CoreRegexFind
    | CoreRegexCaptures
    | CoreRegexSubstitute
    | CoreHttpGet
    | CoreHttpPost
    | CoreTcpConnect
    | CoreTcpSend
    | CoreTcpReceive
    | CoreTcpClose
    | CoreTcpListen
    | CoreTcpAccept
    | CoreTcpForkWorkers
    | CoreTcpSetDrainTimeout
    | CoreTlsConnect
    | CoreTlsSend
    | CoreTlsReceive
    | CoreTlsClose
    | CoreTlsServerHandshake
    | CoreBytesEmpty
    | CoreBytesSingleton
    | CoreBytesLength
    | CoreBytesGet
    | CoreBytesIndexOf
    | CoreBytesCompare
    | CoreBytesScanHash
    | CoreBytesSubText
    | CoreBytesSubView
    | CoreBytesAppend
    | CoreBytesAppendByte
    | CoreBytesAllocate
    | CoreBytesCopyRange
    | CoreBytesSet
    | CoreBytesSetU16Le
    | CoreBytesSetU32Le
    | CoreBytesSetU64Le
    | CoreBytesFromList
    | CoreBytesFromText
    | CoreBytesHash
    | CoreBytesU16Le
    | CoreBytesU32Le
    | CoreBytesU64Le
    | CoreBytesGetU16Le
    | CoreBytesGetU32Le
    | CoreBytesGetU64Le
    | CoreUIntToInt
    | CoreUIntFromInt
    | CoreUIntFromInt64
    | CoreSpawnProcess
    | CoreProcessWriteStdin
    | CoreProcessReadStdoutLine
    | CoreProcessReadStderrLine
    | CoreProcessWaitForExit
    | CoreProcessKill

type CoreBuiltinLayout =
    | moduleName: Str
    | memberName: Str
    | scheme: TypeScheme
    | kind: CoreBuiltinKind

type CoreBuiltinEmissionResult =
    | CoreBuiltinTemp(Int)
    | CoreBuiltinUnit
    | CoreBuiltinNever(Int)

type CoreBuiltinEmission =
    | instructions: List(IrInstructionKind)
    | nextTemp: Int
    | result: CoreBuiltinEmissionResult
    | error: Maybe(Str)

let ioBuiltinKind memberName =
    match memberName with
        | "print" -> Some(CorePrint)
        | "panic" -> Some(CorePanic)
        | "args" -> Some(CoreProgramArgs)
        | "write" -> Some(CoreWrite)
        | "writeBytes" -> Some(CoreWriteBytes)
        | "writeLine" -> Some(CoreWriteLine)
        | "writeError" -> Some(CoreWriteError(false))
        | "writeErrorLine" -> Some(CoreWriteError(true))
        | "exit" -> Some(CoreExit)
        | "writeBuffered" -> Some(CoreWriteBuffered(false))
        | "writeBufferedLine" -> Some(CoreWriteBuffered(true))
        | "flush" -> Some(CoreFlushStdout)
        | "readLine" -> Some(CoreReadLine)
        | "readExact" -> Some(CoreReadExact)
        | _ -> None

let mathBuiltinKind memberName =
    match memberName with
        | "toFloat" -> Some(CoreMathToFloat)
        | "sqrt" -> Some(CoreMathFloatUnary("llvm.sqrt.f64"))
        | "floor" -> Some(CoreMathFloatUnary("llvm.floor.f64"))
        | "ceil" -> Some(CoreMathFloatUnary("llvm.ceil.f64"))
        | "round" -> Some(CoreMathFloatUnary("llvm.round.f64"))
        | "trunc" -> Some(CoreMathFloatUnary("llvm.trunc.f64"))
        | "floorToInt" -> Some(CoreMathFloatToInt(Some("llvm.floor.f64")))
        | "roundToInt" -> Some(CoreMathFloatToInt(Some("llvm.round.f64")))
        | "truncToInt" -> Some(CoreMathFloatToInt(None))
        | "sin" -> Some(CoreMathLibm("sin"))
        | "cos" -> Some(CoreMathLibm("cos"))
        | "tan" -> Some(CoreMathLibm("tan"))
        | "asin" -> Some(CoreMathLibm("asin"))
        | "acos" -> Some(CoreMathLibm("acos"))
        | "atan" -> Some(CoreMathLibm("atan"))
        | "sinh" -> Some(CoreMathLibm("sinh"))
        | "cosh" -> Some(CoreMathLibm("cosh"))
        | "tanh" -> Some(CoreMathLibm("tanh"))
        | "exp" -> Some(CoreMathLibm("exp"))
        | "expm1" -> Some(CoreMathLibm("expm1"))
        | "ln" -> Some(CoreMathLibm("log"))
        | "log2" -> Some(CoreMathLibm("log2"))
        | "log10" -> Some(CoreMathLibm("log10"))
        | "log1p" -> Some(CoreMathLibm("log1p"))
        | "cbrt" -> Some(CoreMathLibm("cbrt"))
        | "powF" -> Some(CoreMathLibm("pow"))
        | "atan2" -> Some(CoreMathLibm("atan2"))
        | "hypot" -> Some(CoreMathLibm("hypot"))
        | "fmod" -> Some(CoreMathLibm("fmod"))
        | _ -> None

let fileBuiltinKind memberName =
    match memberName with
        | "readText" -> Some(CoreFileReadText)
        | "readAllBytes" -> Some(CoreFileReadAllBytes)
        | "mmap" -> Some(CoreFileMmap)
        | "writeText" -> Some(CoreFileWriteText)
        | "writeBytes" -> Some(CoreFileWriteBytes)
        | "exists" -> Some(CoreFileExists)
        | "replace" -> Some(CoreFileReplace)
        | "makeExecutable" -> Some(CoreFileMakeExecutable)
        | "open" -> Some(CoreFileOpen)
        | "readChunk" -> Some(CoreFileReadChunk)
        | "readLine" -> Some(CoreFileReadLine)
        | "close" -> Some(CoreFileClose)
        | _ -> None

let directoryBuiltinKind memberName =
    match memberName with
        | "entries" -> Some(CoreDirectoryEntries)
        | "createAll" -> Some(CoreDirectoryCreateAll)
        | "removeTree" -> Some(CoreDirectoryRemoveTree)
        | _ -> None

let environmentBuiltinKind memberName =
    match memberName with
        | "currentDirectory" -> Some(CoreEnvironmentDirectory(CurrentDirectory))
        | "executableDirectory" -> Some(CoreEnvironmentDirectory(ExecutableDirectory))
        | "temporaryDirectory" -> Some(CoreEnvironmentDirectory(TemporaryDirectory))
        | "cacheDirectory" -> Some(CoreEnvironmentDirectory(CacheDirectory))
        | "get" -> Some(CoreEnvironmentGet)
        | _ -> None

let textBuiltinKind memberName =
    match memberName with
        | "uncons" -> Some(CoreTextUncons(false))
        | "unconsText" -> Some(CoreTextUncons(true))
        | "parseInt" -> Some(CoreTextParseInt)
        | "parseFloat" -> Some(CoreTextParseFloat)
        | "fromInt" -> Some(CoreTextFromInt)
        | "fromFloat" -> Some(CoreTextFromFloat)
        | "formatFloat" -> Some(CoreTextFormatFloat)
        | "fromBigInt" -> Some(CoreBigIntToString)
        | "parseBigInt" -> Some(CoreBigIntFromString)
        | "toHex" -> Some(CoreTextToHex)
        | "byteLength" -> Some(CoreTextByteLength)
        | "asciiUpper" -> Some(CoreTextAsciiCase(true))
        | "asciiLower" -> Some(CoreTextAsciiCase(false))
        | _ -> None

let runeBuiltinKind memberName =
    match memberName with
        | "toText" -> Some(CoreRuneToText)
        | "toInt" -> Some(CoreRuneToInt)
        | "fromInt" -> Some(CoreRuneFromInt)
        | "isAsciiLetter" -> Some(CoreRuneIsAsciiLetter)
        | "isAsciiDigit" -> Some(CoreRuneIsAsciiDigit)
        | "isAsciiWhiteSpace" -> Some(CoreRuneIsAsciiWhiteSpace)
        | _ -> None

let bigIntBuiltinKind memberName =
    match memberName with
        | "fromInt" -> Some(CoreBigIntFromInt)
        | "toInt" -> Some(CoreBigIntToInt)
        | "add" -> Some(CoreBigIntBinary("add"))
        | "sub" -> Some(CoreBigIntBinary("sub"))
        | "mul" -> Some(CoreBigIntBinary("mul"))
        | "div" -> Some(CoreBigIntBinary("div"))
        | "mod" -> Some(CoreBigIntBinary("mod"))
        | "compare" -> Some(CoreBigIntCompare)
        | _ -> None

let regexBuiltinKind memberName =
    match memberName with
        | "compileRaw" -> Some(CoreRegexCompile)
        | "compileError" -> Some(CoreRegexCompileError)
        | "findFrom" -> Some(CoreRegexFind)
        | "capturesFrom" -> Some(CoreRegexCaptures)
        | "substituteAll" -> Some(CoreRegexSubstitute)
        | _ -> None

let bytesBuiltinKind memberName =
    match memberName with
        | "empty" -> Some(CoreBytesEmpty)
        | "singleton" -> Some(CoreBytesSingleton)
        | "length" -> Some(CoreBytesLength)
        | "get" -> Some(CoreBytesGet)
        | "indexOf" -> Some(CoreBytesIndexOf)
        | "compare" -> Some(CoreBytesCompare)
        | "scanHash" -> Some(CoreBytesScanHash)
        | "subText" -> Some(CoreBytesSubText)
        | "subView" -> Some(CoreBytesSubView)
        | "append" -> Some(CoreBytesAppend)
        | "appendByte" -> Some(CoreBytesAppendByte)
        | "allocate" -> Some(CoreBytesAllocate)
        | "copyRange" -> Some(CoreBytesCopyRange)
        | "set" -> Some(CoreBytesSet)
        | "setU16Le" -> Some(CoreBytesSetU16Le)
        | "setU32Le" -> Some(CoreBytesSetU32Le)
        | "setU64Le" -> Some(CoreBytesSetU64Le)
        | "fromList" -> Some(CoreBytesFromList)
        | "fromText" -> Some(CoreBytesFromText)
        | "hash" -> Some(CoreBytesHash)
        | "u16Le" -> Some(CoreBytesU16Le)
        | "u32Le" -> Some(CoreBytesU32Le)
        | "u64Le" -> Some(CoreBytesU64Le)
        | "getU16Le" -> Some(CoreBytesGetU16Le)
        | "getU32Le" -> Some(CoreBytesGetU32Le)
        | "getU64Le" -> Some(CoreBytesGetU64Le)
        | _ -> None

let tcpBuiltinKind memberName =
    match memberName with
        | "connect" -> Some(CoreTcpConnect)
        | "send" -> Some(CoreTcpSend)
        | "receive" -> Some(CoreTcpReceive)
        | "close" -> Some(CoreTcpClose)
        | _ -> None

let tcpServerBuiltinKind memberName =
    match memberName with
        | "listen" -> Some(CoreTcpListen)
        | "accept" -> Some(CoreTcpAccept)
        | "forkWorkers" -> Some(CoreTcpForkWorkers)
        | "setDrainTimeout" -> Some(CoreTcpSetDrainTimeout)
        | _ -> None

let tlsBuiltinKind memberName =
    match memberName with
        | "connect" -> Some(CoreTlsConnect)
        | "send" -> Some(CoreTlsSend)
        | "receive" -> Some(CoreTlsReceive)
        | "close" -> Some(CoreTlsClose)
        | _ -> None

let processBuiltinKind memberName =
    match memberName with
        | "spawn" -> Some(CoreSpawnProcess)
        | "writeStdin" -> Some(CoreProcessWriteStdin)
        | "readStdoutLine" -> Some(CoreProcessReadStdoutLine)
        | "readStderrLine" -> Some(CoreProcessReadStderrLine)
        | "waitForExit" -> Some(CoreProcessWaitForExit)
        | "kill" -> Some(CoreProcessKill)
        | _ -> None

let consoleBuiltinKind memberName =
    match memberName with
        | "enableRawInput" -> Some(CoreConsoleEnableRaw)
        | "restoreInput" -> Some(CoreConsoleRestore)
        | "pollInput" -> Some(CoreConsolePoll)
        | "monotonicMillis" -> Some(CoreMonotonicMillis)
        | _ -> None

let coreBuiltinKind moduleName memberName =
    match moduleName with
        | "Ashes.IO" -> ioBuiltinKind(memberName)
        | "Ashes.Number.Math" -> mathBuiltinKind(memberName)
        | "Ashes.IO.File" -> fileBuiltinKind(memberName)
        | "Ashes.IO.Directory" -> directoryBuiltinKind(memberName)
        | "Ashes.IO.Environment" -> environmentBuiltinKind(memberName)
        | "Ashes.Text" -> textBuiltinKind(memberName)
        | "Ashes.Rune" -> runeBuiltinKind(memberName)
        | "Ashes.Number.BigInt" -> bigIntBuiltinKind(memberName)
        | "Ashes.Internal.Regex" -> regexBuiltinKind(memberName)
        | "Ashes.Byte" -> bytesBuiltinKind(memberName)
        | "Ashes.Number.UInt" ->
            match memberName with
                | "toInt" -> Some(CoreUIntToInt)
                | "fromInt" -> Some(CoreUIntFromInt)
                | "fromInt64" -> Some(CoreUIntFromInt64)
                | _ -> None
        | "Ashes.Net.Http" ->
            match memberName with
                | "get" -> Some(CoreHttpGet)
                | "post" -> Some(CoreHttpPost)
                | _ -> None
        | "Ashes.Net.Tcp" -> tcpBuiltinKind(memberName)
        | "Ashes.Net.Tcp.Server" -> tcpServerBuiltinKind(memberName)
        | "Ashes.Net.Tls" -> tlsBuiltinKind(memberName)
        | "Ashes.Net.Tls.Server" ->
            if memberName == "handshake"
            then Some(CoreTlsServerHandshake)
            else None
        | "Ashes.IO.Process" -> processBuiltinKind(memberName)
        | "Ashes.IO.Console" -> consoleBuiltinKind(memberName)
        | _ -> None

// The module names `coreBuiltinKind` dispatches on — the builtin modules that exist without any
// shipped `.ash` source. An import of one of these resolves to an empty synthesized module (its
// members are reached through qualified access, which needs no import); every other missing
// `Ashes.*` module stays a real error. Keep this list in step with `coreBuiltinKind`'s arms.
let isIntrinsicBuiltinModule moduleName =
    match moduleName with
        | "Ashes.IO" -> true
        | "Ashes.Number.Math" -> true
        | "Ashes.IO.File" -> true
        | "Ashes.IO.Directory" -> true
        | "Ashes.IO.Environment" -> true
        | "Ashes.Text" -> true
        | "Ashes.Rune" -> true
        | "Ashes.Number.BigInt" -> true
        | "Ashes.Internal.Regex" -> true
        | "Ashes.Byte" -> true
        | "Ashes.Number.UInt" -> true
        | "Ashes.Net.Http" -> true
        | "Ashes.Net.Tcp" -> true
        | "Ashes.Net.Tcp.Server" -> true
        | "Ashes.Net.Tls" -> true
        | "Ashes.Net.Tls.Server" -> true
        | "Ashes.IO.Process" -> true
        | "Ashes.IO.Console" -> true
        | _ -> false

// Backs "qualified access (no import required)" (language.md's own section title): a real Ashes
// program never needs to `import Ashes.IO` before calling `Ashes.IO.print`, so builtin
// availability cannot be something a caller hand-supplies per program — it has to be intrinsic,
// the same way `coreBuiltinKind`'s own dispatch already is. Grown incrementally alongside
// `AshesCompiler.Backend.IrCodegen`'s own instruction coverage, not meant to reach full parity in
// one pass: every kind listed here already has a complete `emitCoreBuiltin` case (so it type-checks
// and lowers to real IR), but whether that IR then codegens successfully is a separate, backend-side
// question this table makes no claim about — a still-unimplemented instruction panics cleanly at
// codegen, the same "cover exactly what's verified" discipline every other slice in this arc uses.
// Schemes match `Lowering.Builtins.cs`'s own stage-0 registrations exactly (`print`'s is genuinely
// polymorphic, `forall a. a -> Unit`; the rest are monomorphic).
let standardBuiltinLayout moduleName memberName scheme =
    match coreBuiltinKind(moduleName)(memberName) with
        | None -> Ashes.IO.panic("standardBuiltinLayout: coreBuiltinKind does not recognize " + moduleName + "." + memberName)
        | Some(kind) -> CoreBuiltinLayout(moduleName = moduleName, memberName = memberName, scheme = scheme, kind = kind)

let unitType = SemNamed(0)("Unit")([])

// The number of distinct quantified-variable ids used by EITHER `standardBuiltinLayouts`' schemes
// below OR `CoreLowering.ash`'s `standardConstructorLayouts` (currently: `print`'s `(0, "a")`;
// `Maybe`'s `None`/`Some` sharing `(1, "a")`; `Result`'s `Ok`/`Error` sharing `(2, "e")` and
// `(3, "a")`). `CoreLowering.ash`'s `initialState` starts its per-lowering `TypeVariableSupply` at
// THIS value rather than `0`, permanently reserving `[0, reservedBuiltinTypeVariableCount)` for
// these statically-embedded schemes so the supply's own fresh variables can never collide with
// them. Getting this wrong is not a type error — it is a genuine infinite loop: `TypeSchemes.ash`'s
// `instantiate` mints a substitution `(quantifiedId, freshVariable)`, and if the fresh supply ever
// reissues `quantifiedId` itself, the substitution becomes `(0, SemVariable(0))` — a self-mapping
// `Types.ash`'s `applySubstitution` recurses on forever (confirmed via gdb: 2000+ identical stack
// frames, same argument, before the native stack overflowed as a segfault). Reusing one of these
// reserved ids across two different static schemes (as `None`/`Some` do) is safe — each
// `instantiate` call mints its own independent substitution — only a *live* supply value colliding
// with a *reserved* id is the hazard. Bump this whenever a new entry introduces another distinct id.
let reservedBuiltinTypeVariableCount = 4

let standardBuiltinLayouts =
    [
        standardBuiltinLayout("Ashes.IO")("print")(
            TypeScheme(quantified = [(0, "a")], body = SemFunction(SemVariable(0))(unitType)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("panic")(
            TypeScheme(quantified = [], body = SemFunction(SemString)(SemNever)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("exit")(
            TypeScheme(quantified = [], body = SemFunction(SemInt)(SemNever)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("writeLine")(
            TypeScheme(quantified = [], body = SemFunction(SemString)(unitType)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("write")(
            TypeScheme(quantified = [], body = SemFunction(SemString)(unitType)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("writeBytes")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(unitType)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("writeError")(
            TypeScheme(quantified = [], body = SemFunction(SemString)(unitType)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("writeErrorLine")(
            TypeScheme(quantified = [], body = SemFunction(SemString)(unitType)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO")("readLine")(
            TypeScheme(quantified = [], body = SemFunction(unitType)(SemNamed(0)("Maybe")([SemString]))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.IO.File")("exists")(
            TypeScheme(
                quantified = [],
                body = SemFunction(SemString)(SemNamed(0)("Result")([SemString, SemBool]))(None),
                constraints = []
            )
        ),
        standardBuiltinLayout("Ashes.Text")("fromInt")(
            TypeScheme(quantified = [], body = SemFunction(SemInt)(SemString)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Text")("byteLength")(
            TypeScheme(quantified = [], body = SemFunction(SemString)(SemInt)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("length")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemInt)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("fromText")(
            TypeScheme(quantified = [], body = SemFunction(SemString)(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("get")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemUInt(8))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("indexOf")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemInt)(SemInt)(None))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("compare")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemBytes)(SemInt)(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("subText")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemInt)(SemString)(None))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("subView")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemInt)(SemString)(None))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Number.UInt")("toInt")(
            TypeScheme(quantified = [], body = SemFunction(SemUInt(8))(SemInt)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Text")("unconsText")(
            TypeScheme(
                quantified = [],
                body = SemFunction(SemString)(SemNamed(0)("Maybe")([SemTuple([SemString, SemString])]))(None),
                constraints = []
            )
        ),
        standardBuiltinLayout("Ashes.Rune")("toText")(
            TypeScheme(quantified = [], body = SemFunction(SemRune)(SemString)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Text")("uncons")(
            TypeScheme(
                quantified = [],
                body = SemFunction(SemString)(SemNamed(0)("Maybe")([SemTuple([SemRune, SemString])]))(None),
                constraints = []
            )
        ),
        standardBuiltinLayout("Ashes.Text")("parseInt")(
            TypeScheme(
                quantified = [],
                body = SemFunction(SemString)(SemNamed(0)("Result")([SemString, SemInt]))(None),
                constraints = []
            )
        ),
        standardBuiltinLayout("Ashes.Text")("parseFloat")(
            TypeScheme(
                quantified = [],
                body = SemFunction(SemString)(SemNamed(0)("Result")([SemString, SemFloat]))(None),
                constraints = []
            )
        ),
        standardBuiltinLayout("Ashes.Byte")("singleton")(
            TypeScheme(quantified = [], body = SemFunction(SemUInt(8))(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("appendByte")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemUInt(8))(SemBytes)(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("allocate")(
            TypeScheme(quantified = [], body = SemFunction(SemInt)(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("fromList")(
            TypeScheme(quantified = [], body = SemFunction(SemList(SemUInt(8)))(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("hash")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemInt)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Number.UInt")("fromInt64")(
            TypeScheme(quantified = [], body = SemFunction(SemInt)(SemUInt(64))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("empty")(
            TypeScheme(quantified = [], body = SemFunction(unitType)(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("append")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemBytes)(SemBytes)(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("u16Le")(
            TypeScheme(quantified = [], body = SemFunction(SemUInt(16))(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("u32Le")(
            TypeScheme(quantified = [], body = SemFunction(SemUInt(32))(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("u64Le")(
            TypeScheme(quantified = [], body = SemFunction(SemUInt(64))(SemBytes)(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("getU16Le")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemUInt(16))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("getU32Le")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemUInt(32))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("getU64Le")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemUInt(64))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("set")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemUInt(8))(SemBytes)(None))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("setU16Le")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemUInt(16))(SemBytes)(None))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("setU32Le")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemUInt(32))(SemBytes)(None))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("setU64Le")(
            TypeScheme(quantified = [], body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemUInt(64))(SemBytes)(None))(None))(None), constraints = [])
        ),
        standardBuiltinLayout("Ashes.Byte")("copyRange")(
            TypeScheme(
                quantified = [],
                body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemInt)(SemBytes)(None))(None))(None))(None))(None),
                constraints = []
            )
        ),
        standardBuiltinLayout("Ashes.Byte")("scanHash")(
            TypeScheme(
                quantified = [],
                body = SemFunction(SemBytes)(SemFunction(SemInt)(SemFunction(SemInt)(SemTuple([SemInt, SemInt]))(None))(None))(None),
                constraints = []
            )
        ),
        standardBuiltinLayout("Ashes.Text")("toHex")(
            TypeScheme(quantified = [], body = SemFunction(SemInt)(SemString)(None), constraints = [])
        )
    ]

let emitted instructions nextTemp result =
    CoreBuiltinEmission(
        instructions = instructions,
        nextTemp = nextTemp,
        result = result,
        error = None
    )

let emissionFailure start message =
    CoreBuiltinEmission(
        instructions = [],
        nextTemp = start,
        result = CoreBuiltinTemp(-1),
        error = Some(message)
    )

let target0 start build = emitted([build(start)])(start + 1)(CoreBuiltinTemp(start))

let target1 start first build = emitted([build(start)(first)])(start + 1)(CoreBuiltinTemp(start))

let target2 start first second build = emitted([build(start)(first)(second)])(start + 1)(CoreBuiltinTemp(start))

let target3 start first second third build =
    emitted(
        [build(start)(first)(second)(third)],
        start + 1,
        CoreBuiltinTemp(start)
    )

let target4 start first second third fourth build =
    emitted(
        [build(start)(first)(second)(third)(fourth)],
        start + 1,
        CoreBuiltinTemp(start)
    )

let target5 start first second third fourth fifth build =
    emitted(
        [build(start)(first)(second)(third)(fourth)(fifth)],
        start + 1,
        CoreBuiltinTemp(start)
    )

let unit instructions start = emitted(instructions)(start)(CoreBuiltinUnit)

let unitWithTarget start build = emitted([build(start)])(start + 1)(CoreBuiltinUnit)

let identity start value = emitted([])(start)(CoreBuiltinTemp(value))

let never start value instruction = emitted([instruction(value)])(start)(CoreBuiltinNever(value))

let managedTarget1 start first build =
    target1(start)(first)(given (target) ->
        given (argument) -> build(target)(argument)(false))

let managedTarget2 start first second build =
    target2(start)(first)(second)(given (target) ->
        given (left) ->
            given (right) -> build(target)(left)(right)(false))

let runeRange start rune lower upper =
    emitted(
        [
            LoadConstInt(start)(lower),
            LoadConstInt(start + 1)(upper),
            CmpIntGe(start + 2)(rune)(start),
            CmpIntLe(start + 3)(rune)(start + 1),
            AndInt(start + 4)(start + 2)(start + 3)
        ]
    )(start + 5)(CoreBuiltinTemp(start + 4))

let runeAsciiLetter start rune =
    emitted(
        [
            LoadConstInt(start)(65),
            LoadConstInt(start + 1)(90),
            CmpIntGe(start + 2)(rune)(start),
            CmpIntLe(start + 3)(rune)(start + 1),
            AndInt(start + 4)(start + 2)(start + 3),
            LoadConstInt(start + 5)(97),
            LoadConstInt(start + 6)(122),
            CmpIntGe(start + 7)(rune)(start + 5),
            CmpIntLe(start + 8)(rune)(start + 6),
            AndInt(start + 9)(start + 7)(start + 8),
            OrInt(start + 10)(start + 4)(start + 9)
        ]
    )(start + 11)(CoreBuiltinTemp(start + 10))

let runeAsciiWhiteSpace start rune =
    emitted(
        [
            LoadConstInt(start)(32),
            LoadConstInt(start + 1)(9),
            LoadConstInt(start + 2)(10),
            LoadConstInt(start + 3)(13),
            CmpIntEq(start + 4)(rune)(start),
            CmpIntEq(start + 5)(rune)(start + 1),
            CmpIntEq(start + 6)(rune)(start + 2),
            CmpIntEq(start + 7)(rune)(start + 3),
            OrInt(start + 8)(start + 4)(start + 5),
            OrInt(start + 9)(start + 8)(start + 6),
            OrInt(start + 10)(start + 9)(start + 7)
        ]
    )(start + 11)(CoreBuiltinTemp(start + 10))

let floatToInt start argument intrinsic =
    match intrinsic with
        | None -> target1(start)(argument)(FloatToInt)
        | Some(name) ->
            emitted(
                [
                    FloatUnaryIntrinsic(start)(argument)(name),
                    FloatToInt(start + 1)(start)
                ]
            )(start + 2)(CoreBuiltinTemp(start + 1))

let textFormatFloat start value precision = managedTarget2(start)(value)(precision)(TextFormatFloat)

let regexCaptures start code subject offset = target3(start)(code)(subject)(offset)(RegexCaptures)

let rsub start code subject replacement = target3(start)(code)(subject)(replacement)(RegexSubstitute)

let tlsServerHandshake start socket cert key = target3(start)(socket)(cert)(key)(CreateTlsServerHandshakeTask)

let bytesScanHash start value needle offset = target3(start)(value)(needle)(offset)(BytesScanHash)

let tcpForkWorkers start workers port = target2(start)(port)(workers)(CreateForkWorkersTask)

let uintFromInt start value =
    emitted(
        [
            LoadConstInt(start)(255),
            AndInt(start + 1)(value)(start)
        ]
    )(start + 2)(CoreBuiltinTemp(start + 1))

let printValue start value semanticType =
    match semanticType with
        | SemInt -> unit([PrintInt(value)])(start)
        | SemUInt(bits) ->
            if bits < 64
            then unit([PrintInt(value)])(start)
            else emissionFailure(start)("print does not support u64")
        | SemString -> unit([PrintStr(value)])(start)
        | SemBool -> unit([PrintBool(value)])(start)
        | other -> emissionFailure(start)("print does not support " + formatSemanticType(other))

let emitCoreBuiltin kind start arguments argumentTypes =
    match (kind, arguments, argumentTypes) with
        | (CoreProgramArgs, [], []) -> target0(start)(LoadProgramArgs)
        | (CorePrint, value :: [], semanticType :: []) -> printValue(start)(value)(semanticType)
        | (CorePanic, value :: [], _type :: []) -> never(start)(value)(PanicStr)
        | (CoreWrite, value :: [], _types) -> unit([WriteStr(value)])(start)
        | (CoreWriteBytes, value :: [], _types) -> unit([WriteStr(value)])(start)
        | (CoreWriteLine, value :: [], _types) -> unit([PrintStr(value)])(start)
        | (CoreWriteError(newline), value :: [], _types) -> unit([WriteErrorStr(value)(newline)])(start)
        | (CoreExit, value :: [], _types) -> never(start)(value)(ExitProcess)
        | (CoreWriteBuffered(newline), value :: [], _types) -> unit([WriteBufferedStr(value)(newline)])(start)
        | (CoreFlushStdout, _unit :: [], _types) -> unit([FlushStdout])(start)
        | (CoreReadLine, _unit :: [], _types) -> target0(start)(ReadLine)
        | (CoreReadExact, count :: [], _types) -> target1(start)(count)(ReadExact)
        | (CoreConsoleEnableRaw, _unit :: [], _types) -> target0(start)(ConsoleEnableRaw)
        | (CoreConsoleRestore, token :: [], _types) -> unit([ConsoleRestore(token)])(start)
        | (CoreConsolePoll, timeout :: [], _types) -> target1(start)(timeout)(ConsolePoll)
        | (CoreMonotonicMillis, _unit :: [], _types) -> target0(start)(MonotonicMillis)
        | (CoreTextByteLength, text :: [], _types) -> target1(start)(text)(TextByteLength)
        | (CoreFileReadText, path :: [], _types) -> target1(start)(path)(FileReadText)
        | (CoreFileReadAllBytes, path :: [], _types) -> target1(start)(path)(FileReadAllBytes)
        | (CoreFileMmap, path :: [], _types) -> target1(start)(path)(FileMmap)
        | (CoreFileWriteText, path :: value :: [], _types) -> target2(start)(path)(value)(FileWriteText)
        | (CoreFileWriteBytes, path :: value :: [], _types) -> target2(start)(path)(value)(FileWriteBytes)
        | (CoreFileExists, path :: [], _types) -> target1(start)(path)(FileExists)
        | (CoreFileReplace, source :: destination :: [], _types) -> target2(start)(source)(destination)(FileReplace)
        | (CoreFileMakeExecutable, path :: [], _types) -> target1(start)(path)(FileMakeExecutable)
        | (CoreFileOpen, path :: [], _types) -> target1(start)(path)(FileOpen)
        | (CoreFileReadChunk, fileHandle :: count :: [], _types) -> target2(start)(fileHandle)(count)(FileReadChunk)
        | (CoreFileReadLine, fileHandle :: [], _types) -> target1(start)(fileHandle)(FileReadLine)
        | (CoreFileClose, fileHandle :: [], _types) -> target1(start)(fileHandle)(FileClose)
        | (CoreDirectoryEntries, path :: [], _types) -> target1(start)(path)(DirectoryEntries)
        | (CoreDirectoryCreateAll, path :: [], _types) -> target1(start)(path)(DirectoryCreateAll)
        | (CoreDirectoryRemoveTree, path :: [], _types) -> target1(start)(path)(DirectoryRemoveTree)
        | (CoreEnvironmentDirectory(directoryKind), _unit :: [], _types) ->
            target0(start)(given (target) -> EnvironmentDirectory(target)(directoryKind))
        | (CoreEnvironmentGet, name :: [], _types) -> target1(start)(name)(EnvironmentGet)
        | (CoreTextUncons(stringHead), text :: [], _types) ->
            managedTarget1(start)(text)(if stringHead
            then TextUnconsText
            else TextUncons)
        | (CoreRuneToText, rune :: [], _types) -> managedTarget1(start)(rune)(RuneToText)
        | (CoreRuneToInt, rune :: [], _types) -> identity(start)(rune)
        | (CoreRuneFromInt, value :: [], _types) -> managedTarget1(start)(value)(RuneFromInt)
        | (CoreRuneIsAsciiLetter, rune :: [], _types) -> runeAsciiLetter(start)(rune)
        | (CoreRuneIsAsciiDigit, rune :: [], _types) -> runeRange(start)(rune)(48)(57)
        | (CoreRuneIsAsciiWhiteSpace, rune :: [], _types) -> runeAsciiWhiteSpace(start)(rune)
        | (CoreTextParseInt, text :: [], _types) -> managedTarget1(start)(text)(TextParseInt)
        | (CoreTextParseFloat, text :: [], _types) -> managedTarget1(start)(text)(TextParseFloat)
        | (CoreTextFromInt, value :: [], _types) -> managedTarget1(start)(value)(TextFromInt)
        | (CoreTextFromFloat, value :: [], _types) -> managedTarget1(start)(value)(TextFromFloat)
        | (CoreTextFormatFloat, value :: precision :: [], _types) -> textFormatFloat(start)(value)(precision)
        | (CoreTextToHex, value :: [], _types) -> managedTarget1(start)(value)(TextToHex)
        | (CoreTextAsciiCase(upper), text :: [], _types) ->
            target1(start)(text)(given (target) ->
                given (source) -> TextAsciiCase(target)(source)(upper)(false))
        | (CoreMathToFloat, value :: [], _types) -> target1(start)(value)(IntToFloat)
        | (CoreMathFloatUnary(name), value :: [], _types) ->
            target1(start)(value)(given (target) ->
                given (source) -> FloatUnaryIntrinsic(target)(source)(name))
        | (CoreMathFloatToInt(intrinsic), value :: [], _types) -> floatToInt(start)(value)(intrinsic)
        | (CoreMathLibm(name), values, _types) ->
            target0(start)(given (target) -> CallLibm(target)(name)(values))
        | (CoreBigIntFromInt, value :: [], _types) -> managedTarget1(start)(value)(BigIntFromInt)
        | (CoreBigIntToString, value :: [], _types) -> managedTarget1(start)(value)(BigIntToString)
        | (CoreBigIntToInt, value :: [], _types) -> managedTarget1(start)(value)(BigIntToInt)
        | (CoreBigIntFromString, value :: [], _types) -> managedTarget1(start)(value)(BigIntFromString)
        | (CoreBigIntBinary(operation), left :: right :: [], _types) ->
            target2(start)(left)(right)(given (target) ->
                given (first) ->
                    given (second) -> BigIntBinary(target)(first)(second)(operation)(false))
        | (CoreBigIntCompare, left :: right :: [], _types) -> target2(start)(left)(right)(BigIntCompare)
        | (CoreRegexCompile, pattern :: [], _types) -> target1(start)(pattern)(RegexCompile)
        | (CoreRegexCompileError, pattern :: [], _types) -> target1(start)(pattern)(RegexCompileError)
        | (CoreRegexFind, code :: subject :: from :: [], _types) -> target3(start)(code)(subject)(from)(RegexFind)
        | (CoreRegexCaptures, code :: subject :: from :: [], _types) -> regexCaptures(start)(code)(subject)(from)
        | (CoreRegexSubstitute, code :: subject :: replacement :: [], _types) -> rsub(start)(code)(subject)(replacement)
        | (CoreHttpGet, url :: [], _types) -> target1(start)(url)(CreateHttpGetTask)
        | (CoreHttpPost, url :: body :: [], _types) -> target2(start)(url)(body)(CreateHttpPostTask)
        | (CoreTcpConnect, host :: port :: [], _types) -> target2(start)(host)(port)(CreateTcpConnectTask)
        | (CoreTcpSend, socket :: text :: [], _types) -> target2(start)(socket)(text)(CreateTcpSendTask)
        | (CoreTcpReceive, socket :: count :: [], _types) -> target2(start)(socket)(count)(CreateTcpReceiveTask)
        | (CoreTcpClose, socket :: [], _types) -> target1(start)(socket)(CreateTcpCloseTask)
        | (CoreTcpListen, port :: [], _types) -> target1(start)(port)(CreateTcpListenTask)
        | (CoreTcpAccept, socket :: [], _types) -> target1(start)(socket)(CreateTcpAcceptTask)
        | (CoreTcpForkWorkers, workers :: port :: [], _types) -> tcpForkWorkers(start)(workers)(port)
        | (CoreTcpSetDrainTimeout, timeout :: [], _types) ->
            unitWithTarget(start)(given (target) -> SetDrainTimeout(target)(timeout))
        | (CoreTlsConnect, host :: port :: [], _types) -> target2(start)(host)(port)(CreateTlsConnectTask)
        | (CoreTlsSend, socket :: text :: [], _types) -> target2(start)(socket)(text)(CreateTlsSendTask)
        | (CoreTlsReceive, socket :: count :: [], _types) -> target2(start)(socket)(count)(CreateTlsReceiveTask)
        | (CoreTlsClose, socket :: [], _types) -> target1(start)(socket)(CreateTlsCloseTask)
        | (CoreTlsServerHandshake, socket :: cert :: key :: [], _types) -> tlsServerHandshake(start)(socket)(cert)(key)
        | (CoreBytesEmpty, _unit :: [], _types) ->
            target0(start)(given (target) -> BytesEmpty(target)(false))
        | (CoreBytesSingleton, value :: [], _types) -> managedTarget1(start)(value)(BytesSingleton)
        | (CoreBytesLength, value :: [], _types) -> target1(start)(value)(BytesLength)
        | (CoreBytesGet, value :: index :: [], _types) -> target2(start)(value)(index)(BytesGet)
        | (CoreBytesIndexOf, value :: needle :: from :: [], _types) -> target3(start)(value)(needle)(from)(BytesIndexOf)
        | (CoreBytesCompare, left :: right :: [], _types) -> target2(start)(left)(right)(BytesCompare)
        | (CoreBytesScanHash, value :: needle :: from :: [], _types) -> bytesScanHash(start)(value)(needle)(from)
        | (CoreBytesSubText, value :: from :: count :: [], _types) ->
            target3(start)(value)(from)(count)(given (target) ->
                given (bytes) ->
                    given (offset) ->
                        given (length) -> BytesSubText(target)(bytes)(offset)(length)(false))
        | (CoreBytesSubView, value :: from :: count :: [], _types) -> target3(start)(value)(from)(count)(BytesSubView)
        | (CoreBytesAppend, left :: right :: [], _types) -> managedTarget2(start)(left)(right)(BytesAppend)
        | (CoreBytesAppendByte, value :: byte :: [], _types) -> managedTarget2(start)(value)(byte)(BytesAppendByte)
        | (CoreBytesAllocate, count :: [], _types) ->
            target1(start)(count)(given (target) ->
                given (length) -> BytesAllocate(target)(length)(false))
        | (CoreBytesCopyRange, destination :: destinationOffset :: source :: sourceOffset :: count :: [], _types) ->
            target5(start)(destination)(destinationOffset)(source)(sourceOffset)(count)(
                given (target) ->
                    given (first) ->
                        given (firstOffset) ->
                            given (second) ->
                                given (secondOffset) ->
                                    given (length) ->
                                        BytesCopyRange(
                                            target,
                                            first,
                                            firstOffset,
                                            second,
                                            secondOffset,
                                            length,
                                            false,
                                            false
                                        )
            )
        | (CoreBytesSet, value :: offset :: replacement :: [], _types) ->
            target3(start)(value)(offset)(replacement)(given (target) ->
                given (bytes) ->
                    given (index) ->
                        given (item) -> BytesSet(target)(bytes)(index)(item)(false)(false))
        | (CoreBytesSetU16Le, value :: offset :: replacement :: [], _types) ->
            target3(start)(value)(offset)(replacement)(given (target) ->
                given (bytes) ->
                    given (index) ->
                        given (item) -> BytesSetU16Le(target)(bytes)(index)(item)(false)(false))
        | (CoreBytesSetU32Le, value :: offset :: replacement :: [], _types) ->
            target3(start)(value)(offset)(replacement)(given (target) ->
                given (bytes) ->
                    given (index) ->
                        given (item) -> BytesSetU32Le(target)(bytes)(index)(item)(false)(false))
        | (CoreBytesSetU64Le, value :: offset :: replacement :: [], _types) ->
            target3(start)(value)(offset)(replacement)(given (target) ->
                given (bytes) ->
                    given (index) ->
                        given (item) -> BytesSetU64Le(target)(bytes)(index)(item)(false)(false))
        | (CoreBytesFromList, values :: [], _types) -> managedTarget1(start)(values)(BytesFromList)
        | (CoreBytesFromText, text :: [], _types) -> identity(start)(text)
        | (CoreBytesHash, value :: [], _types) -> target1(start)(value)(BytesHash)
        | (CoreBytesU16Le, value :: [], _types) -> managedTarget1(start)(value)(BytesU16Le)
        | (CoreBytesU32Le, value :: [], _types) -> managedTarget1(start)(value)(BytesU32Le)
        | (CoreBytesU64Le, value :: [], _types) -> managedTarget1(start)(value)(BytesU64Le)
        | (CoreBytesGetU16Le, value :: offset :: [], _types) -> target2(start)(value)(offset)(BytesGetU16Le)
        | (CoreBytesGetU32Le, value :: offset :: [], _types) -> target2(start)(value)(offset)(BytesGetU32Le)
        | (CoreBytesGetU64Le, value :: offset :: [], _types) -> target2(start)(value)(offset)(BytesGetU64Le)
        | (CoreUIntToInt, value :: [], _types) -> identity(start)(value)
        | (CoreUIntFromInt64, value :: [], _types) -> identity(start)(value)
        | (CoreUIntFromInt, value :: [], _types) -> uintFromInt(start)(value)
        | (CoreSpawnProcess, executable :: args :: [], _types) -> target2(start)(executable)(args)(SpawnProcess)
        | (CoreProcessWriteStdin, process :: text :: [], _types) -> target2(start)(process)(text)(ProcessWriteStdin)
        | (CoreProcessReadStdoutLine, process :: [], _types) -> target1(start)(process)(ProcessReadStdoutLine)
        | (CoreProcessReadStderrLine, process :: [], _types) -> target1(start)(process)(ProcessReadStderrLine)
        | (CoreProcessWaitForExit, process :: [], _types) -> target1(start)(process)(ProcessWaitForExit)
        | (CoreProcessKill, process :: [], _types) -> target1(start)(process)(ProcessKill)
        | _ -> emissionFailure(start)("builtin arguments do not match the registered arity")
