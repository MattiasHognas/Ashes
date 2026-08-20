// Defines the frontend syntax tree shared by parsing, formatting, and semantics.
//
// Invariants:
// - Patterns, type expressions, expressions, and declarations remain distinct categories.
// - At wrappers attach UTF-8 byte spans without altering the enclosed syntax.
// - Declaration order reflects the language's sequential top-level scope.

import AshesCompiler.Frontend.Token
export (
    type Pattern(..),
    type TypeExpr(..),
    type CapabilityRefSyntax(..),
    type NeedsRowSyntax(..),
    type TraitConstraintSyntax(..),
    type Expr(..),
    type TypeParameter(..),
    type TypeConstructor(..),
    type TypeDecl(..),
    type TypeAliasDecl(..),
    type ZeroCostTypeDecl(..),
    type CapabilityOperation(..),
    type CapabilityDecl(..),
    type ProvideBinding(..),
    type ProvideDecl(..),
    type TraitMethodDecl(..),
    type TraitDecl(..),
    type TraitImplementationMethodBinding(..),
    type TraitImplementationDecl(..),
    type ParsedType(..),
    type FfiStringOwnership(..),
    type ExternalParameterOwnership(..),
    type ExternalDecl(..),
    type ExportConstructors(..),
    type ExportItem(..),
    type ExportDecl(..),
    type LetBindingSyntax(..),
    type TopLevelItem(..),
    type ProgramSyntax(..),
)

type Pattern =
    | PatternAt(TextSpan, Pattern)
    | PatternEmptyList
    | PatternVar(Str)
    | PatternWildcard
    | PatternCons(Pattern, Pattern)
    | PatternTuple(List(Pattern))
    | PatternConstructor(Str, List(Pattern))
    | PatternRecord(Str, List((Str, Pattern)))
    | PatternAs(Pattern, Str)
    | PatternOr(List(Pattern))
    | PatternInt(Int)
    | PatternString(Str)
    | PatternRune(Int)
    | PatternBool(Bool)
    deriving {Eq, Show}

type TypeExpr =
    | TypeAt(TextSpan, TypeExpr)
    | TypeNamed(Str)
    | TypeApplied(Str, List(TypeExpr))
    | TypeArrow(TypeExpr, TypeExpr, List((Str, List(TypeExpr))), Maybe(Str))
    | TypeTuple(List(TypeExpr))
    | TypeUnit
    deriving {Eq, Show}

type CapabilityRefSyntax =
    | name: Str
    | args: List(TypeExpr)
    deriving {Eq, Show}

type NeedsRowSyntax =
    | capabilities: List(CapabilityRefSyntax)
    | tailVariable: Maybe(Str)
    deriving {Eq, Show}

type TraitConstraintSyntax =
    | traitName: Str
    | typeArguments: List(TypeExpr)
    deriving {Eq, Show}

type Expr =
    | ExprAt(TextSpan, Expr)
    | ExprInt(Int)
    | ExprBigInt(Str)
    | ExprUInt(Int, Int, Str)
    | ExprFloat(Float, Str)
    | ExprString(Str)
    | ExprRune(Int)
    | ExprBool(Bool)
    | ExprVar(Str)
    | ExprQualifiedVar(Str, Str)
    | ExprAdd(Expr, Expr)
    | ExprSubtract(Expr, Expr)
    | ExprMultiply(Expr, Expr)
    | ExprDivide(Expr, Expr)
    | ExprModulo(Expr, Expr)
    | ExprBitwiseAnd(Expr, Expr)
    | ExprBitwiseOr(Expr, Expr)
    | ExprBitwiseXor(Expr, Expr)
    | ExprShiftLeft(Expr, Expr)
    | ExprShiftRight(Expr, Expr)
    | ExprBitwiseNot(Expr)
    | ExprLogicalNot(Expr)
    | ExprGreaterThan(Expr, Expr)
    | ExprLessThan(Expr, Expr)
    | ExprGreaterOrEqual(Expr, Expr)
    | ExprLessOrEqual(Expr, Expr)
    | ExprEqual(Expr, Expr)
    | ExprNotEqual(Expr, Expr)
    | ExprResultPipe(Expr, Expr)
    | ExprResultMapErrorPipe(Expr, Expr)
    | ExprLet(Str, Expr, Expr, List(Str), Maybe(TypeExpr), List(TraitConstraintSyntax))
    | ExprLetResult(Str, Expr, Expr)
    | ExprLetRecursive(Str, Expr, Expr, List(Str), Maybe(TypeExpr), List(TraitConstraintSyntax))
    | ExprIf(Expr, Expr, Expr)
    | ExprLambda(Str, Expr, Maybe(TypeExpr))
    | ExprCall(Expr, Expr, Bool)
    | ExprTuple(List(Expr))
    | ExprList(List(Expr))
    | ExprCons(Expr, Expr)
    | ExprMatch(Expr, List((Pattern, Expr, Maybe(Expr))), Maybe(Int))
    | ExprAwait(Expr)
    | ExprRecord(Str, List((Str, Expr)))
    | ExprRecordUpdate(Expr, List((Str, Expr)))
    | ExprPerform(Expr)
    | ExprHandle(Expr, List((Maybe(Str), Str, List(Pattern), Expr)))

type TypeParameter =
    | name: Str
    deriving {Eq, Show}

type TypeConstructor =
    | name: Str
    | parameters: List(TypeExpr)
    | fieldNames: List(Str)
    deriving {Eq, Show}

type TypeDecl =
    | name: Str
    | typeParameters: List(TypeParameter)
    | constructors: List(TypeConstructor)
    | isRecord: Bool
    | derivingTraits: List(Str)
    deriving {Eq, Show}

type TypeAliasDecl =
    | name: Str
    | typeParameters: List(TypeParameter)
    | target: TypeExpr
    deriving {Eq, Show}

type ZeroCostTypeDecl =
    | name: Str
    | typeParameters: List(TypeParameter)
    | constructor: TypeConstructor
    | derivingTraits: List(Str)
    deriving {Eq, Show}

type CapabilityOperation =
    | name: Str
    | signature: Maybe(TypeExpr)
    deriving {Eq, Show}

type CapabilityDecl =
    | name: Str
    | typeParameters: List(TypeParameter)
    | operations: List(CapabilityOperation)
    deriving {Eq, Show}

type ProvideBinding =
    | operationName: Str
    | implementation: Expr

type ProvideDecl =
    | capabilityName: Str
    | typeArguments: List(TypeExpr)
    | bindings: List(ProvideBinding)

type TraitMethodDecl =
    | name: Str
    | signature: TypeExpr
    | defaultImplementation: Maybe(Expr)

type TraitDecl =
    | name: Str
    | typeParameters: List(TypeParameter)
    | supertraits: List(TraitConstraintSyntax)
    | methods: List(TraitMethodDecl)

type TraitImplementationMethodBinding =
    | methodName: Str
    | implementation: Expr

type TraitImplementationDecl =
    | traitName: Str
    | typeArguments: List(TypeExpr)
    | requirements: List(TraitConstraintSyntax)
    | bindings: List(TraitImplementationMethodBinding)

type FfiStringOwnership =
    | FfiStringBorrowed
    | FfiStringOwned
    deriving {Eq, Show}

type ParsedType =
    | ParsedNamed(Str)
    | ParsedPointer(ParsedType)
    | ParsedBuffer(ParsedType)
    | ParsedOut(ParsedType)
    | ParsedNativeString(Bool, FfiStringOwnership, Maybe(Str))
    deriving {Eq, Show}

type ExternalParameterOwnership =
    | ExternalOwnershipUnspecified
    | ExternalOwnershipBorrow
    | ExternalOwnershipConsume
    deriving {Eq, Show}

type ExternalDecl =
    | ExternalOpaqueType(Str, Maybe(Str))
    | ExternalFunction(Str, List(ParsedType), ParsedType, Maybe(Str), List(ExternalParameterOwnership), Maybe(NeedsRowSyntax))
    deriving {Eq, Show}

type ExportConstructors =
    | ExportConstructorsHidden
    | ExportConstructorsAll
    | ExportConstructorsSelected(List(Str))
    deriving {Eq, Show}

type ExportItem =
    | ExportValue(Str)
    | ExportType(Str, ExportConstructors)
    | ExportModule(Str)
    deriving {Eq, Show}

type ExportDecl =
    | items: List(ExportItem)
    deriving {Eq, Show}

type LetBindingSyntax =
    | name: Str
    | value: Expr
    | sugarParameters: List(Str)
    | typeAnnotation: Maybe(TypeExpr)
    | requirements: List(TraitConstraintSyntax)

type TopLevelItem =
    | TopLevelAt(TextSpan, TopLevelItem)
    | TopLevelExport(ExportDecl)
    | TopLevelType(TypeDecl)
    | TopLevelTypeAlias(TypeAliasDecl)
    | TopLevelZeroCostType(ZeroCostTypeDecl)
    | TopLevelExternal(ExternalDecl)
    | TopLevelCapability(CapabilityDecl)
    | TopLevelProvide(ProvideDecl)
    | TopLevelTrait(TraitDecl)
    | TopLevelImplementation(TraitImplementationDecl)
    | TopLevelLet(LetBindingSyntax, Bool)
    | TopLevelRecursiveGroup(List(LetBindingSyntax))

type ProgramSyntax =
    | items: List(TopLevelItem)
    | body: Maybe(Expr)
