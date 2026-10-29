namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool ResultTypeReachEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_RESULTTYPEREACH") is null;

    /// <summary>
    /// Whether a value of <paramref name="resultType"/> can hold an owned value of
    /// <paramref name="ownedType"/> or one of its heap parts: a value of some heap type the owned
    /// value is built from may be reachable inside the result. An owned type with a part of
    /// unresolved type or a function inside may share a value with any result.
    /// </summary>
    private bool ResultTypeMayHoldPartOf(TypeRef resultType, TypeRef ownedType)
    {
        if (!ResultTypeReachEnabled)
        {
            return true;
        }

        List<TypeRef> ownedParts = [];
        return !CollectHeapComponentTypes(ownedType, ownedParts, [])
            || ownedParts.Exists(part => CallResultMayContainArgumentType(resultType, part, new HashSet<string>(StringComparer.Ordinal)));
    }

    // Adds every heap type a value of the type is built from, itself included, and answers whether
    // all of them are known: false when some part may be a value of any type.
    private bool CollectHeapComponentTypes(TypeRef type, List<TypeRef> parts, HashSet<string> expanded)
    {
        TypeRef valueType = Prune(type);
        switch (valueType)
        {
            case TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBool or TypeRef.TRune or TypeRef.TOpaque or TypeRef.TNever:
                return true;
            case TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt:
                parts.Add(valueType);
                return true;
            case TypeRef.TList list:
                parts.Add(valueType);
                return CollectHeapComponentTypes(list.Element, parts, expanded);
            case TypeRef.TTuple tuple:
                parts.Add(valueType);
                return tuple.Elements.All(element => CollectHeapComponentTypes(element, parts, expanded));
            case TypeRef.TNamedType named:
                parts.Add(valueType);
                return !expanded.Add(named.Symbol.Name)
                    || named.Symbol.Constructors.All(constructor =>
                        Enumerable.Range(0, constructor.Arity).All(index =>
                            CollectHeapComponentTypes(InstantiatedFieldType(constructor, index, named, instantiated: true), parts, expanded)));
            default:
                return false;
        }
    }
}
