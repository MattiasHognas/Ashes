using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>A bound generic type parameter of a type or capability declaration.</summary>
/// <param name="Name">The parameter's name as written (e.g. <c>T</c>).</param>
public sealed record TypeParameterSymbol(string Name);

/// <summary>A resolved data constructor of an algebraic data type.</summary>
/// <param name="Name">The constructor's name (e.g. <c>Some</c>).</param>
/// <param name="ParentType">The name of the type this constructor belongs to.</param>
/// <param name="Arity">The number of fields the constructor takes.</param>
/// <param name="ParameterTypes">The resolved types of the constructor's fields, in declaration order.</param>
/// <param name="DeclaringSyntax">The AST constructor node this symbol was resolved from.</param>
/// <param name="IsBuiltin">True when the constructor belongs to a compiler-provided built-in type rather than user source.</param>
public sealed record ConstructorSymbol(
    string Name,
    string ParentType,
    int Arity,
    IReadOnlyList<TypeRef> ParameterTypes,
    TypeConstructor DeclaringSyntax,
    bool IsBuiltin = false
);

/// <summary>A resolved type declaration: its name, generic parameters, and data constructors.</summary>
/// <param name="Name">The type's name.</param>
/// <param name="TypeParameters">The type's generic parameters, in order.</param>
/// <param name="Constructors">The type's data constructors; empty for opaque or resource types.</param>
/// <param name="DeclaringSyntax">The AST type declaration this symbol was resolved from.</param>
/// <param name="IsBuiltin">True when the type is compiler-provided rather than declared in user source.</param>
public sealed record TypeSymbol(
    string Name,
    IReadOnlyList<TypeParameterSymbol> TypeParameters,
    IReadOnlyList<ConstructorSymbol> Constructors,
    TypeDecl DeclaringSyntax,
    bool IsBuiltin = false
);

/// <summary>
/// One operation of a declared capability. <see cref="DeclaredSignature"/> is the resolved signature
/// with the capability's type parameters as <see cref="TypeRef.TTypeParam"/> placeholders, or null for
/// an unsigned operation whose type is inferred by unifying its uses (<see cref="InferredType"/> is
/// the shared inference variable in that case).
/// </summary>
public sealed record CapabilityOperationSymbol(string Name, TypeRef? DeclaredSignature, TypeRef? InferredType);

/// <summary>A declared capability: a named set of operations, optionally parameterized.</summary>
public sealed record CapabilitySymbol(
    string Name,
    IReadOnlyList<TypeParameterSymbol> TypeParameters,
    IReadOnlyDictionary<string, CapabilityOperationSymbol> Operations,
    CapabilityDecl DeclaringSyntax
)
{
    // Rows compare and dedupe capabilities by name; the record's structural equality over the
    // operations dictionary is neither needed nor meaningful.
    /// <summary>Compares capabilities by <see cref="Name"/> alone, ignoring the operations dictionary.</summary>
    public bool Equals(CapabilitySymbol? other)
    {
        return other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);
    }

    /// <summary>Hashes the capability by <see cref="Name"/>, consistent with <see cref="Equals(CapabilitySymbol)"/>.</summary>
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Name);
    }
}
