using Ashes.Frontend;

namespace Ashes.Semantics;

public static class BuiltinRegistry
{
    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    {
        "Float"
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
        TextUncons,
        TextParseInt,
        TextParseFloat,
        TextFromInt,
        TextFromFloat,
        TextToHex,
        HttpGet,
        HttpPost,
        NetTcpConnect,
        NetTcpSend,
        NetTcpReceive,
        NetTcpClose,
        NetTlsConnect,
        NetTlsSend,
        NetTlsReceive,
        NetTlsClose,
        AsyncRun,
        AsyncTask,
        AsyncFromResult,
        AsyncSleep,
        AsyncAll,
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
        FileWriteBytes,
        IoReadExact,
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
                    ["writeLine"] = new("writeLine", BuiltinValueKind.WriteLine, IsCallable: true, Arity: 1),
                    ["readLine"] = new("readLine", BuiltinValueKind.ReadLine, IsCallable: true, Arity: 1),
                    ["readExact"] = new("readExact", BuiltinValueKind.IoReadExact, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Result"] = new(
                "Ashes.Result",
                "Ashes.Semantics.StdLib.Ashes.Result.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.String"] = new(
                "Ashes.String",
                "Ashes.Semantics.StdLib.Ashes.String.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.List"] = new(
                "Ashes.List",
                "Ashes.Semantics.StdLib.Ashes.List.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Math"] = new(
                "Ashes.Math",
                "Ashes.Semantics.StdLib.Ashes.Math.ash",
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
            ["Ashes.Array"] = new(
                "Ashes.Array",
                "Ashes.Semantics.StdLib.Ashes.Array.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Map"] = new(
                "Ashes.Map",
                "Ashes.Semantics.StdLib.Ashes.Map.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.HashMap"] = new(
                "Ashes.HashMap",
                "Ashes.Semantics.StdLib.Ashes.HashMap.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.HashTrie"] = new(
                "Ashes.HashTrie",
                "Ashes.Semantics.StdLib.Ashes.HashTrie.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Parallel"] = new(
                "Ashes.Parallel",
                "Ashes.Semantics.StdLib.Ashes.Parallel.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    // Hybrid module: `both` is a compiler intrinsic (lowered at each call site so it
                    // can deep-copy a worker's result at the concrete result type); `map`/`reduce`/
                    // helpers come from the embedded source.
                    ["both"] = new("both", BuiltinValueKind.ParallelBoth, IsCallable: true, Arity: 2)
                }),
            ["Ashes.Maybe"] = new(
                "Ashes.Maybe",
                "Ashes.Semantics.StdLib.Ashes.Maybe.ash",
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
            ["Ashes.File"] = new(
                "Ashes.File",
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
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["uncons"] = new("uncons", BuiltinValueKind.TextUncons, IsCallable: true, Arity: 1),
                    ["parseInt"] = new("parseInt", BuiltinValueKind.TextParseInt, IsCallable: true, Arity: 1),
                    ["parseFloat"] = new("parseFloat", BuiltinValueKind.TextParseFloat, IsCallable: true, Arity: 1),
                    ["fromInt"] = new("fromInt", BuiltinValueKind.TextFromInt, IsCallable: true, Arity: 1),
                    ["fromFloat"] = new("fromFloat", BuiltinValueKind.TextFromFloat, IsCallable: true, Arity: 1),
                    ["toHex"] = new("toHex", BuiltinValueKind.TextToHex, IsCallable: true, Arity: 1),
                    ["byteLength"] = new("byteLength", BuiltinValueKind.TextByteLength, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Bytes"] = new(
                "Ashes.Bytes",
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
            ["Ashes.UInt"] = new(
                "Ashes.UInt",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["toInt"] = new("toInt", BuiltinValueKind.UIntToInt, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Http"] = new(
                "Ashes.Http",
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
            ["Ashes.Async"] = new(
                "Ashes.Async",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["run"] = new("run", BuiltinValueKind.AsyncRun, IsCallable: true, Arity: 1),
                    ["task"] = new("task", BuiltinValueKind.AsyncTask, IsCallable: true, Arity: 1),
                    ["fromResult"] = new("fromResult", BuiltinValueKind.AsyncFromResult, IsCallable: true, Arity: 1),
                    ["sleep"] = new("sleep", BuiltinValueKind.AsyncSleep, IsCallable: true, Arity: 1),
                    ["all"] = new("all", BuiltinValueKind.AsyncAll, IsCallable: true, Arity: 1),
                    ["race"] = new("race", BuiltinValueKind.AsyncRace, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Process"] = new(
                "Ashes.Process",
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
            ["Ashes.Json"] = new(
                "Ashes.Json",
                "Ashes.Semantics.StdLib.Ashes.Json.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Rpc"] = new(
                "Ashes.Rpc",
                "Ashes.Semantics.StdLib.Ashes.Rpc.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Regex"] = new(
                "Ashes.Regex",
                "Ashes.Semantics.StdLib.Ashes.Regex.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal))
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
        return ModulesByName.TryGetValue(moduleName, out module!);
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
        return TypesByName.TryGetValue(typeName, out type!);
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
                case TopLevelItem.RecGroup recGroup:
                    foreach (var (name, _) in recGroup.Bindings)
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
                case Expr.LetRec letRecExpr:
                    exports.Add(letRecExpr.Name);
                    expr = letRecExpr.Body;
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
        var unitTypeParameters = Array.Empty<TypeParameterSymbol>();
        var unitDecl = new TypeDecl(
            "Unit",
            [],
            [new TypeConstructor("Unit", [])]);

        var listTypeParameters = new[]
        {
            new TypeParameterSymbol("T")
        };
        var listDecl = new TypeDecl(
            "List",
            [new TypeParameter("T")],
            []);

        var maybeTypeParameters = new[]
        {
            new TypeParameterSymbol("T")
        };
        var maybeDecl = new TypeDecl(
            "Maybe",
            [new TypeParameter("T")],
            [
                new TypeConstructor("None", []),
                new TypeConstructor("Some", ["T"])
            ]);

        var resultTypeParameters = new[]
        {
            new TypeParameterSymbol("E"),
            new TypeParameterSymbol("A")
        };
        var resultDecl = new TypeDecl(
            "Result",
            [new TypeParameter("E"), new TypeParameter("A")],
            [
                new TypeConstructor("Ok", ["A"]),
                new TypeConstructor("Error", ["E"])
            ]);

        var socketTypeParameters = Array.Empty<TypeParameterSymbol>();
        var socketDecl = new TypeDecl(
            "Socket",
            [],
            []);

        var tlsSocketTypeParameters = Array.Empty<TypeParameterSymbol>();
        var tlsSocketDecl = new TypeDecl(
            "TlsSocket",
            [],
            []);

        var taskTypeParameters = new[]
        {
            new TypeParameterSymbol("E"),
            new TypeParameterSymbol("A")
        };
        var taskDecl = new TypeDecl(
            "Task",
            [new TypeParameter("E"), new TypeParameter("A")],
            []);

        var processTypeParameters = Array.Empty<TypeParameterSymbol>();
        var processDecl = new TypeDecl(
            "Process",
            [],
            []);

        var fileHandleTypeParameters = Array.Empty<TypeParameterSymbol>();
        var fileHandleDecl = new TypeDecl(
            "FileHandle",
            [],
            []);

        return new Dictionary<string, BuiltinType>(StringComparer.Ordinal)
        {
            ["Unit"] = new(
                "Unit",
                unitTypeParameters,
                [
                    new BuiltinConstructor(
                        "Unit",
                        [],
                        unitDecl.Constructors[0])
                ],
                unitDecl),
            ["List"] = new(
                "List",
                listTypeParameters,
                [],
                listDecl),
            ["Maybe"] = new(
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
                maybeDecl),
            ["Result"] = new(
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
                resultDecl),
            ["Socket"] = new(
                "Socket",
                socketTypeParameters,
                [],
                socketDecl),
            ["TlsSocket"] = new(
                "TlsSocket",
                tlsSocketTypeParameters,
                [],
                tlsSocketDecl),
            ["Task"] = new(
                "Task",
                taskTypeParameters,
                [],
                taskDecl),
            ["Process"] = new(
                "Process",
                processTypeParameters,
                [],
                processDecl),
            ["FileHandle"] = new(
                "FileHandle",
                fileHandleTypeParameters,
                [],
                fileHandleDecl)
        };
    }
}
