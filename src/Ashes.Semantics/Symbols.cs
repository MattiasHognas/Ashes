using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>A bound generic type parameter of a type or capability declaration.</summary>
/// <param name="Name">The parameter's name as written (e.g. <c>T</c>).</param>
public sealed record TypeParameterSymbol(string Name);

/// <summary>A source location and ownership identity retained for coherent instance diagnostics.</summary>
public sealed record TraitDeclarationProvenance(
    string PackageId,
    string ModuleName,
    string SourcePath,
    TextSpan Span);

/// <summary>One statically resolved trait requirement.</summary>
public sealed record TraitConstraint(TraitSymbol Trait, IReadOnlyList<TypeRef> TypeArgs)
{
    /// <summary>Returns constraints in deterministic dictionary-layout and diagnostic order.</summary>
    public static IReadOnlyList<TraitConstraint> Canonicalize(IEnumerable<TraitConstraint> constraints)
    {
        return constraints
            .OrderBy(StableKey, StringComparer.Ordinal)
            .DistinctBy(StableKey, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string StableKey(TraitConstraint constraint)
    {
        return $"{constraint.Trait.QualifiedName}({string.Join(",", constraint.TypeArgs.Select(TypeStableKey))})";
    }

    private static string TypeStableKey(TypeRef type) => type switch
    {
        TypeRef.TInt => "Int",
        TypeRef.TUInt unsigned => $"u{unsigned.Bits}",
        TypeRef.TFloat => "Float",
        TypeRef.TBigInt => "BigInt",
        TypeRef.TStr => "Str",
        TypeRef.TRune => "Rune",
        TypeRef.TBytes => "Bytes",
        TypeRef.TBool => "Bool",
        TypeRef.TNever => "Never",
        TypeRef.TVar variable => $"?{variable.Id:D10}",
        TypeRef.TTypeParam parameter => $"'{parameter.Symbol.Name}",
        TypeRef.TList list => $"List({TypeStableKey(list.Element)})",
        TypeRef.TTuple tuple => $"Tuple({string.Join(",", tuple.Elements.Select(TypeStableKey))})",
        TypeRef.TFun function => $"Fun({TypeStableKey(function.Arg)},{TypeStableKey(function.Ret)})",
        TypeRef.TNamedType named => $"{named.Symbol.Name}({string.Join(",", named.TypeArgs.Select(TypeStableKey))})",
        TypeRef.TPtr pointer => $"Ptr({TypeStableKey(pointer.Pointee)})",
        TypeRef.TOpaque opaque => $"Opaque({opaque.Name})",
        TypeRef.TCapability capability => $"Capability({capability.Symbol.Name},{string.Join(",", capability.Args.Select(TypeStableKey))})",
        TypeRef.TRow row => $"Row({string.Join(",", row.Capabilities.Select(TypeStableKey))}|{(row.Tail is null ? "" : TypeStableKey(row.Tail))})",
        _ => type.GetType().Name,
    };
}

/// <summary>A method declared by a trait.</summary>
public sealed record TraitMethodSymbol(
    string Name,
    TypeScheme Scheme,
    Expr? DefaultImplementation,
    TextSpan Span);

/// <summary>An immutable registered trait declaration.</summary>
public sealed class TraitSymbol
{
    /// <summary>Creates a fully registered immutable trait symbol.</summary>
    public TraitSymbol(
        string name,
        string qualifiedName,
        IReadOnlyList<TypeParameterSymbol> typeParameters,
        IReadOnlyDictionary<string, TraitMethodSymbol> methods,
        IReadOnlyList<TraitConstraint> supertraits,
        TraitDecl declaringSyntax,
        TraitDeclarationProvenance provenance)
    {
        Name = name;
        QualifiedName = qualifiedName;
        TypeParameters = typeParameters.ToArray();
        Methods = new Dictionary<string, TraitMethodSymbol>(methods, StringComparer.Ordinal);
        Supertraits = TraitConstraint.Canonicalize(supertraits);
        DeclaringSyntax = declaringSyntax;
        Provenance = provenance;
    }

    /// <summary>The source-level unqualified trait name.</summary>
    public string Name { get; }
    /// <summary>The stable module-qualified trait name.</summary>
    public string QualifiedName { get; }
    /// <summary>The declared type parameters in source order.</summary>
    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; }
    /// <summary>The methods keyed by source name.</summary>
    public IReadOnlyDictionary<string, TraitMethodSymbol> Methods { get; }
    /// <summary>The canonically ordered direct supertrait requirements.</summary>
    public IReadOnlyList<TraitConstraint> Supertraits { get; }
    /// <summary>The frontend declaration that produced this symbol.</summary>
    public TraitDecl DeclaringSyntax { get; }
    /// <summary>The package, module, file, and span that own this declaration.</summary>
    public TraitDeclarationProvenance Provenance { get; }
}

/// <summary>The resolved head of an instance declaration.</summary>
public sealed record TraitInstanceHead(TraitSymbol Trait, IReadOnlyList<TypeRef> TypeArgs);

/// <summary>An immutable registered instance and its conditional evidence requirements.</summary>
public sealed record TraitInstanceSymbol(
    TraitInstanceHead Head,
    IReadOnlyList<TraitConstraint> Requirements,
    IReadOnlyDictionary<string, Expr> MethodImplementations,
    TraitImplementationDecl DeclaringSyntax,
    TraitDeclarationProvenance Provenance);

/// <summary>
/// A deterministic proof that one concrete or structurally reducible trait goal is supplied by a
/// unique coherent instance. Conditional requirements and inherited evidence retain their complete
/// proof trees for dictionary construction and diagnostics.
/// </summary>
public abstract record TraitEvidencePlan(TraitConstraint Goal)
{
    /// <summary>Evidence that must be supplied by a constrained caller.</summary>
    public sealed record Parameter(TraitConstraint Constraint) : TraitEvidencePlan(Constraint);

    /// <summary>Evidence constructed from the unique coherent instance and its dependencies.</summary>
    public sealed record Instance(
        TraitConstraint Constraint,
        TraitInstanceSymbol SelectedInstance,
        IReadOnlyList<TraitEvidencePlan> Requirements,
        IReadOnlyList<TraitEvidencePlan> Supertraits) : TraitEvidencePlan(Constraint);
}

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
/// <param name="IsZeroCost">True when the nominal type erases to its sole constructor payload.</param>
public sealed record TypeSymbol(
    string Name,
    IReadOnlyList<TypeParameterSymbol> TypeParameters,
    IReadOnlyList<ConstructorSymbol> Constructors,
    TypeDecl DeclaringSyntax,
    bool IsBuiltin = false,
    bool IsZeroCost = false
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
