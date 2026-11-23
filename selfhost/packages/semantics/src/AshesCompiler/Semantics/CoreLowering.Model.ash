// The public vocabulary of core lowering: its errors, its result, constructor layouts with their
// lookup index, and bindings. A part of `AshesCompiler.Semantics.CoreLowering`.

import Ashes.Collection.List.append
import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.TaglessAdtLayout
import AshesCompiler.Semantics.PatternBindingOwnership
import AshesCompiler.Semantics.TraitEvidenceThreading
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
// Stage 0's `ProducesFreshTuple`: a tuple literal, or a constructor carrying one, at some
// terminal arm, with no passthrough arm that could alias a tuple built elsewhere.
            // A call routed inside a nested lambda rewrites the enclosing function's accumulator
            // too, so the nested routings join the enclosing function's set.
            // A goal deferred inside the body is the program's, not the frame's: the pass that
            // proves it reads the record after the whole program is lowered.
export (
    type CoreCallArgument(..),
    type CoreMismatchSite(..),
    type CoreLoweringError(..),
    type ResourceReleaseKind(..),
    type CoreLoweringResult(..),
    type CoreConstructorLayout(..),
    type CoreConstructorLookupIndex(..),
    type CoreBindingLocation(..),
    type CoreBinding(..),
    value schemeResultName,
    value layoutResultName,
    value lookupStableConstructorFields,
    value lookupFirstConstructor,
    value lookupResultConstructors,
    value lookupFieldConstructors,
    value extendConstructorLookupIndex,
    value constructorLookupIndex,
)

// The call argument a mismatch was found for: the argument's 1-based ordinal in the spine and
// the callee's display name when the callee is a variable.
type CoreCallArgument =
    | ordinal: Int
    | callee: Maybe(Str)
    deriving {Eq, Show}

// Where a type mismatch was found: the innermost enclosing span and its resolved location, the
// span stage 0 reports a unification failure at, and, for an argument that failed to meet its
// parameter type, the argument, with the span moved to the call's (an argument's own span is
// restored to the call's once the argument is lowered).
type CoreMismatchSite =
    | span: Maybe(TextSpan)
    | location: Maybe(IrSourceLocation)
    | argument: Maybe(CoreCallArgument)
    deriving {Eq, Show}

type CoreLoweringError =
    | UnknownLoweringBinding(Str)
    | CoreCallRequiresFunction(SemanticType)
    | CoreCallArityMismatch(Str, Int, Int, SemanticType)
    | CoreCallTypeMismatch(UnificationError, CoreMismatchSite)
    | CoreOperatorTypeMismatch(Str, SemanticType, SemanticType)
    | CoreConstructorArityMismatch(Str, Int, Int)
    | CoreBuiltinArityMismatch(Str, Str, Int, Int)
    | UnsupportedCoreBuiltinLowering(Str)
    | CoreExternalDirectOnlyViolation(Str)
    | UnsupportedCoreExternalLowering(Str)
    | CoreUnhandledCapabilityOperation(Str, Str)
    | CoreAmbiguousCapabilitySatisfaction(Str)
    | UnknownCoreRecordField(Str, Str)
    | CoreRecordUpdateRequiresRecord(SemanticType)
    | UnsupportedCoreLoweringPattern(Str)
    | CoreConstructorPatternArityMismatch(Str, Int, Int)
    | UnknownCoreRecordPatternField(Str, Str)
    | CoreRecursiveBindingRequiresFunction(Str)
    | UnsupportedCoreLoweringExpression(Str)
    | DuplicateTopLevelBinding(Str)
    | UnsupportedOperationArmResume(Str, Str)
    | ForwardTopLevelReference(Str)
    | UnresolvedTraitEvidenceForwarding(TraitEvidenceForwardingError)
    | UnsupportedTypeDeclaration(Str)
    | CoreMatchCoverageError(Str)
    | ReservedTypeName(Str)
    | ReservedCapabilityName(Str)
    | DuplicateCapabilityName(Str)
    | DuplicateCapabilityOperationName(Str, Str)
    | PerformTargetNotCapabilityOperation(Str)
    // A capability, handler or provider diagnostic stage 0 reports: its code and message.
    | CoreCapabilityDiagnostic(Str, Str)
    | CoreImmediateEvidenceDiagnostic(Str)
    | ResourceUseAfterClose(Str)
    | ResourceUseAfterMove(Str)
    | ResourceDoubleClose(Str)
    | MissingCoreTraitEvidence(Str, SemanticType)
    | UnsupportedCoreTraitDispatch(Str, SemanticType)
    | UnsupportedCoreImplementationHead(Str)
    | UnsupportedCoreDerivingExpansion(Str)
    | ResourcePatternAliasConflict(Str)
    deriving {Eq, Show}

// How a resource binding stopped being its scope's responsibility: closed by an explicit close
// builtin, or moved into an aggregate, a consuming callee, or the arm result it escapes through. A
// binding with neither is live and closed at its scope exit.
type ResourceReleaseKind =
    | ResourceClosed
    | ResourceMoved
    deriving {Eq, Show}

type CoreLoweringResult =
    | program: Maybe(IrProgram)
    | semanticType: SemanticType
    | error: Maybe(CoreLoweringError)
    // Where the error was raised when lowering located it, as stage 0 reports it.
    | errorLocation: Maybe(IrSourceLocation)
    | valuePlacements: List((Int, Maybe(IrFunctionOrigin), Bool))
    | joinRepresentations: List((Maybe(IrFunctionOrigin), Int, Bool, Bool))

type CoreConstructorLayout =
    | name: Str
    | tag: Int
    | scheme: TypeScheme
    | fieldNames: List(Str)
    | isZeroCost: Bool
    | tagless: Bool
    deriving {Eq, Show}

let recursive schemeResultName (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_, result, _) -> schemeResultName(result)
        | SemNamed(_, name, _) -> Some(name)
        | _ -> None

let layoutResultName (layout: CoreConstructorLayout) =
    match layout with
        | CoreConstructorLayout { scheme = TypeScheme { body = body } } -> schemeResultName(body)

// Raw constructor declarations are indexed once per layout environment. Queries still instantiate
// and resolve their fields against the current inference state.
type CoreConstructorLookupIndex =
    | byConstructor: MapTree(Str, CoreConstructorLayout)
    | byResult: MapTree(Str, List(CoreConstructorLayout))
    | byField: MapTree(Str, List(CoreConstructorLayout))
    | stableFields: MapTree(Str, Maybe(List(SemanticType)))
    | taglessShapes: TaglessConstructorIndex

// Only completely closed schemes share fields across inference states. Generic and
// open schemes still instantiate at the query's current fresh-variable supply.
let recursive stableConstructorFieldTypes types =
    match types with
        | [] -> true
        | head :: rest -> stableConstructorFieldType(head) && stableConstructorFieldTypes(rest)
and stableConstructorFieldType semanticType =
    match semanticType with
        | SemVariable(_) -> false
        | SemParameter(_, _) -> false
        | SemList(element) -> stableConstructorFieldType(element)
        | SemPointer(element) -> stableConstructorFieldType(element)
        | SemTuple(elements) -> stableConstructorFieldTypes(elements)
        | SemNamed(_, _, arguments) -> stableConstructorFieldTypes(arguments)
        | SemCapability(_, arguments) -> stableConstructorFieldTypes(arguments)
        | SemFunction(argument, result, row) -> stableConstructorFieldTypes([argument, result]) && stableConstructorOptionalField(row)
        | SemRow(elements, tail) -> stableConstructorFieldTypes(elements) && stableConstructorOptionalField(tail)
        | _ -> true
and stableConstructorOptionalField optional =
    match optional with
        | None -> true
        | Some(semanticType) -> stableConstructorFieldType(semanticType)

let recursive closedConstructorFields body =
    match body with
        | SemFunction(parameter, result, _) -> parameter :: closedConstructorFields(result)
        | _ -> []

let stableConstructorFields (layout: CoreConstructorLayout) =
    match layout.scheme with
        | TypeScheme { quantified = [], body = body } ->
            if stableConstructorFieldType(body)
            then
                body
                |> closedConstructorFields
                |> Some
            else None
        | _ -> None

let extendStableConstructorFields name layout (index: CoreConstructorLookupIndex) =
    (let fields =
        match (stableConstructorFields(layout), Ashes.Collection.Map.getStr(name)(index.stableFields)) with
            | (Some(current), Some(Some(previous))) ->
                current
                |> append(previous)
                |> Some
            | (Some(current), None) -> Some(current)
            | _ -> None
    in index with stableFields = Ashes.Collection.Map.setStr(name)(fields)(index.stableFields))

let lookupStableConstructorFields name (index: CoreConstructorLookupIndex) =
    match Ashes.Collection.Map.getStr(name)(index.stableFields) with
        | Some(Some(fields)) -> Some(fields)
        | _ -> None

let lookupFirstConstructor (name: Str) (index: CoreConstructorLookupIndex) = Ashes.Collection.Map.getStr(name)(index.byConstructor)

let lookupResultConstructors (name: Str) (index: CoreConstructorLookupIndex) =
    match Ashes.Collection.Map.getStr(name)(index.byResult) with
        | Some(layouts) -> layouts
        | None -> []

let lookupFieldConstructors (name: Str) (index: CoreConstructorLookupIndex) =
    match Ashes.Collection.Map.getStr(name)(index.byField) with
        | Some(layouts) -> layouts
        | None -> []

let recursive constructorFieldNameOccurs (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | head :: rest -> head == name || constructorFieldNameOccurs(name)(rest)

let recursive extendConstructorFields (layout: CoreConstructorLayout) (names: List(Str)) (index: CoreConstructorLookupIndex) =
    match names with
        | [] -> index
        | name :: rest ->
            if constructorFieldNameOccurs(name)(rest)
            then extendConstructorFields(layout)(rest)(index)
            else
                let updated =
                    index with byField = Ashes.Collection.Map.setStr(name)(append(lookupFieldConstructors(name)(index))([layout]))(index.byField)
                in extendConstructorFields(layout)(rest)(updated)

// Adds layouts declared after every layout the index already holds, in declaration order. The
// first constructor of a name wins, and every group lists its layouts in declaration order, so a
// type declaration extends the index instead of rebuilding it over the whole program.
let recursive extendConstructorLookupIndex (layouts: List(CoreConstructorLayout)) (index: CoreConstructorLookupIndex) =
    match layouts with
        | [] -> index
        | layout :: rest ->
            let constructed =
                match lookupFirstConstructor(layout.name)(index) with
                    | Some(_earlier) -> index
                    | None -> index with byConstructor = Ashes.Collection.Map.setStr(layout.name)(layout)(index.byConstructor)
            in
                let named = extendConstructorFields(layout)(layout.fieldNames)((constructed with taglessShapes = extendTaglessConstructorIndex([layout.scheme])(constructed.taglessShapes)))
                in
                    match layoutResultName(layout) with
                        | None -> extendConstructorLookupIndex(rest)(named)
                        | Some(resultName) ->
                            (named with byResult = Ashes.Collection.Map.setStr(resultName)(append(lookupResultConstructors(resultName)(named))([layout]))(named.byResult))
                            |> extendStableConstructorFields(resultName)(layout)
                            |> extendConstructorLookupIndex(rest)

let constructorLookupIndex (layouts: List(CoreConstructorLayout)) = extendConstructorLookupIndex(layouts)(CoreConstructorLookupIndex(byConstructor = Ashes.Collection.Map.empty, byResult = Ashes.Collection.Map.empty, byField = Ashes.Collection.Map.empty, stableFields = Ashes.Collection.Map.empty, taglessShapes = emptyTaglessConstructorIndex(Unit)))

type CoreBindingLocation =
    | CoreLocal(Int)
    | CoreEnvironment(Int)
    | CoreSelf(Str, Int)

// `ownedRead` marks a `let` or pattern binding, whose reads borrow when the binding's resolved
// type is heap-represented (see `finishOwnedRead`), and a capture of such a binding borrows the
// same way (stage 0 finds the owner by name through the closure); a parameter or recursive
// self-reference is not owned by its reader and loads plainly. A pattern binding whose
// pre-lowering ownership fact says it keeps a reference of its own past its arm (stage 0's
// Perceus pattern owner) carries that fact: its reads borrow whatever its type, a tail self-call
// argument or aggregate child reading it takes a protective duplicate, and the loop's finalize
// pass decides whether those markers become real reference-count operations.
// `closedScheme` is set only for a scheme known to have no free type variable: the substitution
// leaves such a scheme as it is and it holds nothing back in a generalization, so both pass the
// binding over. A binding without the mark is resolved through the substitution at every use.
type CoreBinding =
    | name: Str
    | scheme: TypeScheme
    | location: CoreBindingLocation
    | ownedRead: Bool
    | patternFact: Maybe(PatternBindingFact)
    | closedScheme: Bool
    // Stage 0's `Binding.Scheme` for an ordinary `let`: a let-bound name, as opposed to a parameter,
    // a pattern binder or a capture (stage 0's `Binding.Local`).
    | letBound: Bool
