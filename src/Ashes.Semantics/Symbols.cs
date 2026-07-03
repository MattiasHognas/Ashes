using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed record TypeParameterSymbol(string Name);

public sealed record ConstructorSymbol(
    string Name,
    string ParentType,
    int Arity,
    IReadOnlyList<TypeRef> ParameterTypes,
    TypeConstructor DeclaringSyntax,
    bool IsBuiltin = false
);

public sealed record TypeSymbol(
    string Name,
    IReadOnlyList<TypeParameterSymbol> TypeParameters,
    IReadOnlyList<ConstructorSymbol> Constructors,
    TypeDecl DeclaringSyntax,
    bool IsBuiltin = false
);

/// <summary>
/// One operation of a declared effect. <see cref="DeclaredSignature"/> is the resolved signature
/// with the effect's type parameters as <see cref="TypeRef.TTypeParam"/> placeholders, or null for
/// an unsigned operation whose type is inferred by unifying its uses (<see cref="InferredType"/> is
/// the shared inference variable in that case).
/// </summary>
public sealed record EffectOperationSymbol(string Name, TypeRef? DeclaredSignature, TypeRef? InferredType);

/// <summary>A declared effect: a named set of operations, optionally parameterized.</summary>
public sealed record EffectSymbol(
    string Name,
    IReadOnlyList<TypeParameterSymbol> TypeParameters,
    IReadOnlyDictionary<string, EffectOperationSymbol> Operations,
    EffectDecl DeclaringSyntax
)
{
    // Rows compare and dedupe effects by name; the record's structural equality over the
    // operations dictionary is neither needed nor meaningful.
    public bool Equals(EffectSymbol? other)
    {
        return other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Name);
    }
}
