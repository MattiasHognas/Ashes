using Ashes.Frontend;

namespace Ashes.Semantics;

public static class BuiltinRegistry
{
    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    {
        "Float",
        "Bytes"
    };

    public enum BuiltinValueKind
    {
        Print,
        Panic,
        Args,
        Write,
        WriteLine,
        ReadLine,
        FileReadText,
        FileReadAllBytes,
        FileMmap,
        FileWriteText,
        FileExists,
        FileOpen,
        FileReadChunk,
        FileReadLine,
        FileClose,
        InternalDeepCopy,
        ParallelBoth,
        ParallelWithWorkers,
        TextUncons,
        TextParseInt,
        TextParseFloat,
        TextFromInt,
        TextFromFloat,
        TextFormatFloat,
        TextToHex,
        TextAsciiUpper,
        TextAsciiLower,
        BigIntFromInt,
        BigIntToString,
        BigIntToInt,
        BigIntFromString,
        BigIntAdd,
        BigIntSub,
        BigIntMul,
        BigIntDiv,
        BigIntMod,
        BigIntCompare,
        HttpGet,
        HttpPost,
        NetTcpConnect,
        NetTcpSend,
        NetTcpReceive,
        NetTcpClose,
        NetTcpListen,
        NetTcpAccept,
        NetTcpForkWorkers,
        NetTcpSetDrainTimeout,
        NetTlsConnect,
        NetTlsSend,
        NetTlsReceive,
        NetTlsClose,
        NetTlsServerHandshake,
        AsyncRun,
        AsyncTask,
        AsyncFromResult,
        AsyncSleep,
        AsyncAll,
        AsyncSpawn,
        AsyncRace,
        BytesEmpty,
        BytesSingleton,
        BytesLength,
        BytesGet,
        BytesIndexOf,
        BytesCompare,
        BytesScanHash,
        BytesSubText,
        BytesSubView,
        BytesAppend,
        BytesAppendByte,
        BytesFromList,
        BytesFromText,
        BytesHash,
        BytesU16Le,
        BytesU32Le,
        BytesU64Le,
        BytesGetU16Le,
        BytesGetU32Le,
        BytesGetU64Le,
        UIntToInt,
        UIntFromInt,
        MathToFloat,
        MathSqrt,
        MathFloor,
        MathCeil,
        MathRound,
        MathTrunc,
        MathFloorToInt,
        MathRoundToInt,
        MathTruncToInt,
        MathSin,
        MathCos,
        MathTan,
        MathAsin,
        MathAcos,
        MathAtan,
        MathSinh,
        MathCosh,
        MathTanh,
        MathExp,
        MathExpm1,
        MathLn,
        MathLog2,
        MathLog10,
        MathLog1p,
        MathCbrt,
        MathPowF,
        MathAtan2,
        MathHypot,
        MathFmod,
        RegexCompile,
        RegexCompileError,
        RegexFind,
        RegexCaptures,
        RegexSubstitute,
        FileWriteBytes,
        IoWriteBytes,
        IoReadExact,
        ConsoleEnableRaw,
        ConsoleRestore,
        ConsolePoll,
        ConsoleMonotonicMillis,
        TextByteLength,
        SpawnProcess,
        ProcessWriteStdin,
        ProcessReadStdoutLine,
        ProcessReadStderrLine,
        ProcessWaitForExit,
        ProcessKill
    }

    public sealed record BuiltinModuleMember(
        string Name,
        BuiltinValueKind Kind,
        bool IsCallable,
        int Arity);

    public sealed record BuiltinModule(
        string Name,
        string? ResourceName,
        IReadOnlyDictionary<string, BuiltinModuleMember> Members);

    public sealed record BuiltinConstructor(
        string Name,
        IReadOnlyList<TypeRef> ParameterTypes,
        TypeConstructor DeclaringSyntax);

    public sealed record BuiltinType(
        string Name,
        IReadOnlyList<TypeParameterSymbol> TypeParameters,
        IReadOnlyList<BuiltinConstructor> Constructors,
        TypeDecl DeclaringSyntax);

    private static readonly IReadOnlyDictionary<string, BuiltinModule> ModulesByName =
        new Dictionary<string, BuiltinModule>(StringComparer.Ordinal)
        {
            ["Ashes"] = new(
                "Ashes",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.IO"] = new(
                "Ashes.IO",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["print"] = new("print", BuiltinValueKind.Print, IsCallable: true, Arity: 1),
                    ["panic"] = new("panic", BuiltinValueKind.Panic, IsCallable: true, Arity: 1),
                    ["args"] = new("args", BuiltinValueKind.Args, IsCallable: false, Arity: 0),
                    ["write"] = new("write", BuiltinValueKind.Write, IsCallable: true, Arity: 1),
                    ["writeBytes"] = new("writeBytes", BuiltinValueKind.IoWriteBytes, IsCallable: true, Arity: 1),
                    ["writeLine"] = new("writeLine", BuiltinValueKind.WriteLine, IsCallable: true, Arity: 1),
                    ["readLine"] = new("readLine", BuiltinValueKind.ReadLine, IsCallable: true, Arity: 1),
                    ["readExact"] = new("readExact", BuiltinValueKind.IoReadExact, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Core.Result"] = new(
                "Ashes.Core.Result",
                "Ashes.Semantics.StdLib.Ashes.Core.Result.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Collection.List"] = new(
                "Ashes.Collection.List",
                "Ashes.Semantics.StdLib.Ashes.Collection.List.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Number.Math"] = new(
                "Ashes.Number.Math",
                "Ashes.Semantics.StdLib.Ashes.Number.Math.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["toFloat"] = new("toFloat", BuiltinValueKind.MathToFloat, IsCallable: true, Arity: 1),
                    ["sqrt"] = new("sqrt", BuiltinValueKind.MathSqrt, IsCallable: true, Arity: 1),
                    ["floor"] = new("floor", BuiltinValueKind.MathFloor, IsCallable: true, Arity: 1),
                    ["ceil"] = new("ceil", BuiltinValueKind.MathCeil, IsCallable: true, Arity: 1),
                    ["round"] = new("round", BuiltinValueKind.MathRound, IsCallable: true, Arity: 1),
                    ["trunc"] = new("trunc", BuiltinValueKind.MathTrunc, IsCallable: true, Arity: 1),
                    ["floorToInt"] = new("floorToInt", BuiltinValueKind.MathFloorToInt, IsCallable: true, Arity: 1),
                    ["roundToInt"] = new("roundToInt", BuiltinValueKind.MathRoundToInt, IsCallable: true, Arity: 1),
                    ["truncToInt"] = new("truncToInt", BuiltinValueKind.MathTruncToInt, IsCallable: true, Arity: 1),
                    ["sin"] = new("sin", BuiltinValueKind.MathSin, IsCallable: true, Arity: 1),
                    ["cos"] = new("cos", BuiltinValueKind.MathCos, IsCallable: true, Arity: 1),
                    ["tan"] = new("tan", BuiltinValueKind.MathTan, IsCallable: true, Arity: 1),
                    ["asin"] = new("asin", BuiltinValueKind.MathAsin, IsCallable: true, Arity: 1),
                    ["acos"] = new("acos", BuiltinValueKind.MathAcos, IsCallable: true, Arity: 1),
                    ["atan"] = new("atan", BuiltinValueKind.MathAtan, IsCallable: true, Arity: 1),
                    ["sinh"] = new("sinh", BuiltinValueKind.MathSinh, IsCallable: true, Arity: 1),
                    ["cosh"] = new("cosh", BuiltinValueKind.MathCosh, IsCallable: true, Arity: 1),
                    ["tanh"] = new("tanh", BuiltinValueKind.MathTanh, IsCallable: true, Arity: 1),
                    ["exp"] = new("exp", BuiltinValueKind.MathExp, IsCallable: true, Arity: 1),
                    ["expm1"] = new("expm1", BuiltinValueKind.MathExpm1, IsCallable: true, Arity: 1),
                    ["ln"] = new("ln", BuiltinValueKind.MathLn, IsCallable: true, Arity: 1),
                    ["log2"] = new("log2", BuiltinValueKind.MathLog2, IsCallable: true, Arity: 1),
                    ["log10"] = new("log10", BuiltinValueKind.MathLog10, IsCallable: true, Arity: 1),
                    ["log1p"] = new("log1p", BuiltinValueKind.MathLog1p, IsCallable: true, Arity: 1),
                    ["cbrt"] = new("cbrt", BuiltinValueKind.MathCbrt, IsCallable: true, Arity: 1),
                    ["powF"] = new("powF", BuiltinValueKind.MathPowF, IsCallable: true, Arity: 2),
                    ["atan2"] = new("atan2", BuiltinValueKind.MathAtan2, IsCallable: true, Arity: 2),
                    ["hypot"] = new("hypot", BuiltinValueKind.MathHypot, IsCallable: true, Arity: 2),
                    ["fmod"] = new("fmod", BuiltinValueKind.MathFmod, IsCallable: true, Arity: 2)
                }),
            ["Ashes.Collection.Array"] = new(
                "Ashes.Collection.Array",
                "Ashes.Semantics.StdLib.Ashes.Collection.Array.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Collection.Map"] = new(
                "Ashes.Collection.Map",
                "Ashes.Semantics.StdLib.Ashes.Collection.Map.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Collection.HashMap"] = new(
                "Ashes.Collection.HashMap",
                "Ashes.Semantics.StdLib.Ashes.Collection.HashMap.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Collection.HashTrie"] = new(
                "Ashes.Collection.HashTrie",
                "Ashes.Semantics.StdLib.Ashes.Collection.HashTrie.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Task.Parallel"] = new(
                "Ashes.Task.Parallel",
                "Ashes.Semantics.StdLib.Ashes.Task.Parallel.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    // Hybrid module: `both` and `withWorkers` are compiler intrinsics (lowered at
                    // each call site — `both` to deep-copy a worker's result at the concrete result
                    // type, `withWorkers` to save/set/restore the runtime worker override around a
                    // thunk); `map`/`reduce`/helpers come from the embedded source.
                    ["both"] = new("both", BuiltinValueKind.ParallelBoth, IsCallable: true, Arity: 2),
                    ["withWorkers"] = new("withWorkers", BuiltinValueKind.ParallelWithWorkers, IsCallable: true, Arity: 2)
                }),
            ["Ashes.Core.Maybe"] = new(
                "Ashes.Core.Maybe",
                "Ashes.Semantics.StdLib.Ashes.Core.Maybe.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Test"] = new(
                "Ashes.Test",
                "Ashes.Semantics.StdLib.Ashes.Test.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Internal"] = new(
                "Ashes.Internal",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    // Foundation primitive for in-place reuse (#2) and parallel result copy-out (#5):
                    // produces an independent deep copy. Semantically identity for immutable values.
                    ["deepCopy"] = new("deepCopy", BuiltinValueKind.InternalDeepCopy, IsCallable: true, Arity: 1)
                }),
            ["Ashes.IO.File"] = new(
                "Ashes.IO.File",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["readText"] = new("readText", BuiltinValueKind.FileReadText, IsCallable: true, Arity: 1),
                    ["readAllBytes"] = new("readAllBytes", BuiltinValueKind.FileReadAllBytes, IsCallable: true, Arity: 1),
                    ["mmap"] = new("mmap", BuiltinValueKind.FileMmap, IsCallable: true, Arity: 1),
                    ["writeText"] = new("writeText", BuiltinValueKind.FileWriteText, IsCallable: true, Arity: 2),
                    ["writeBytes"] = new("writeBytes", BuiltinValueKind.FileWriteBytes, IsCallable: true, Arity: 2),
                    ["exists"] = new("exists", BuiltinValueKind.FileExists, IsCallable: true, Arity: 1),
                    ["open"] = new("open", BuiltinValueKind.FileOpen, IsCallable: true, Arity: 1),
                    ["readChunk"] = new("readChunk", BuiltinValueKind.FileReadChunk, IsCallable: true, Arity: 2),
                    ["readLine"] = new("readLine", BuiltinValueKind.FileReadLine, IsCallable: true, Arity: 1),
                    ["close"] = new("close", BuiltinValueKind.FileClose, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Text"] = new(
                "Ashes.Text",
                "Ashes.Semantics.StdLib.Ashes.Text.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["uncons"] = new("uncons", BuiltinValueKind.TextUncons, IsCallable: true, Arity: 1),
                    ["parseInt"] = new("parseInt", BuiltinValueKind.TextParseInt, IsCallable: true, Arity: 1),
                    ["parseFloat"] = new("parseFloat", BuiltinValueKind.TextParseFloat, IsCallable: true, Arity: 1),
                    ["fromInt"] = new("fromInt", BuiltinValueKind.TextFromInt, IsCallable: true, Arity: 1),
                    ["fromFloat"] = new("fromFloat", BuiltinValueKind.TextFromFloat, IsCallable: true, Arity: 1),
                    ["formatFloat"] = new("formatFloat", BuiltinValueKind.TextFormatFloat, IsCallable: true, Arity: 2),
                    ["fromBigInt"] = new("fromBigInt", BuiltinValueKind.BigIntToString, IsCallable: true, Arity: 1),
                    ["parseBigInt"] = new("parseBigInt", BuiltinValueKind.BigIntFromString, IsCallable: true, Arity: 1),
                    ["toHex"] = new("toHex", BuiltinValueKind.TextToHex, IsCallable: true, Arity: 1),
                    ["byteLength"] = new("byteLength", BuiltinValueKind.TextByteLength, IsCallable: true, Arity: 1),
                    ["asciiUpper"] = new("asciiUpper", BuiltinValueKind.TextAsciiUpper, IsCallable: true, Arity: 1),
                    ["asciiLower"] = new("asciiLower", BuiltinValueKind.TextAsciiLower, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Number.BigInt"] = new(
                "Ashes.Number.BigInt",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["fromInt"] = new("fromInt", BuiltinValueKind.BigIntFromInt, IsCallable: true, Arity: 1),
                    ["toInt"] = new("toInt", BuiltinValueKind.BigIntToInt, IsCallable: true, Arity: 1),
                    ["add"] = new("add", BuiltinValueKind.BigIntAdd, IsCallable: true, Arity: 2),
                    ["sub"] = new("sub", BuiltinValueKind.BigIntSub, IsCallable: true, Arity: 2),
                    ["mul"] = new("mul", BuiltinValueKind.BigIntMul, IsCallable: true, Arity: 2),
                    ["div"] = new("div", BuiltinValueKind.BigIntDiv, IsCallable: true, Arity: 2),
                    ["mod"] = new("mod", BuiltinValueKind.BigIntMod, IsCallable: true, Arity: 2),
                    ["compare"] = new("compare", BuiltinValueKind.BigIntCompare, IsCallable: true, Arity: 2)
                }),
            ["Ashes.Byte"] = new(
                "Ashes.Byte",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["empty"] = new("empty", BuiltinValueKind.BytesEmpty, IsCallable: true, Arity: 1),
                    ["singleton"] = new("singleton", BuiltinValueKind.BytesSingleton, IsCallable: true, Arity: 1),
                    ["length"] = new("length", BuiltinValueKind.BytesLength, IsCallable: true, Arity: 1),
                    ["get"] = new("get", BuiltinValueKind.BytesGet, IsCallable: true, Arity: 2),
                    ["indexOf"] = new("indexOf", BuiltinValueKind.BytesIndexOf, IsCallable: true, Arity: 3),
                    ["compare"] = new("compare", BuiltinValueKind.BytesCompare, IsCallable: true, Arity: 2),
                    ["scanHash"] = new("scanHash", BuiltinValueKind.BytesScanHash, IsCallable: true, Arity: 3),
                    ["subText"] = new("subText", BuiltinValueKind.BytesSubText, IsCallable: true, Arity: 3),
                    ["subView"] = new("subView", BuiltinValueKind.BytesSubView, IsCallable: true, Arity: 3),
                    ["append"] = new("append", BuiltinValueKind.BytesAppend, IsCallable: true, Arity: 2),
                    ["appendByte"] = new("appendByte", BuiltinValueKind.BytesAppendByte, IsCallable: true, Arity: 2),
                    ["fromList"] = new("fromList", BuiltinValueKind.BytesFromList, IsCallable: true, Arity: 1),
                    ["fromText"] = new("fromText", BuiltinValueKind.BytesFromText, IsCallable: true, Arity: 1),
                    ["hash"] = new("hash", BuiltinValueKind.BytesHash, IsCallable: true, Arity: 1),
                    ["u16Le"] = new("u16Le", BuiltinValueKind.BytesU16Le, IsCallable: true, Arity: 1),
                    ["u32Le"] = new("u32Le", BuiltinValueKind.BytesU32Le, IsCallable: true, Arity: 1),
                    ["u64Le"] = new("u64Le", BuiltinValueKind.BytesU64Le, IsCallable: true, Arity: 1),
                    ["getU16Le"] = new("getU16Le", BuiltinValueKind.BytesGetU16Le, IsCallable: true, Arity: 2),
                    ["getU32Le"] = new("getU32Le", BuiltinValueKind.BytesGetU32Le, IsCallable: true, Arity: 2),
                    ["getU64Le"] = new("getU64Le", BuiltinValueKind.BytesGetU64Le, IsCallable: true, Arity: 2)
                }),
            ["Ashes.Number.UInt"] = new(
                "Ashes.Number.UInt",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["toInt"] = new("toInt", BuiltinValueKind.UIntToInt, IsCallable: true, Arity: 1),
                    ["fromInt"] = new("fromInt", BuiltinValueKind.UIntFromInt, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Net.Http"] = new(
                "Ashes.Net.Http",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["get"] = new("get", BuiltinValueKind.HttpGet, IsCallable: true, Arity: 1),
                    ["post"] = new("post", BuiltinValueKind.HttpPost, IsCallable: true, Arity: 2)
                }),
            ["Ashes.Net.Tcp"] = new(
                "Ashes.Net.Tcp",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["connect"] = new("connect", BuiltinValueKind.NetTcpConnect, IsCallable: true, Arity: 2),
                    ["send"] = new("send", BuiltinValueKind.NetTcpSend, IsCallable: true, Arity: 2),
                    ["receive"] = new("receive", BuiltinValueKind.NetTcpReceive, IsCallable: true, Arity: 2),
                    ["close"] = new("close", BuiltinValueKind.NetTcpClose, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Net.Tcp.Server"] = new(
                "Ashes.Net.Tcp.Server",
                "Ashes.Semantics.StdLib.Ashes.Net.Tcp.Server.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["listen"] = new("listen", BuiltinValueKind.NetTcpListen, IsCallable: true, Arity: 1),
                    ["accept"] = new("accept", BuiltinValueKind.NetTcpAccept, IsCallable: true, Arity: 1),
                    ["forkWorkers"] = new("forkWorkers", BuiltinValueKind.NetTcpForkWorkers, IsCallable: true, Arity: 2),
                    ["setDrainTimeout"] = new("setDrainTimeout", BuiltinValueKind.NetTcpSetDrainTimeout, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Net.Http.Server"] = new(
                "Ashes.Net.Http.Server",
                "Ashes.Semantics.StdLib.Ashes.Net.Http.Server.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Net.Tls.Server"] = new(
                "Ashes.Net.Tls.Server",
                "Ashes.Semantics.StdLib.Ashes.Net.Tls.Server.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["handshake"] = new("handshake", BuiltinValueKind.NetTlsServerHandshake, IsCallable: true, Arity: 3)
                }),
            ["Ashes.Net.Tls"] = new(
                "Ashes.Net.Tls",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["connect"] = new("connect", BuiltinValueKind.NetTlsConnect, IsCallable: true, Arity: 2),
                    ["send"] = new("send", BuiltinValueKind.NetTlsSend, IsCallable: true, Arity: 2),
                    ["receive"] = new("receive", BuiltinValueKind.NetTlsReceive, IsCallable: true, Arity: 2),
                    ["close"] = new("close", BuiltinValueKind.NetTlsClose, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Task"] = new(
                "Ashes.Task",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["run"] = new("run", BuiltinValueKind.AsyncRun, IsCallable: true, Arity: 1),
                    ["task"] = new("task", BuiltinValueKind.AsyncTask, IsCallable: true, Arity: 1),
                    ["fromResult"] = new("fromResult", BuiltinValueKind.AsyncFromResult, IsCallable: true, Arity: 1),
                    ["sleep"] = new("sleep", BuiltinValueKind.AsyncSleep, IsCallable: true, Arity: 1),
                    ["all"] = new("all", BuiltinValueKind.AsyncAll, IsCallable: true, Arity: 1),
                    ["spawn"] = new("spawn", BuiltinValueKind.AsyncSpawn, IsCallable: true, Arity: 1),
                    ["race"] = new("race", BuiltinValueKind.AsyncRace, IsCallable: true, Arity: 1)
                }),
            ["Ashes.IO.Process"] = new(
                "Ashes.IO.Process",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["spawn"] = new("spawn", BuiltinValueKind.SpawnProcess, IsCallable: true, Arity: 2),
                    ["writeStdin"] = new("writeStdin", BuiltinValueKind.ProcessWriteStdin, IsCallable: true, Arity: 2),
                    ["readStdoutLine"] = new("readStdoutLine", BuiltinValueKind.ProcessReadStdoutLine, IsCallable: true, Arity: 1),
                    ["readStderrLine"] = new("readStderrLine", BuiltinValueKind.ProcessReadStderrLine, IsCallable: true, Arity: 1),
                    ["waitForExit"] = new("waitForExit", BuiltinValueKind.ProcessWaitForExit, IsCallable: true, Arity: 1),
                    ["kill"] = new("kill", BuiltinValueKind.ProcessKill, IsCallable: true, Arity: 1)
                }),
            ["Ashes.IO.Console"] = new(
                "Ashes.IO.Console",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["enableRawInput"] = new("enableRawInput", BuiltinValueKind.ConsoleEnableRaw, IsCallable: true, Arity: 1),
                    ["restoreInput"] = new("restoreInput", BuiltinValueKind.ConsoleRestore, IsCallable: true, Arity: 1),
                    ["pollInput"] = new("pollInput", BuiltinValueKind.ConsolePoll, IsCallable: true, Arity: 1),
                    ["monotonicMillis"] = new("monotonicMillis", BuiltinValueKind.ConsoleMonotonicMillis, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Text.Json"] = new(
                "Ashes.Text.Json",
                "Ashes.Semantics.StdLib.Ashes.Text.Json.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Net.Rpc"] = new(
                "Ashes.Net.Rpc",
                "Ashes.Semantics.StdLib.Ashes.Net.Rpc.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            // Ashes.Text.Regex is backed by PCRE2. The native members below are the low-level primitives
            // (a compiled pattern is a pcre2_code* carried as an Int handle); the ergonomic pattern-
            // string API (compile/isMatch/find/findAll/captures/replace and the Regex type) is defined
            // on top of them in Regex.ash, which references them as Ashes.Text.Regex.<primitive>.
            ["Ashes.Text.Regex"] = new(
                "Ashes.Text.Regex",
                "Ashes.Semantics.StdLib.Ashes.Text.Regex.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["compileRaw"] = new("compileRaw", BuiltinValueKind.RegexCompile, IsCallable: true, Arity: 1),
                    ["compileError"] = new("compileError", BuiltinValueKind.RegexCompileError, IsCallable: true, Arity: 1),
                    ["findFrom"] = new("findFrom", BuiltinValueKind.RegexFind, IsCallable: true, Arity: 3),
                    ["capturesFrom"] = new("capturesFrom", BuiltinValueKind.RegexCaptures, IsCallable: true, Arity: 3),
                    ["substituteAll"] = new("substituteAll", BuiltinValueKind.RegexSubstitute, IsCallable: true, Arity: 3)
                })
        };

    /// <summary>
    /// Resource type names. Resources are types that represent external handles
    /// (files, sockets) and require deterministic cleanup via Drop.
    /// Internal compiler concept — not exposed to user code.
    /// </summary>
    private static readonly HashSet<string> ResourceTypeNames = new(StringComparer.Ordinal)
    {
        "Socket",
        "TlsSocket",
        "Process",
        "FileHandle"
    };

    private static readonly IReadOnlyDictionary<string, BuiltinType> TypesByName = CreateBuiltinTypes();

    /// <summary>
    /// Per-module export-name sets, computed once from the same knowledge the registry uses to
    /// expose qualified members: intrinsic modules contribute their <see cref="BuiltinModule.Members"/>
    /// keys, and resource-backed modules contribute the top-level <c>let</c> binding and <c>type</c>
    /// names parsed from their embedded <c>.ash</c> source. The import resolver queries this to
    /// validate <c>import Ashes.IO.print</c>-style selectors against built-in modules.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> ModuleExportsByName =
        new(BuildModuleExports);

    public static IReadOnlyCollection<string> StandardModuleNames => ModulesByName.Keys.ToArray();

    public static IReadOnlyCollection<BuiltinType> Types => TypesByName.Values.ToArray();

    /// <summary>
    /// Returns true if the given type name is classified as a resource type.
    /// Resource types require deterministic cleanup (Drop) at scope exit.
    /// </summary>
    public static bool IsResourceTypeName(string typeName)
    {
        return ResourceTypeNames.Contains(typeName);
    }

    /// <summary>
    /// Returns true if the given type is a copy type (stack-allocated, trivially duplicated).
    /// Copy types: Int, Float, Bool.
    /// Copy types do NOT get Drop instructions.
    /// </summary>
    public static bool IsCopyType(TypeRef prunedType)
    {
        return prunedType is TypeRef.TInt or TypeRef.TFloat or TypeRef.TBool or TypeRef.TOpaque or TypeRef.TPtr;
    }

    /// <summary>
    /// Returns true if the given type is an owned type (heap-allocated, requires cleanup).
    /// Owned types: String, List, Tuple, Function (closures), named types (ADTs incl. resources).
    /// Owned types get Drop instructions at scope exit.
    /// </summary>
    public static bool IsOwnedType(TypeRef prunedType)
    {
        return prunedType is TypeRef.TStr
            or TypeRef.TBytes
            or TypeRef.TList
            or TypeRef.TTuple
            or TypeRef.TFun
            or TypeRef.TNamedType;
    }

    public static bool TryGetModule(string moduleName, out BuiltinModule module)
    {
        if (ModulesByName.TryGetValue(moduleName, out BuiltinModule? resolved))
        {
            module = resolved;
            return true;
        }

        module = null!;
        return false;
    }

    /// <summary>
    /// Surfaces the set of names a built-in module exports (its public value bindings and types),
    /// so the import resolver can validate selector imports such as <c>import Ashes.IO.print</c>
    /// against built-in modules with the same query it uses for user modules. Returns false when
    /// the module is not a known built-in module.
    /// </summary>
    public static bool TryGetModuleExports(string moduleName, out IReadOnlySet<string> exportNames)
    {
        if (ModuleExportsByName.Value.TryGetValue(moduleName, out var exports))
        {
            exportNames = exports;
            return true;
        }

        exportNames = EmptyExports;
        return false;
    }

    /// <summary>
    /// Returns true when the named built-in module exports the given name. Returns false for
    /// unknown modules and for names the module does not export.
    /// </summary>
    public static bool ModuleExportsName(string moduleName, string name)
    {
        return ModuleExportsByName.Value.TryGetValue(moduleName, out var exports)
            && exports.Contains(name);
    }

    public static bool TryGetType(string typeName, out BuiltinType type)
    {
        if (TypesByName.TryGetValue(typeName, out BuiltinType? resolved))
        {
            type = resolved;
            return true;
        }

        type = null!;
        return false;
    }

    public static bool IsBuiltinModule(string moduleName)
    {
        return ModulesByName.ContainsKey(moduleName);
    }

    public static bool IsReservedModuleNamespace(string moduleName)
    {
        return string.Equals(moduleName, "Ashes", StringComparison.Ordinal)
            || moduleName.StartsWith("Ashes.", StringComparison.Ordinal);
    }

    public static bool IsReservedTypeName(string typeName)
    {
        return string.Equals(typeName, "Ashes", StringComparison.Ordinal)
            || TypesByName.ContainsKey(typeName)
            || PrimitiveTypeNames.Contains(typeName);
    }

    public static bool TryGetPrimitiveType(string typeName, out TypeRef type)
    {
        if (string.Equals(typeName, "Float", StringComparison.Ordinal))
        {
            type = new TypeRef.TFloat();
            return true;
        }

        if (string.Equals(typeName, "Bytes", StringComparison.Ordinal))
        {
            type = new TypeRef.TBytes();
            return true;
        }

        type = null!;
        return false;
    }

    private static readonly IReadOnlySet<string> EmptyExports =
        new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildModuleExports()
    {
        var exportsByModule = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var module in ModulesByName.Values)
        {
            var exports = new HashSet<string>(StringComparer.Ordinal);

            // Intrinsic members: the same data backing qualified `Ashes.IO.print` resolution.
            foreach (var memberName in module.Members.Keys)
            {
                exports.Add(memberName);
            }

            // Resource-backed modules: the public top-level bindings/types parsed from the same
            // embedded `.ash` source the registry exposes to qualified access.
            if (module.ResourceName is not null)
            {
                CollectResourceModuleExports(module.ResourceName, exports);
            }

            exportsByModule[module.Name] = exports;
        }

        return exportsByModule;
    }

    private static void CollectResourceModuleExports(string resourceName, HashSet<string> exports)
    {
        var source = LoadEmbeddedResource(resourceName);
        if (source is null)
        {
            return;
        }

        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        if (diagnostics.StructuredErrors.Count > 0)
        {
            return;
        }

        // Model-A top-level declarations: a module exports its top-level `let`/`type` items only.
        foreach (var item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl letDecl:
                    exports.Add(letDecl.Name);
                    break;
                case TopLevelItem.RecursiveGroup recursiveGroup:
                    foreach (var (name, _) in recursiveGroup.Bindings)
                    {
                        exports.Add(name);
                    }

                    break;
                case TopLevelItem.Type typeDecl:
                    exports.Add(typeDecl.Decl.Name);
                    break;
            }
        }

        // Legacy pyramid modules carry their bindings as a trailing `let ... in` chain rather than
        // top-level items. Walk the chain spine (never descending into binding values) so those
        // names are exposed identically to how qualified access resolves them today.
        for (var expr = program.Body; expr is not null;)
        {
            switch (expr)
            {
                case Expr.Let letExpr:
                    exports.Add(letExpr.Name);
                    expr = letExpr.Body;
                    break;
                case Expr.LetRecursive letRecursiveExpr:
                    exports.Add(letRecursiveExpr.Name);
                    expr = letRecursiveExpr.Body;
                    break;
                default:
                    expr = null;
                    break;
            }
        }
    }

    private static string? LoadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(BuiltinRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyDictionary<string, BuiltinType> CreateBuiltinTypes()
    {
        return new Dictionary<string, BuiltinType>(StringComparer.Ordinal)
        {
            ["Unit"] = CreateUnitBuiltinType(),
            ["List"] = CreateListBuiltinType(),
            ["Maybe"] = CreateMaybeBuiltinType(),
            ["Result"] = CreateResultBuiltinType(),
            ["Socket"] = CreateSocketBuiltinType(),
            ["TlsSocket"] = CreateTlsSocketBuiltinType(),
            ["Task"] = CreateTaskBuiltinType(),
            ["Process"] = CreateProcessBuiltinType(),
            ["FileHandle"] = CreateFileHandleBuiltinType()
        };
    }

    private static BuiltinType CreateUnitBuiltinType()
    {
        var unitTypeParameters = Array.Empty<TypeParameterSymbol>();
        var unitDecl = new TypeDecl(
            "Unit",
            [],
            [new TypeConstructor("Unit", [])]);

        return new BuiltinType(
            "Unit",
            unitTypeParameters,
            [
                new BuiltinConstructor(
                    "Unit",
                    [],
                    unitDecl.Constructors[0])
            ],
            unitDecl);
    }

    private static BuiltinType CreateListBuiltinType()
    {
        var listTypeParameters = new[]
        {
            new TypeParameterSymbol("T")
        };
        var listDecl = new TypeDecl(
            "List",
            [new TypeParameter("T")],
            []);

        return new BuiltinType(
            "List",
            listTypeParameters,
            [],
            listDecl);
    }

    private static BuiltinType CreateMaybeBuiltinType()
    {
        var maybeTypeParameters = new[]
        {
            new TypeParameterSymbol("T")
        };
        var maybeDecl = new TypeDecl(
            "Maybe",
            [new TypeParameter("T")],
            [
                new TypeConstructor("None", []),
                new TypeConstructor("Some", [new TypeExpr.Named("T")])
            ]);

        return new BuiltinType(
            "Maybe",
            maybeTypeParameters,
            [
                new BuiltinConstructor(
                    "None",
                    [],
                    maybeDecl.Constructors[0]),
                new BuiltinConstructor(
                    "Some",
                    [new TypeRef.TTypeParam(maybeTypeParameters[0])],
                    maybeDecl.Constructors[1])
            ],
            maybeDecl);
    }

    private static BuiltinType CreateResultBuiltinType()
    {
        var resultTypeParameters = new[]
        {
            new TypeParameterSymbol("E"),
            new TypeParameterSymbol("A")
        };
        var resultDecl = new TypeDecl(
            "Result",
            [new TypeParameter("E"), new TypeParameter("A")],
            [
                new TypeConstructor("Ok", [new TypeExpr.Named("A")]),
                new TypeConstructor("Error", [new TypeExpr.Named("E")])
            ]);

        return new BuiltinType(
            "Result",
            resultTypeParameters,
            [
                new BuiltinConstructor(
                    "Ok",
                    [new TypeRef.TTypeParam(resultTypeParameters[1])],
                    resultDecl.Constructors[0]),
                new BuiltinConstructor(
                    "Error",
                    [new TypeRef.TTypeParam(resultTypeParameters[0])],
                    resultDecl.Constructors[1])
            ],
            resultDecl);
    }

    private static BuiltinType CreateSocketBuiltinType()
    {
        var socketTypeParameters = Array.Empty<TypeParameterSymbol>();
        var socketDecl = new TypeDecl(
            "Socket",
            [],
            []);

        return new BuiltinType(
            "Socket",
            socketTypeParameters,
            [],
            socketDecl);
    }

    private static BuiltinType CreateTlsSocketBuiltinType()
    {
        var tlsSocketTypeParameters = Array.Empty<TypeParameterSymbol>();
        var tlsSocketDecl = new TypeDecl(
            "TlsSocket",
            [],
            []);

        return new BuiltinType(
            "TlsSocket",
            tlsSocketTypeParameters,
            [],
            tlsSocketDecl);
    }

    private static BuiltinType CreateTaskBuiltinType()
    {
        var taskTypeParameters = new[]
        {
            new TypeParameterSymbol("E"),
            new TypeParameterSymbol("A")
        };
        var taskDecl = new TypeDecl(
            "Task",
            [new TypeParameter("E"), new TypeParameter("A")],
            []);

        return new BuiltinType(
            "Task",
            taskTypeParameters,
            [],
            taskDecl);
    }

    private static BuiltinType CreateProcessBuiltinType()
    {
        var processTypeParameters = Array.Empty<TypeParameterSymbol>();
        var processDecl = new TypeDecl(
            "Process",
            [],
            []);

        return new BuiltinType(
            "Process",
            processTypeParameters,
            [],
            processDecl);
    }

    private static BuiltinType CreateFileHandleBuiltinType()
    {
        var fileHandleTypeParameters = Array.Empty<TypeParameterSymbol>();
        var fileHandleDecl = new TypeDecl(
            "FileHandle",
            [],
            []);

        return new BuiltinType(
            "FileHandle",
            fileHandleTypeParameters,
            [],
            fileHandleDecl);
    }
}
