// Concrete source copies use ordinary ownership lowering, independently of the
// dictionary ABI and the linear accumulator specialization machinery.
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes.schemeIsClosed
import Ashes.Collection.Map.MapTree
export (
    type CoreConcreteCandidate(..),
    type CoreConcreteCopy(..),
    type CoreConcreteRegistry(..),
    value emptyCoreConcreteRegistry,
    value registerCoreConcreteCandidate,
    value coreConcreteOpenSchemes,
    value lookupCoreConcreteCandidate,
    value lookupCoreConcreteCopy,
    value recordCoreConcreteCopy,
    value concreteConstrainedParameter,
    value concreteConstrainedArguments,
    value pinConcreteConstrainedFunction,
    value pinProvenConcreteConstrainedFunction,
    value preparePureConcreteScheme,
)

type CoreConcreteCandidate =
    | identity: Int
    | name: Str
    | source: Expr
    | scheme: TypeScheme
    | arity: Int
    | bindingSlot: Int
    | isRecursive: Bool

// None is a proved rejection, distinct from an absent cache entry. An in-progress
// copy is never published here: recursive calls must use the generated self binding.
type CoreConcreteCopy =
    | candidateIdentity: Int
    | parameterTypes: List(SemanticType)
    | label: Maybe(Str)

// `inferenceBindings` lists every registration youngest first, so the first entry for a name is
// the candidate `candidates` holds for it. `openSchemes` has an entry for each name ever
// registered with a free type variable in its scheme: the scheme while the current candidate of
// that name is open, None once a closed one replaced it.
type CoreConcreteRegistry =
    | nextIdentity: Int
    | candidates: MapTree(Str, CoreConcreteCandidate)
    | copies: MapTree(Int, List(CoreConcreteCopy))
    | inferenceBindings: List((Str, TypeScheme))
    | openSchemes: MapTree(Str, Maybe(TypeScheme))

let emptyCoreConcreteRegistry unit = CoreConcreteRegistry(nextIdentity = 0, candidates = Ashes.Collection.Map.empty, copies = Ashes.Collection.Map.empty, inferenceBindings = [], openSchemes = Ashes.Collection.Map.empty)

let registeredOpenSchemes (name: Str) (scheme: TypeScheme) (open: MapTree(Str, Maybe(TypeScheme))) =
    if schemeIsClosed(scheme)
    then
        match Ashes.Collection.Map.getStr(name)(open) with
            | Some(_earlier) -> Ashes.Collection.Map.setStr(name)(None)(open)
            | None -> open
    else Ashes.Collection.Map.setStr(name)(Some(scheme))(open)

// The caller admits only top-level sources and keeps the pre-dictionary scheme.
// A new declaration gets a distinct cache identity even when its spelling repeats.
let registerCoreConcreteCandidate name source scheme arity bindingSlot isRecursive (registry: CoreConcreteRegistry) =
    (let candidate = CoreConcreteCandidate(identity = registry.nextIdentity, name = name, source = source, scheme = scheme, arity = arity, bindingSlot = bindingSlot, isRecursive = isRecursive)
    in registry with nextIdentity = registry.nextIdentity + 1, candidates = Ashes.Collection.Map.setStr(name)(candidate)(registry.candidates), inferenceBindings = (name, scheme) :: registry.inferenceBindings, openSchemes = registeredOpenSchemes(name)(scheme)(registry.openSchemes))

// The schemes of the current candidates that hold a free type variable, in the order a fold of
// `candidates` lists them.
let coreConcreteOpenSchemes (registry: CoreConcreteRegistry) =
    Ashes.Collection.Map.foldLeft(given (schemes) ->
        given (_name) ->
            given (entry) ->
                match entry with
                    | Some(scheme) -> scheme :: schemes
                    | None -> schemes)([])(registry.openSchemes)

let lookupCoreConcreteCandidate name (registry: CoreConcreteRegistry) = Ashes.Collection.Map.getStr(name)(registry.candidates)

let recursive lookupConcreteCopy identity parameters copies =
    match copies with
        | [] -> None
        | (CoreConcreteCopy { candidateIdentity = candidateIdentity, parameterTypes = parameterTypes } as copy) :: rest ->
            if identity == candidateIdentity && parameters == parameterTypes
            then Some(copy.label)
            else lookupConcreteCopy(identity)(parameters)(rest)

let lookupCoreConcreteCopy identity parameters (registry: CoreConcreteRegistry) =
    match Ashes.Collection.Map.getInt(identity)(registry.copies) with
        | Some(copies) -> lookupConcreteCopy(identity)(parameters)(copies)
        | None -> None

let recordCoreConcreteCopy identity parameters label (registry: CoreConcreteRegistry) =
    (let earlier =
        match Ashes.Collection.Map.getInt(identity)(registry.copies) with
            | Some(copies) -> copies
            | None -> []
    in registry with copies = Ashes.Collection.Map.setInt(identity)(CoreConcreteCopy(candidateIdentity = identity, parameterTypes = parameters, label = label) :: earlier)(registry.copies))

// No open variables, higher-order arguments, or effect rows enter the concrete
// ownership path. Named identity is its symbol, not its printed type name.
let recursive concreteConstrainedParameter semanticType =
    match semanticType with
        | SemVariable(_id) -> false
        | SemParameter(_id, _name) -> false
        | SemFunction(_argument, _result, _row) -> false
        | SemRow(_capabilities, _tail) -> false
        | SemCapability(_name, _arguments) -> false
        | SemList(element) -> concreteConstrainedParameter(element)
        | SemPointer(element) -> concreteConstrainedParameter(element)
        | SemTuple(elements) -> concreteConstrainedParameters(elements)
        | SemNamed(_symbol, _name, arguments) -> concreteConstrainedParameters(arguments)
        | _ -> true
and concreteConstrainedParameters types =
    match types with
        | [] -> true
        | head :: rest -> concreteConstrainedParameter(head) && concreteConstrainedParameters(rest)

let recursive quantifiedVariable id quantified =
    match quantified with
        | [] -> false
        | (candidate, _name) :: rest -> id == candidate || quantifiedVariable(id)(rest)

let recursive pinnedVariable id substitution =
    match substitution with
        | [] -> None
        | (candidate, semanticType) :: rest ->
            if id == candidate
            then Some(semanticType)
            else pinnedVariable(id)(rest)

let recursive pinConcreteShape quantified formal actual substitution =
    match (formal, actual) with
        | (SemVariable(id), _) ->
            if quantifiedVariable(id)(quantified)
            then
                match pinnedVariable(id)(substitution) with
                    | None -> Some((id, actual) :: substitution)
                    | Some(earlier) ->
                        if earlier == actual
                        then Some(substitution)
                        else None
            else None
        | (SemList(left), SemList(right)) -> pinConcreteShape(quantified)(left)(right)(substitution)
        | (SemPointer(left), SemPointer(right)) -> pinConcreteShape(quantified)(left)(right)(substitution)
        | (SemTuple(left), SemTuple(right)) -> pinConcreteShapes(quantified)(left)(right)(substitution)
        | (SemNamed(leftId, _leftName, left), SemNamed(rightId, _rightName, right)) ->
            if leftId == rightId
            then pinConcreteShapes(quantified)(left)(right)(substitution)
            else None
        | _ ->
            if formal == actual
            then Some(substitution)
            else None
and pinConcreteShapes quantified formals actuals substitution =
    match (formals, actuals) with
        | ([], []) -> Some(substitution)
        | (formal :: restFormal, actual :: restActual) ->
            match pinConcreteShape(quantified)(formal)(actual)(substitution) with
                | Some(next) -> pinConcreteShapes(quantified)(restFormal)(restActual)(next)
                | None -> None
        | _ -> None

let provenEmptyArrowRow row =
    match row with
        | None -> true
        | Some(SemRow([], None)) -> true
        | _ -> false

// Choose an empty-row instantiation only for quantified source rows, before
// inferring a call. Captured rows and actual capabilities are never erased.
let recursive pureConcreteRows quantified arity functionType substitution =
    if arity == 0
    then Some(substitution)
    else
        match functionType with
            | SemFunction(_parameter, result, row) ->
                let pinned =
                    match row with
                        | None -> Some(substitution)
                        | Some(formalRow) ->
                            pinConcreteShape(quantified)(formalRow)(SemRow([])(None))(substitution)
                in
                    match pinned with
                        | Some(next) -> pureConcreteRows(quantified)(arity - 1)(result)(next)
                        | None -> None
            | _ -> None

let preparePureConcreteScheme (scheme: TypeScheme) arity =
    match pureConcreteRows(scheme.quantified)(arity)(scheme.body)([]) with
        | None -> None
        | Some(substitution) ->
            let constraints =
                Ashes.Collection.List.map((given (constraint: TraitConstraint) ->
                    constraint with typeArguments = Ashes.Collection.List.map(applySubstitution(substitution))(constraint.typeArguments)))(scheme.constraints)
            in Some((scheme with body = applySubstitution(substitution)(scheme.body), constraints = constraints))

let recursive pinConcreteParameters quantified functionType arguments substitution =
    match arguments with
        | [] -> Some(substitution)
        | argument :: rest ->
            match functionType with
                | SemFunction(parameter, result, row) ->
                    if provenEmptyArrowRow(row)
                    then
                        match pinConcreteShape(quantified)(parameter)(argument)(substitution) with
                            | Some(next) -> pinConcreteParameters(quantified)(result)(rest)(next)
                            | None -> None
                    else None
                | _ -> None

// Pinning is a pure compatibility check, not unification against the caller's
// mutable inference state. Repeated quantified occurrences must agree exactly.
let pinConcreteConstrainedFunction (scheme: TypeScheme) argumentTypes =
    if concreteConstrainedParameters(argumentTypes)
    then
        match pinConcreteParameters(scheme.quantified)(scheme.body)(argumentTypes)([]) with
            | Some(substitution) ->
                scheme.body
                |> applySubstitution(substitution)
                |> Some
            | None -> None
    else None

// The call has already inferred a fresh instantiation. Only its proven empty
// rows may close quantified source rows; argument pinning alone cannot do that.
// The result is checked, never independently pinned, so the argument-only cache
// cannot conflate unrelated result variables.
let recursive pinProvenParameters quantified formal proven arguments substitution =
    match arguments with
        | [] ->
            let result = applySubstitution(substitution)(formal)
            in
                if concreteConstrainedParameter(result) && result == proven
                then Some(substitution)
                else None
        | argument :: rest ->
            match (formal, proven) with
                | (SemFunction(parameter, result, row), SemFunction(provenParameter, provenResult, provenRow)) ->
                    if argument == provenParameter && provenEmptyArrowRow(provenRow)
                    then
                        match pinConcreteShape(quantified)(parameter)(argument)(substitution) with
                            | None -> None
                            | Some(next) ->
                                let rowPinned =
                                    match row with
                                        | None -> Some(next)
                                        | Some(formalRow) ->
                                            pinConcreteShape(quantified)(formalRow)(SemRow([])(None))(next)
                                in
                                    match rowPinned with
                                        | None -> None
                                        | Some(withRow) -> pinProvenParameters(quantified)(result)(provenResult)(rest)(withRow)
                    else None
                | _ -> None

let pinProvenConcreteConstrainedFunction (scheme: TypeScheme) argumentTypes provenFunctionType =
    if concreteConstrainedParameters(argumentTypes)
    then
        match pinProvenParameters(scheme.quantified)(scheme.body)(provenFunctionType)(argumentTypes)([]) with
            | Some(substitution) ->
                scheme.body
                |> applySubstitution(substitution)
                |> Some
            | None -> None
    else None

// Exact source saturation is checked by the caller. Requiring a closed result as
// well means the parameter-only cache key cannot conflate independent result types.
let recursive concreteConstrainedArguments arity functionType =
    if arity == 0
    then
        if concreteConstrainedParameter(functionType)
        then Some([])
        else None
    else
        match functionType with
            | SemFunction(parameter, result, row) ->
                if concreteConstrainedParameter(parameter) && provenEmptyArrowRow(row)
                then
                    match concreteConstrainedArguments(arity - 1)(result) with
                        | Some(rest) -> Some(parameter :: rest)
                        | None -> None
                else None
            | _ -> None
