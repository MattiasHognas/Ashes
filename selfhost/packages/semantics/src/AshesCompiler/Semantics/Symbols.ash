import AshesCompiler.Frontend.Token
export (
    type SymbolKind(..),
    type SemanticSymbol(..),
    value makeSymbol,
)

type SymbolKind =
    | SymbolValue
    | SymbolType
    | SymbolConstructor
    | SymbolCapability
    | SymbolTrait
    | SymbolExternal
    | SymbolModule
    deriving {Eq, Show}

type SemanticSymbol =
    | id: Int
    | name: Str
    | qualifiedName: Maybe(Str)
    | kind: SymbolKind
    | definitionSpan: Maybe(TextSpan)
    deriving {Eq, Show}

let makeSymbol id name qualifiedName kind definitionSpan = SemanticSymbol(id = id, name = name, qualifiedName = qualifiedName, kind = kind, definitionSpan = definitionSpan)
