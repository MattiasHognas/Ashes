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
