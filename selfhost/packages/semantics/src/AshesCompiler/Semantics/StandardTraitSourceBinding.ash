// Binds the seeded standard-trait ABI to implementation expressions from stitched Ashes.Trait.
//
// Invariants:
// - Source and seeded heads match by alpha-normalized structure; source type-parameter spellings and
//   seeded parameter ids are never treated as identity.
// - Bound expressions come from the rewritten module, so private values and imported modules already
//   use their stable stitched identities.
// - Every seeded placeholder must have exactly one source method, and rewritten trait-qualified calls
//   resolve through aliases that retain the canonical seeded constraint identity.

import Ashes.Collection.List.reverse
import Ashes.Text
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.ModuleReferenceRewriting
import AshesCompiler.Semantics.ModuleSemanticStitching
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.TypeInference
export (
    type StandardTraitSourceBinding(..),
    type StandardTraitSourceBindingError(..),
    type StandardTraitSourceBindingResult(..),
    value bindStandardTraitImplementationSources,
)

type StandardTraitSourceBinding =
    | traitName: Str
    | methodName: Str
    | headKey: Str
    | implementation: Expr

type StandardTraitSourceBindingError =
    | MissingStandardTraitSourceModule
    | StandardTraitSourceShapeMismatch(Int)
    | UnsupportedStandardTraitSourceHead(Str)
    | DuplicateStandardTraitSourceBinding(Str)
    | MissingStandardTraitSourceBinding(Str)
    | MissingSeededStandardTrait(Str)
    deriving {Eq, Show}

type StandardTraitSourceBindingResult =
    | environment: TypeEnvironment
    | bindings: List(StandardTraitSourceBinding)

type StandardTraitUnitPair =
    | original: SemanticStitchUnit
    | rewritten: SemanticStitchUnit

type StandardTraitBindingCollection =
    | bindingsReversed: List(StandardTraitSourceBinding)
    | error: Maybe(StandardTraitSourceBindingError)

type BoundStandardMethods =
    | methods: List(TraitImplementationMethodInferenceDefinition)
    | error: Maybe(StandardTraitSourceBindingError)

type BoundStandardImplementations =
    | implementations: List(TraitImplementationInferenceDefinition)
    | error: Maybe(StandardTraitSourceBindingError)

type StandardTraitAliasResult =
    | environment: TypeEnvironment
    | error: Maybe(StandardTraitSourceBindingError)

type SourceTypeKeyState =
    | parameters: List((Str, Int))
    | nextOrdinal: Int

type SemanticTypeKeyState =
    | parameters: List((Int, Int))
    | nextOrdinal: Int

let recursive lastNamePart parts =
    match parts with
        | [] -> ""
        | name :: [] -> name
        | _head :: tail -> lastNamePart(tail)

let nameLeaf name =
    "."
    |> Ashes.Text.split(name)
    |> lastNamePart

let standardNamedSourceKey name =
    match name with
        | "Int" -> Some("int")
        | "Float" -> Some("float")
        | "BigInt" -> Some("bigint")
        | "u8" -> Some("u8")
        | "u16" -> Some("u16")
        | "u32" -> Some("u32")
        | "u64" -> Some("u64")
        | "Bool" -> Some("bool")
        | "Str" -> Some("str")
        | "Rune" -> Some("rune")
        | _ -> None

let isSourceParameterName name =
    (let bytes = Ashes.Byte.fromText(name)
    in
        if Ashes.Byte.length(bytes) <= 0
        then false
        else
            let first =
                0
                |> Ashes.Byte.get(bytes)
                |> Ashes.Number.UInt.toInt
            in
                if first >= 97
                then first <= 122
                else false)

let recursive findSourceParameterOrdinal name parameters =
    match parameters with
        | [] -> None
        | (candidate, ordinal) :: tail ->
            if candidate == name
            then Some(ordinal)
            else findSourceParameterOrdinal(name)(tail)

let sourceParameterKey name state =
    match state with
        | SourceTypeKeyState { parameters = parameters, nextOrdinal = nextOrdinal } ->
            match findSourceParameterOrdinal(name)(parameters) with
                | Some(ordinal) -> ("parameter" + Ashes.Text.fromInt(ordinal), state)
                | None ->
                    ("parameter" + Ashes.Text.fromInt(
                        nextOrdinal
                    ), SourceTypeKeyState(parameters = (name, nextOrdinal) :: parameters, nextOrdinal = nextOrdinal + 1))

let recursive standardSourceTypeKeys types state =
    match types with
        | [] -> (Some(""), state)
        | head :: tail ->
            match standardSourceTypeKey(head)(state) with
                | (None, nextState) -> (None, nextState)
                | (Some(headKey), headState) ->
                    match standardSourceTypeKeys(tail)(headState) with
                        | (None, nextState) -> (None, nextState)
                        | (Some(tailKeys), nextState) -> (Some("_" + headKey + tailKeys), nextState)
and standardSourceTypeKey typeExpression state =
    match typeExpression with
        | TypeAt(_span, inner) -> standardSourceTypeKey(inner)(state)
        | TypeNamed(name) ->
            let leaf = nameLeaf(name)
            in
                match standardNamedSourceKey(leaf) with
                    | Some(key) -> (Some(key), state)
                    | None ->
                        if isSourceParameterName(leaf)
                        then
                            match sourceParameterKey(leaf)(state) with
                                | (key, nextState) -> (Some(key), nextState)
                        else (None, state)
        | TypeApplied(name, arguments) ->
            match standardSourceTypeKeys(arguments)(state) with
                | (None, nextState) -> (None, nextState)
                | (Some(argumentKeys), nextState) ->
                    match nameLeaf(name) with
                        | "List" -> (Some("list" + argumentKeys), nextState)
                        | "Maybe" -> (Some("Maybe" + argumentKeys), nextState)
                        | "Result" -> (Some("Result" + argumentKeys), nextState)
                        | _ -> (None, nextState)
        | TypeTuple(first :: second :: []) ->
            match standardSourceTypeKey(first)(state) with
                | (None, nextState) -> (None, nextState)
                | (Some(firstKey), firstState) ->
                    match standardSourceTypeKey(second)(firstState) with
                        | (None, nextState) -> (None, nextState)
                        | (Some(secondKey), nextState) -> (Some("tuple2_" + firstKey + "_" + secondKey), nextState)
        | _ -> (None, state)

let normalizedSourceTypeKey typeExpression =
    match standardSourceTypeKey(typeExpression)(SourceTypeKeyState(parameters = [], nextOrdinal = 0)) with
        | (key, _state) -> key

let recursive findSemanticParameterOrdinal parameterId parameters =
    match parameters with
        | [] -> None
        | (candidate, ordinal) :: tail ->
            if candidate == parameterId
            then Some(ordinal)
            else findSemanticParameterOrdinal(parameterId)(tail)

let semanticParameterKey parameterId state =
    match state with
        | SemanticTypeKeyState { parameters = parameters, nextOrdinal = nextOrdinal } ->
            match findSemanticParameterOrdinal(parameterId)(parameters) with
                | Some(ordinal) -> ("parameter" + Ashes.Text.fromInt(ordinal), state)
                | None ->
                    ("parameter" + Ashes.Text.fromInt(
                        nextOrdinal
                    ), SemanticTypeKeyState(parameters = (parameterId, nextOrdinal) :: parameters, nextOrdinal = nextOrdinal + 1))

let recursive standardSemanticTypeKeys types state =
    match types with
        | [] -> (Some(""), state)
        | head :: tail ->
            match standardSemanticTypeKey(head)(state) with
                | (None, nextState) -> (None, nextState)
                | (Some(headKey), headState) ->
                    match standardSemanticTypeKeys(tail)(headState) with
                        | (None, nextState) -> (None, nextState)
                        | (Some(tailKeys), nextState) -> (Some("_" + headKey + tailKeys), nextState)
and standardSemanticTypeKey semanticType state =
    match semanticType with
        | SemInt -> (Some("int"), state)
        | SemUInt(bits) -> (Some("u" + Ashes.Text.fromInt(bits)), state)
        | SemFloat -> (Some("float"), state)
        | SemBigInt -> (Some("bigint"), state)
        | SemString -> (Some("str"), state)
        | SemRune -> (Some("rune"), state)
        | SemBool -> (Some("bool"), state)
        | SemList(element) ->
            match standardSemanticTypeKey(element)(state) with
                | (None, nextState) -> (None, nextState)
                | (Some(elementKey), nextState) -> (Some("list_" + elementKey), nextState)
        | SemTuple(first :: second :: []) ->
            match standardSemanticTypeKey(first)(state) with
                | (None, nextState) -> (None, nextState)
                | (Some(firstKey), firstState) ->
                    match standardSemanticTypeKey(second)(firstState) with
                        | (None, nextState) -> (None, nextState)
                        | (Some(secondKey), nextState) -> (Some("tuple2_" + firstKey + "_" + secondKey), nextState)
        | SemNamed(_symbolId, name, arguments) ->
            match standardSemanticTypeKeys(arguments)(state) with
                | (None, nextState) -> (None, nextState)
                | (Some(argumentKeys), nextState) -> (Some(name + argumentKeys), nextState)
        | SemParameter(parameterId, _name) ->
            match semanticParameterKey(parameterId)(state) with
                | (key, nextState) -> (Some(key), nextState)
        | _ -> (None, state)

let normalizedSemanticTypeKey semanticType =
    match standardSemanticTypeKey(semanticType)(SemanticTypeKeyState(parameters = [], nextOrdinal = 0)) with
        | (key, _state) -> key

let findStandardTraitUnit units =
    (let recursive find remaining =
        match remaining with
            | [] -> None
            | (SemanticStitchUnit { name = name } as unit) :: tail ->
                if name == "Ashes.Trait"
                then Some(unit)
                else find(tail)
    in find(units))

let standardTraitUnitPair units project =
    match findStandardTraitUnit(units) with
        | None -> Error(MissingStandardTraitSourceModule)
        | Some(original) ->
            Ok(
                StandardTraitUnitPair(original = original, rewritten = rewriteStitchedModuleReferences(
                    project,
                    original
                ))
            )

let findRewrittenMethod methodName bindings =
    (let recursive find remaining =
        match remaining with
            | [] -> None
            | (TraitImplementationMethodBinding { methodName = candidate } as binding) :: tail ->
                if candidate == methodName
                then Some(binding)
                else find(tail)
    in find(bindings))

let bindingExists traitName methodName headKey bindings =
    (let recursive exists remaining =
        match remaining with
            | [] -> false
            | StandardTraitSourceBinding { traitName = candidateTrait, methodName = candidateMethod, headKey = candidateHead } :: tail ->
                if candidateTrait == traitName
                then
                    if candidateMethod == methodName
                    then
                        if candidateHead == headKey
                        then true
                        else exists(tail)
                    else exists(tail)
                else exists(tail)
    in exists(bindings))

let sourceBindingIdentity traitName methodName headKey = traitName + "." + methodName + ":" + headKey

let addSourceMethodBinding (traitName: Str) (head: TypeExpr) (originalMethod: TraitImplementationMethodBinding) (rewrittenMethods: List(TraitImplementationMethodBinding)) (collection: StandardTraitBindingCollection) =
    match (originalMethod, normalizedSourceTypeKey(head), collection) with
        | (_method, _key, StandardTraitBindingCollection { error = Some(error) }) ->
            StandardTraitBindingCollection(bindingsReversed = [], error = Some(
                error
            ))
        | (TraitImplementationMethodBinding { methodName = methodName }, None, StandardTraitBindingCollection { bindingsReversed = reversed, error = None }) ->
            StandardTraitBindingCollection(bindingsReversed = reversed, error = Some(
                UnsupportedStandardTraitSourceHead(traitName)
            ))
        | (TraitImplementationMethodBinding { methodName = methodName }, Some(headKey), StandardTraitBindingCollection { bindingsReversed = reversed, error = None }) ->
            let canonicalTraitName = nameLeaf(traitName)
            in
                let identity = sourceBindingIdentity(canonicalTraitName)(methodName)(headKey)
                in
                    if bindingExists(canonicalTraitName)(methodName)(headKey)(reversed)
                    then
                        StandardTraitBindingCollection(bindingsReversed = reversed, error = Some(
                            DuplicateStandardTraitSourceBinding(identity)
                        ))
                    else
                        match findRewrittenMethod(methodName)(rewrittenMethods) with
                            | None ->
                                StandardTraitBindingCollection(bindingsReversed = reversed, error = Some(
                                    StandardTraitSourceShapeMismatch(0)
                                ))
                            | Some(TraitImplementationMethodBinding { implementation = implementation }) -> StandardTraitBindingCollection(bindingsReversed = StandardTraitSourceBinding(traitName = canonicalTraitName, methodName = methodName, headKey = headKey, implementation = implementation) :: reversed, error = None)

let recursive addSourceMethodBindings traitName head originalMethods rewrittenMethods (collection: StandardTraitBindingCollection) =
    match originalMethods with
        | [] -> collection
        | method :: tail ->
            collection
            |> addSourceMethodBinding(traitName)(head)(method)(rewrittenMethods)
            |> addSourceMethodBindings(traitName)(head)(tail)(rewrittenMethods)

let addSourceImplementationBindings original rewritten (collection: StandardTraitBindingCollection) =
    match (original, rewritten) with
        | (TraitImplementationDecl { traitName = traitName, typeArguments = head :: [], bindings = originalMethods }, TraitImplementationDecl { bindings = rewrittenMethods }) ->
            addSourceMethodBindings(
                traitName,
                head,
                originalMethods,
                rewrittenMethods,
                collection
            )
        | (TraitImplementationDecl { traitName = traitName }, _rewritten) ->
            match collection with
                | StandardTraitBindingCollection { bindingsReversed = reversed } ->
                    StandardTraitBindingCollection(bindingsReversed = reversed, error = Some(
                        UnsupportedStandardTraitSourceHead(traitName)
                    ))

let recursive addSourceItemBindings original rewritten index (collection: StandardTraitBindingCollection) =
    match (original, rewritten) with
        | (TopLevelAt(_originalSpan, originalInner), TopLevelAt(_rewrittenSpan, rewrittenInner)) ->
            addSourceItemBindings(
                originalInner,
                rewrittenInner,
                index,
                collection
            )
        | (TopLevelImplementation(originalImplementation), TopLevelImplementation(rewrittenImplementation)) ->
            addSourceImplementationBindings(
                originalImplementation,
                rewrittenImplementation,
                collection
            )
        | (TopLevelImplementation(_originalImplementation), _other) ->
            match collection with
                | StandardTraitBindingCollection { bindingsReversed = reversed } ->
                    StandardTraitBindingCollection(bindingsReversed = reversed, error = Some(
                        StandardTraitSourceShapeMismatch(index)
                    ))
        | (_original, _rewritten) -> collection

let recursive collectSourceBindings originalItems rewrittenItems index (collection: StandardTraitBindingCollection) =
    match (originalItems, rewrittenItems) with
        | ([], []) ->
            match collection with
                | StandardTraitBindingCollection { bindingsReversed = reversed, error = error } ->
                    StandardTraitBindingCollection(bindingsReversed = reverse(
                        reversed
                    ), error = error)
        | (original :: originalTail, rewritten :: rewrittenTail) ->
            collection
            |> addSourceItemBindings(original)(rewritten)(index)
            |> collectSourceBindings(originalTail)(rewrittenTail)(index + 1)
        | _ ->
            StandardTraitBindingCollection(bindingsReversed = [], error = Some(
                StandardTraitSourceShapeMismatch(index)
            ))

let sourceBindings pair =
    match pair with
        | StandardTraitUnitPair { original = SemanticStitchUnit { program = ProgramSyntax { items = originalItems } }, rewritten = SemanticStitchUnit { program = ProgramSyntax { items = rewrittenItems } } } ->
            collectSourceBindings(
                originalItems,
                rewrittenItems,
                0,
                StandardTraitBindingCollection(bindingsReversed = [], error = None)
            )

let findSourceBinding traitName methodName headKey bindings =
    (let recursive find remaining =
        match remaining with
            | [] -> None
            | (StandardTraitSourceBinding { traitName = candidateTrait, methodName = candidateMethod, headKey = candidateHead } as binding) :: tail ->
                if candidateTrait == traitName
                then
                    if candidateMethod == methodName
                    then
                        if candidateHead == headKey
                        then Some(binding)
                        else find(tail)
                    else find(tail)
                else find(tail)
    in find(bindings))

let bindStandardMethod bindings traitName headKey method =
    match (headKey, method) with
        | (Some(normalizedHead), TraitImplementationMethodInferenceDefinition { name = methodName, implementation = ExprVar(bindingName), semanticType = semanticType }) ->
            if Ashes.Text.startsWith(bindingName)("__ashes_standard_trait_")
            then
                match findSourceBinding(traitName)(methodName)(normalizedHead)(bindings) with
                    | None -> Error(MissingStandardTraitSourceBinding(bindingName))
                    | Some(StandardTraitSourceBinding { implementation = implementation }) ->
                        Ok(
                            TraitImplementationMethodInferenceDefinition(name = methodName, implementation = implementation, semanticType = semanticType)
                        )
            else Ok(method)
        | (None, TraitImplementationMethodInferenceDefinition { implementation = ExprVar(bindingName) }) ->
            if Ashes.Text.startsWith(bindingName)("__ashes_standard_trait_")
            then Error(MissingStandardTraitSourceBinding(bindingName))
            else Ok(method)
        | _ -> Ok(method)

let recursive bindStandardMethods bindings traitName headKey methods reversedMethods =
    match methods with
        | [] -> BoundStandardMethods(methods = reverse(reversedMethods), error = None)
        | method :: tail ->
            match bindStandardMethod(bindings)(traitName)(headKey)(method) with
                | Error(error) -> BoundStandardMethods(methods = [], error = Some(error))
                | Ok(bound) -> bindStandardMethods(bindings)(traitName)(headKey)(tail)(bound :: reversedMethods)

let recursive bindStandardImplementations bindings implementations reversedImplementations =
    match implementations with
        | [] -> BoundStandardImplementations(implementations = reverse(reversedImplementations), error = None)
        | TraitImplementationInferenceDefinition { traitName = traitName, typeArguments = typeArguments, requirements = requirements, methods = methods } :: tail ->
            let headKey =
                match typeArguments with
                    | head :: [] -> normalizedSemanticTypeKey(head)
                    | _ -> None
            in
                match bindStandardMethods(bindings)(traitName)(headKey)(methods)([]) with
                    | BoundStandardMethods { methods = _boundMethods, error = Some(error) } ->
                        BoundStandardImplementations(implementations = [], error = Some(
                            error
                        ))
                    | BoundStandardMethods { methods = boundMethods, error = None } ->
                        bindStandardImplementations(
                            bindings,
                            tail,
                            TraitImplementationInferenceDefinition(traitName = traitName, typeArguments = typeArguments, requirements = requirements, methods = boundMethods) :: reversedImplementations
                        )

let withTraitImplementations implementations (environment: TypeEnvironment) = environment with traitImplementations = implementations

let recursive addAliasMethodBindings compilerName methods environment =
    match methods with
        | [] -> environment
        | TraitMethodInferenceDefinition { name = methodName, scheme = scheme } :: tail ->
            environment
            |> addTypeBinding(compilerName + "." + methodName)(scheme)
            |> addAliasMethodBindings(compilerName)(tail)

let addStandardTraitAlias definition environment =
    match definition with
        | StitchedDefinition { sourceName = sourceName, compilerName = compilerName, packageId = packageId, kind = StitchedTrait } ->
            match resolveTraitBinding(sourceName)(environment) with
                | None ->
                    StandardTraitAliasResult(environment = environment, error = Some(
                        MissingSeededStandardTrait(sourceName)
                    ))
                | Some(TraitInferenceDefinition { parameterCount = parameterCount, parameters = parameters, methods = methods, supertraits = supertraits }) ->
                    let originalPackageId = inferencePackageId(environment)
                    in
                        let aliased =
                            environment
                            |> withInferencePackage(packageId)
                            |> addTraitBinding(compilerName)(parameterCount)(parameters)(methods)(supertraits)
                            |> addAliasMethodBindings(compilerName)(methods)
                            |> withInferencePackage(originalPackageId)
                        in StandardTraitAliasResult(environment = aliased, error = None)
        | _ -> StandardTraitAliasResult(environment = environment, error = None)

let recursive addStandardTraitAliases definitions environment =
    match definitions with
        | [] -> StandardTraitAliasResult(environment = environment, error = None)
        | definition :: tail ->
            match addStandardTraitAlias(definition)(environment) with
                | StandardTraitAliasResult { environment = _next, error = Some(error) } ->
                    StandardTraitAliasResult(environment = environment, error = Some(
                        error
                    ))
                | StandardTraitAliasResult { environment = next, error = None } -> addStandardTraitAliases(tail)(next)

let findStandardTraitDefinitions project =
    (let recursive find scopes =
        match scopes with
            | [] -> None
            | StitchedModuleScope { name = "Ashes.Trait", definitions = definitions } :: _tail -> Some(definitions)
            | _head :: tail -> find(tail)
    in
        match project with
            | StitchedSemanticProject { scopes = scopes } -> find(scopes))

let bindSourceEnvironment bindings project environment =
    match environment with
        | TypeEnvironment { traitImplementations = implementations } ->
            match bindStandardImplementations(bindings)(implementations)([]) with
                | BoundStandardImplementations { implementations = _bound, error = Some(error) } -> Error(error)
                | BoundStandardImplementations { implementations = bound, error = None } ->
                    match findStandardTraitDefinitions(project) with
                        | None -> Error(MissingStandardTraitSourceModule)
                        | Some(definitions) ->
                            match environment
                            |> withTraitImplementations(bound)
                            |> addStandardTraitAliases(definitions) with
                                | StandardTraitAliasResult { environment = _aliased, error = Some(error) } ->
                                    Error(
                                        error
                                    )
                                | StandardTraitAliasResult { environment = aliased, error = None } ->
                                    Ok(
                                        StandardTraitSourceBindingResult(environment = aliased, bindings = bindings)
                                    )

let bindStandardTraitImplementationSources units project environment =
    match standardTraitUnitPair(units)(project) with
        | Error(error) -> Error(error)
        | Ok(pair) ->
            match sourceBindings(pair) with
                | StandardTraitBindingCollection { bindingsReversed = _bindings, error = Some(error) } -> Error(error)
                | StandardTraitBindingCollection { bindingsReversed = bindings, error = None } ->
                    bindSourceEnvironment(
                        bindings,
                        project,
                        environment
                    )
