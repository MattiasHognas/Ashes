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
        FsReadText,
        FsWriteText,
        FsExists,
        NetTcpConnect,
        NetTcpSend,
        NetTcpReceive,
        NetTcpClose
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
            ["Ashes.Test"] = new(
                "Ashes.Test",
                "Ashes.Semantics.StdLib.Ashes.Test.ash",
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)),
            ["Ashes.Fs"] = new(
                "Ashes.Fs",
                null,
                new Dictionary<string, BuiltinModuleMember>(StringComparer.Ordinal)
                {
                    ["readText"] = new("readText", BuiltinValueKind.FsReadText, IsCallable: true, Arity: 1),
                    ["writeText"] = new("writeText", BuiltinValueKind.FsWriteText, IsCallable: true, Arity: 2),
                    ["exists"] = new("exists", BuiltinValueKind.FsExists, IsCallable: true, Arity: 1)
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
                })
        };

    private static readonly IReadOnlyDictionary<string, BuiltinType> TypesByName = CreateBuiltinTypes();

    public static IReadOnlyCollection<string> StandardModuleNames => ModulesByName.Keys.ToArray();

    public static IReadOnlyCollection<BuiltinType> Types => TypesByName.Values.ToArray();

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

        var optionStringTypeParameters = Array.Empty<TypeParameterSymbol>();
        var optionStringDecl = new TypeDecl(
            "OptionString",
            [],
            [
                new TypeConstructor("None", []),
                new TypeConstructor("Some", ["Str"])
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
            ["OptionString"] = new(
                "OptionString",
                optionStringTypeParameters,
                [
                    new BuiltinConstructor(
                        "None",
                        [],
                        optionStringDecl.Constructors[0]),
                    new BuiltinConstructor(
                        "Some",
                        [new TypeRef.TStr()],
                        optionStringDecl.Constructors[1])
                ],
                optionStringDecl),
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
                socketDecl)
        };
    }
}
