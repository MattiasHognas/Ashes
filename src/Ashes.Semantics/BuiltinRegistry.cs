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
        FileWriteText,
        FileExists,
        HttpGet,
        HttpPost,
        NetTcpConnect,
        NetTcpSend,
        NetTcpReceive,
        NetTcpClose,
        AsyncRun,
        AsyncFromResult,
        AsyncSleep,
        AsyncAll,
        AsyncRace
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
                    ["readLine"] = new("readLine", BuiltinValueKind.ReadLine, IsCallable: true, Arity: 1)
                }),
            ["Ashes.Result"] = new(
                "Ashes.Result",
                "Ashes.Semantics.StdLib.Ashes.Result.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.List"] = new(
                "Ashes.List",
                "Ashes.Semantics.StdLib.Ashes.List.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Maybe"] = new(
                "Ashes.Maybe",
                "Ashes.Semantics.StdLib.Ashes.Maybe.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Test"] = new(
                "Ashes.Test",
                "Ashes.Semantics.StdLib.Ashes.Test.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.File"] = new(
                "Ashes.File",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["readText"] = new("readText", BuiltinValueKind.FileReadText, IsCallable: true, Arity: 1),
                    ["writeText"] = new("writeText", BuiltinValueKind.FileWriteText, IsCallable: true, Arity: 2),
                    ["exists"] = new("exists", BuiltinValueKind.FileExists, IsCallable: true, Arity: 1)
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
            ["Ashes.Async"] = new(
                "Ashes.Async",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["run"] = new("run", BuiltinValueKind.AsyncRun, IsCallable: true, Arity: 1),
                    ["fromResult"] = new("fromResult", BuiltinValueKind.AsyncFromResult, IsCallable: true, Arity: 1),
                    ["sleep"] = new("sleep", BuiltinValueKind.AsyncSleep, IsCallable: true, Arity: 1),
                    ["all"] = new("all", BuiltinValueKind.AsyncAll, IsCallable: true, Arity: 1),
                    ["race"] = new("race", BuiltinValueKind.AsyncRace, IsCallable: true, Arity: 1)
                })
        };

    /// <summary>
    /// Resource type names. Resources are types that represent external handles
    /// (files, sockets) and require deterministic cleanup via Drop.
    /// Internal compiler concept — not exposed to user code.
    /// </summary>
    private static readonly HashSet<string> ResourceTypeNames = new(StringComparer.Ordinal)
    {
        "Socket"
    };

    private static readonly IReadOnlyDictionary<string, BuiltinType> TypesByName = CreateBuiltinTypes();

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
        return prunedType is TypeRef.TInt or TypeRef.TFloat or TypeRef.TBool;
    }

    /// <summary>
    /// Returns true if the given type is an owned type (heap-allocated, requires cleanup).
    /// Owned types: String, List, Tuple, Function (closures), named types (ADTs incl. resources).
    /// Owned types get Drop instructions at scope exit.
    /// </summary>
    public static bool IsOwnedType(TypeRef prunedType)
    {
        return prunedType is TypeRef.TStr
            or TypeRef.TList
            or TypeRef.TTuple
            or TypeRef.TFun
            or TypeRef.TNamedType;
    }

    public static bool TryGetModule(string moduleName, out BuiltinModule module)
    {
        return ModulesByName.TryGetValue(moduleName, out module!);
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

        var taskTypeParameters = new[]
        {
            new TypeParameterSymbol("E"),
            new TypeParameterSymbol("A")
        };
        var taskDecl = new TypeDecl(
            "Task",
            [new TypeParameter("E"), new TypeParameter("A")],
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
            ["Task"] = new(
                "Task",
                taskTypeParameters,
                [],
                taskDecl)
        };
    }
}
