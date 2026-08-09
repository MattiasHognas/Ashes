using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

internal abstract record AshesType
{
    internal sealed record Primitive(string Name) : AshesType;
    internal sealed record UInt(int Bits) : AshesType;
    internal sealed record Tuple(IReadOnlyList<AshesType> Elements) : AshesType;
    internal sealed record List(AshesType Element) : AshesType;
    internal sealed record Function(AshesType Parameter, AshesType Return) : AshesType;
    internal sealed record GenericParameter(int Index) : AshesType;
    internal sealed record Adt(string Name, IReadOnlyList<AshesType> Arguments) : AshesType;
    internal sealed record Record(string Name) : AshesType;
    internal sealed record Result(AshesType Error, AshesType Value) : AshesType;
    internal sealed record Task(AshesType Error, AshesType Value) : AshesType;
    internal sealed record Resource(string Name) : AshesType;

    internal static Primitive Int { get; } = new("Int");
    internal static Primitive Bool { get; } = new("Bool");
    internal static Primitive Str { get; } = new("Str");
    internal static Primitive Float { get; } = new("Float");
    internal static Primitive BigInt { get; } = new("BigInt");
    internal static Primitive Unit { get; } = new("Unit");
    internal static Resource FileHandle { get; } = new("FileHandle");
    internal static Resource Socket { get; } = new("Socket");
    internal static Resource TlsSocket { get; } = new("TlsSocket");
    internal static IReadOnlyList<Resource> SupportedResources { get; } = [FileHandle, Socket, TlsSocket];

    internal TypeExpr ToSyntax() => this switch
    {
        Primitive primitive => new TypeExpr.Named(primitive.Name),
        UInt unsigned => new TypeExpr.Named($"u{unsigned.Bits}"),
        Tuple tuple => new TypeExpr.TupleType(tuple.Elements.Select(element => element.ToSyntax()).ToArray()),
        List list => new TypeExpr.Applied("List", [list.Element.ToSyntax()]),
        Function function => new TypeExpr.Arrow(function.Parameter.ToSyntax(), function.Return.ToSyntax()),
        GenericParameter parameter => throw new InvalidOperationException($"Unresolved fuzz type parameter {parameter.Index}."),
        Adt adt => new TypeExpr.Applied(adt.Name, adt.Arguments.Select(argument => argument.ToSyntax()).ToArray()),
        Record record => new TypeExpr.Named(record.Name),
        Result result => new TypeExpr.Applied("Result", [result.Error.ToSyntax(), result.Value.ToSyntax()]),
        Task task => new TypeExpr.Applied("Task", [task.Error.ToSyntax(), task.Value.ToSyntax()]),
        Resource resource => new TypeExpr.Named(resource.Name),
        _ => throw new InvalidOperationException("Unknown fuzz generation type."),
    };

    public sealed override string ToString() => this switch
    {
        Primitive primitive => primitive.Name,
        UInt unsigned => $"UInt{unsigned.Bits}",
        Tuple tuple => $"({string.Join(",", tuple.Elements)})",
        List list => $"List({list.Element})",
        Function function => $"{function.Parameter}->{function.Return}",
        GenericParameter parameter => $"T{parameter.Index}",
        Adt adt => adt.Arguments.Count == 0 ? adt.Name : $"{adt.Name}({string.Join(",", adt.Arguments)})",
        Record record => record.Name,
        Result result => $"Result({result.Error},{result.Value})",
        Task task => $"Task({task.Error},{task.Value})",
        Resource resource => resource.Name,
        _ => base.ToString() ?? "unknown",
    };
}
