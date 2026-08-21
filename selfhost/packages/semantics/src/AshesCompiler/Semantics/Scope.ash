// Maintains immutable lexical scopes and stable symbol allocation during binding.
//
// Invariants:
// - Duplicate declarations are rejected only within the current lexical frame.
// - Resolution searches from the innermost frame outward.
// - Leaving a scope never rewinds the next symbol identifier.

import AshesCompiler.Semantics.Symbols
export (
    type SemanticScope(..),
    type SemanticContext(..),
    type DeclarationResult(..),
    value createContext,
    value enterScope,
    value leaveScope,
    value declare,
    value resolve,
    value resolveQualified,
    value scopeDepth,
)

type SemanticScope =
    | frames: List(List(SemanticSymbol))

type SemanticContext =
    | scope: SemanticScope
    | nextSymbolId: Int

type DeclarationResult =
    | context: SemanticContext
    | symbol: Maybe(SemanticSymbol)
    | duplicate: Maybe(SemanticSymbol)

let createContext unit = SemanticContext(scope = SemanticScope(frames = [[]]), nextSymbolId = 0)

let enterScope context =
    match context with
        | SemanticContext { scope = SemanticScope { frames = frames }, nextSymbolId = nextSymbolId } -> SemanticContext(scope = SemanticScope(frames = [] :: frames), nextSymbolId = nextSymbolId)

let leaveScope context =
    match context with
        | SemanticContext { scope = SemanticScope { frames = _current :: parent :: tail }, nextSymbolId = nextSymbolId } ->
            Some(
                SemanticContext(scope = SemanticScope(frames = parent :: tail), nextSymbolId = nextSymbolId)
            )
        | _ -> None

let recursive findByName name symbols =
    match symbols with
        | [] -> None
        | (SemanticSymbol { name = symbolName, qualifiedName = _qualifiedName, id = _id, kind = _kind, definitionSpan = _span } as symbol) :: tail ->
            if name == symbolName
            then Some(symbol)
            else findByName(name)(tail)

let recursive findByQualifiedName qualifiedName symbols =
    match symbols with
        | [] -> None
        | (SemanticSymbol { name = _name, qualifiedName = symbolQualifiedName, id = _id, kind = _kind, definitionSpan = _span } as symbol) :: tail ->
            if Some(qualifiedName) == symbolQualifiedName
            then Some(symbol)
            else findByQualifiedName(qualifiedName)(tail)

let recursive resolveFrames name frames =
    match frames with
        | [] -> None
        | frame :: tail ->
            match findByName(name)(frame) with
                | Some(symbol) -> Some(symbol)
                | None -> resolveFrames(name)(tail)

let resolve name context =
    match context with
        | SemanticContext { scope = SemanticScope { frames = frames }, nextSymbolId = _nextSymbolId } ->
            resolveFrames(
                name,
                frames
            )

let recursive resolveQualifiedFrames qualifiedName frames =
    match frames with
        | [] -> None
        | frame :: tail ->
            match findByQualifiedName(qualifiedName)(frame) with
                | Some(symbol) -> Some(symbol)
                | None -> resolveQualifiedFrames(qualifiedName)(tail)

let resolveQualified qualifiedName context =
    match context with
        | SemanticContext { scope = SemanticScope { frames = frames }, nextSymbolId = _nextSymbolId } ->
            resolveQualifiedFrames(
                qualifiedName,
                frames
            )

let declare name qualifiedName kind definitionSpan context =
    match context with
        | SemanticContext { scope = SemanticScope { frames = current :: parents }, nextSymbolId = nextSymbolId } ->
            match findByName(name)(current) with
                | Some(existing) -> DeclarationResult(context = context, symbol = None, duplicate = Some(existing))
                | None ->
                    let symbol = makeSymbol(nextSymbolId)(name)(qualifiedName)(kind)(definitionSpan)
                    in
                        let nextContext = SemanticContext(scope = SemanticScope(frames = (symbol :: current) :: parents), nextSymbolId = nextSymbolId + 1)
                        in DeclarationResult(context = nextContext, symbol = Some(symbol), duplicate = None)
        | _ -> DeclarationResult(context = context, symbol = None, duplicate = None)

let recursive frameCount frames =
    match frames with
        | [] -> 0
        | _ :: tail -> 1 + frameCount(tail)

let scopeDepth context =
    match context with
        | SemanticContext { scope = SemanticScope { frames = frames }, nextSymbolId = _nextSymbolId } ->
            frameCount(
                frames
            ) - 1
