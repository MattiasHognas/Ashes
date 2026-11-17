// Renders a standalone type inference error as the diagnostic stage 0 reports for the same
// program: stage 0's code and message, prefixed with `path:line:column` when the inference located
// the error. Names the stitcher gave a definition (a private type's or capability's compiler name)
// read as the source spells them. An error stage 0 has no wording for prints its structural form.

import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.InlineModules
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.DerivingExpansion.DerivingExpansionError
import AshesCompiler.Semantics.DiagnosticTypeText
import AshesCompiler.Semantics.ExternalAbi.ExternalAbiError
import AshesCompiler.Semantics.ExternalTyping.ExternalTypingError
import AshesCompiler.Semantics.ModuleSemanticStitching.StitchedDefinition
import AshesCompiler.Semantics.ModuleSemanticStitching.StitchedNameKind
import AshesCompiler.Semantics.ProgramInference.InferenceErrorLocation
import AshesCompiler.Semantics.ProgramInference.ProgramInferenceError
import AshesCompiler.Semantics.ProgramInference.ProgramInferenceResult
import AshesCompiler.Semantics.ProjectCompilationPlanning.readSourceText
import AshesCompiler.Semantics.ProjectSyntaxStitching.StitchedDefinitionPlacement
import AshesCompiler.Semantics.ProjectSyntaxStitching.StitchedSyntaxProject
import AshesCompiler.Semantics.SourceContext.buildSourceTextIndex
import AshesCompiler.Semantics.SourceContext.resolvePositionInIndex
import AshesCompiler.Semantics.TypeInference.ConstructorInferenceDefinition
import AshesCompiler.Semantics.TypeInference.TypeEnvironment
import AshesCompiler.Semantics.TypeInference.TypeInferenceError
import AshesCompiler.Semantics.TypeInference.resolveWrittenTypeBinding
import AshesCompiler.Semantics.TypeResolution.TypeResolutionError
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification.UnificationError
export (
    value inferenceErrorDiagnostic,
    value inferenceErrorWantsCallSiteWording,
    value inferenceErrorText,
)

let typeMismatchCode = "ASH002"

let unknownIdentifierCode = "ASH001"

let invalidTraitDeclarationCode = "ASH010"

let invalidTraitImplementationCode = "ASH011"

let traitCoherenceCode = "ASH012"

let forwardReferenceCode = "ASH014"

let unhandledCapabilityCode = "ASH017"

let capabilityNotPermittedCode = "ASH018"

let unknownCapabilityCode = "ASH019"

let invalidHandlerCode = "ASH020"

let traitCycleCode = "ASH025"

let duplicateProviderCode = "ASH026"

let ambiguousSatisfactionCode = "ASH027"

let traitResolutionCode = "ASH036"

let recursiveTypeAliasCode = "ASH039"

let coded (code: Str) (message: Str) = Some((Some(code), message))

let uncoded (message: Str) = Some((None, message))

let recursive definitionNamed (placements: List(StitchedDefinitionPlacement)) (compilerName: Str) =
    match placements with
        | [] -> None
        | StitchedDefinitionPlacement { definition = definition } :: rest ->
            if definition.compilerName == compilerName
            then Some(definition)
            else definitionNamed(rest)(compilerName)

// The name the source spells for a stitched compiler name: a private type or capability carries
// its module in its compiler name, which no message of stage 0 shows.
let sourceNameOf (placements: List(StitchedDefinitionPlacement)) (name: Str) =
    match definitionNamed(placements)(name) with
        | Some(definition) -> definition.sourceName
        | None -> name

let recursive withSourceNames (placements: List(StitchedDefinitionPlacement)) (semanticType: SemanticType) =
    match semanticType with
        | SemList(element) ->
            element
            |> withSourceNames(placements)
            |> SemList
        | SemTuple(elements) ->
            elements
            |> withSourceNamesIn(placements)
            |> SemTuple
        | SemFunction(argument, result, row) ->
            row
            |> withSourceNamesMaybe(placements)
            |> SemFunction(withSourceNames(placements)(argument))(withSourceNames(placements)(result))
        | SemCapability(name, arguments) ->
            arguments
            |> withSourceNamesIn(placements)
            |> SemCapability(sourceNameOf(placements)(name))
        | SemRow(capabilities, tail) ->
            tail
            |> withSourceNamesMaybe(placements)
            |> SemRow(withSourceNamesIn(placements)(capabilities))
        | SemNamed(id, name, arguments) ->
            arguments
            |> withSourceNamesIn(placements)
            |> SemNamed(id)(sourceNameOf(placements)(name))
        | SemPointer(pointee) ->
            pointee
            |> withSourceNames(placements)
            |> SemPointer
        | other -> other
and withSourceNamesIn (placements: List(StitchedDefinitionPlacement)) (types: List(SemanticType)) =
    match types with
        | [] -> []
        | head :: rest -> withSourceNames(placements)(head) :: withSourceNamesIn(placements)(rest)
and withSourceNamesMaybe (placements: List(StitchedDefinitionPlacement)) (semanticType: Maybe(SemanticType)) =
    match semanticType with
        | Some(inner) ->
            inner
            |> withSourceNames(placements)
            |> Some
        | None -> None

let typeText (placements: List(StitchedDefinitionPlacement)) (semanticType: SemanticType) =
    semanticType
    |> withSourceNames(placements)
    |> diagnosticTypeText

let recursive standardTraitNamed (placements: List(StitchedDefinitionPlacement)) (name: Str) =
    match placements with
        | [] -> None
        | StitchedDefinitionPlacement { definition = definition } :: rest ->
            if definition.moduleName == "Ashes.Trait" && definition.sourceName == name
            then Some(definition)
            else standardTraitNamed(rest)(name)

// A trait as stage 0 names it in a requirement: by its qualified name.
let qualifiedTraitName (placements: List(StitchedDefinitionPlacement)) (name: Str) =
    match definitionNamed(placements)(name) with
        | Some(definition) -> definition.qualifiedName
        | None ->
            match standardTraitNamed(placements)(name) with
                | Some(definition) -> definition.qualifiedName
                | None -> name

let recursive typeListText (placements: List(StitchedDefinitionPlacement)) (types: List(SemanticType)) (reversed: List(Str)) =
    match types with
        | [] ->
            reversed
            |> reverse
            |> Ashes.Text.join(", ")
        | head :: rest -> typeListText(placements)(rest)(typeText(placements)(head) :: reversed)

let requirementText (placements: List(StitchedDefinitionPlacement)) (traitName: Str) (types: List(SemanticType)) = qualifiedTraitName(placements)(traitName) + "(" + typeListText(placements)(types)([]) + ")"

let recursive firstTypeText (placements: List(StitchedDefinitionPlacement)) (types: List(SemanticType)) =
    match types with
        | [] -> "?"
        | head :: _rest -> typeText(placements)(head)

// A concrete requirement no implementation supplies. The compiler-private argument traits stand
// for the builtins whose argument stage 0 checks itself, so they read as those builtins' messages.
let missingImplementationDiagnostic (placements: List(StitchedDefinitionPlacement)) (traitName: Str) (types: List(SemanticType)) =
    match traitName with
        | "$PrintArgument" -> uncoded("print() does not support type " + firstTypeText(placements)(types) + " yet.")
        | "$UnsignedIntegerArgument" -> uncoded("Ashes.Number.UInt.toInt() expects an unsigned integer (u8/u16/u32/u64) but got " + firstTypeText(placements)(types) + ".")
        | "$TextFromIntArgument" -> uncoded("Ashes.Text.fromInt() expects Int but got " + firstTypeText(placements)(types) + ".")
        | "$TextToHexArgument" -> uncoded("Ashes.Text.toHex() expects Int but got " + firstTypeText(placements)(types) + ".")
        | _ ->
            let requirement = requirementText(placements)(traitName)(types)
            in coded(traitResolutionCode)("No implementation supplies '" + requirement + "'. Requirement trace: " + requirement + ".")

// The module path and the member of a dotted name: everything before the last dot, and what
// follows it.
let recursive moduleAndMember (parts: List(Str)) (reversedModule: List(Str)) =
    match parts with
        | [] -> ("", "")
        | member :: [] ->
            (reversedModule
            |> reverse
            |> Ashes.Text.join("."), member)
        | part :: rest -> moduleAndMember(rest)(part :: reversedModule)

let startsUppercase (name: Str) =
    (let first = Ashes.Text.substring(name)(0)(1)
    in first >= "A" && first <= "Z")

// Stage 0's intrinsic modules in the order it searches them for an undefined name's suggestion.
let suggestionModules =
    [
        "Ashes.Trait",
        "Ashes.IO",
        "Ashes.Core.Result",
        "Ashes.Collection.List",
        "Ashes.Number.Math",
        "Ashes.Collection.Array",
        "Ashes.Collection.Map",
        "Ashes.Collection.HashMap",
        "Ashes.Collection.HashTrie",
        "Ashes.Task.Parallel",
        "Ashes.Core.Maybe",
        "Ashes.Test",
        "Ashes.IO.File",
        "Ashes.IO.Directory",
        "Ashes.IO.Path",
        "Ashes.IO.Environment",
        "Ashes.Text",
        "Ashes.Text.Source",
        "Ashes.Rune",
        "Ashes.Number.BigInt",
        "Ashes.Byte",
        "Ashes.Ffi",
        "Ashes.Number.UInt",
        "Ashes.Net.Http",
        "Ashes.Net.Tcp",
        "Ashes.Net.Tcp.Server",
        "Ashes.Net.Http.Server",
        "Ashes.Net.Tls.Server",
        "Ashes.Net.Tls",
        "Ashes.Task",
        "Ashes.IO.Process",
        "Ashes.IO.Console",
        "Ashes.Text.Json",
        "Ashes.Net.Rpc",
        "Ashes.Text.Regex"
    ]

let recursive suggestionIn (builtins: TypeEnvironment) (name: Str) (modules: List(Str)) =
    match modules with
        | [] -> ""
        | moduleName :: rest ->
            match resolveWrittenTypeBinding(moduleName + "." + name)(builtins) with
                | Some(_scheme) -> " Did you mean '" + moduleName + "." + name + "'?"
                | None -> suggestionIn(builtins)(name)(rest)

let recursive declaredInModule (placements: List(StitchedDefinitionPlacement)) (moduleName: Str) (name: Str) =
    match placements with
        | [] -> false
        | StitchedDefinitionPlacement { definition = definition } :: rest ->
            if definition.moduleName == moduleName && definition.sourceName == name && definition.kind == StitchedValue
            then true
            else declaredInModule(rest)(moduleName)(name)

let locationModule (location: Maybe(InferenceErrorLocation)) =
    match location with
        | Some(InferenceErrorLocation { moduleName = Some(moduleName) }) -> moduleName
        | _ -> ""

// Whether the error was raised in a module of its own file. Stage 0 checks the declaration order
// of a file's top level only; a name an inline module declares further down is undefined there.
let inFileModule (location: Maybe(InferenceErrorLocation)) =
    match location with
        | Some(InferenceErrorLocation { sourcePath = Some(path) }) -> Ashes.Text.indexOf(path)("#") < 0
        | _ -> true

let recursive textCharacters (text: Str) (index: Int) (reversed: List(Str)) =
    if index >= Ashes.Text.length(text)
    then reverse(reversed)
    else textCharacters(text)(index + 1)(Ashes.Text.substring(text)(index)(1) :: reversed)

let smallest (first: Int) (second: Int) (third: Int) =
    if first <= second && first <= third
    then first
    else
        if second <= third
        then second
        else third

// One row of the edit-distance table: `previous` is the row above, `left` the cell just computed.
let recursive distanceRow (character: Str) (target: List(Str)) (previous: List(Int)) (left: Int) (reversed: List(Int)) =
    match (target, previous) with
        | (candidate :: targetRest, diagonal :: above :: previousRest) ->
            let cost =
                if character == candidate
                then 0
                else 1
            in
                let value = smallest(above + 1)(left + 1)(diagonal + cost)
                in distanceRow(character)(targetRest)(above :: previousRest)(value)(value :: reversed)
        | _ -> reverse(reversed)

let recursive lastNumber (numbers: List(Int)) (fallback: Int) =
    match numbers with
        | [] -> fallback
        | head :: rest -> lastNumber(rest)(head)

let recursive numbersUpTo (count: Int) (reversed: List(Int)) =
    if count < 0
    then reversed
    else numbersUpTo(count - 1)(count :: reversed)

let recursive editRows (source: List(Str)) (target: List(Str)) (previous: List(Int)) (row: Int) =
    match source with
        | [] -> lastNumber(previous)(0)
        | character :: rest ->
            editRows(rest)(target)(distanceRow(character)(target)(previous)(row)([row]))(row + 1)

// The Levenshtein distance between two names.
let editDistance (left: Str) (right: Str) =
    (let target = textCharacters(right)(0)([])
    in
        editRows(textCharacters(left)(0)([]))(target)(numbersUpTo(Ashes.Text.length(right))([]))(1))

let recursive builtinConstructorNames (definitions: List(ConstructorInferenceDefinition)) (names: List(Str)) =
    match definitions with
        | [] -> names
        | definition :: rest -> builtinConstructorNames(rest)(definition.name :: names)

let recursive stitchedConstructorNames (placements: List(StitchedDefinitionPlacement)) =
    match placements with
        | [] -> []
        | StitchedDefinitionPlacement { definition = definition } :: rest ->
            if definition.kind == StitchedConstructor
            then definition.sourceName :: stitchedConstructorNames(rest)
            else stitchedConstructorNames(rest)

let recursive appendTexts (left: List(Str)) (right: List(Str)) =
    match left with
        | [] -> right
        | head :: rest -> head :: appendTexts(rest)(right)

let recursive closeNames (name: Str) (threshold: Int) (candidates: List(Str)) (seen: List(Str)) =
    match candidates with
        | [] -> []
        | candidate :: rest ->
            if containsName(seen)(candidate)
            then closeNames(name)(threshold)(rest)(seen)
            else
                let distance = editDistance(name)(candidate)
                in
                    if distance <= threshold
                    then (distance, candidate) :: closeNames(name)(threshold)(rest)(candidate :: seen)
                    else closeNames(name)(threshold)(rest)(candidate :: seen)
and containsName (names: List(Str)) (wanted: Str) =
    match names with
        | [] -> false
        | head :: rest ->
            if head == wanted
            then true
            else containsName(rest)(wanted)

let recursive firstNames (pairs: List((Int, Str))) (count: Int) =
    match pairs with
        | [] -> []
        | (_distance, name) :: rest ->
            if count <= 0
            then []
            else name :: firstNames(rest)(count - 1)

// Stage 0's suggestion for an unknown constructor: up to three constructors in scope within an
// edit distance of half the name's length (at least 3), nearest first, the builtin constructors
// ahead of the program's own when equally near.
let constructorHint (placements: List(StitchedDefinitionPlacement)) (builtins: TypeEnvironment) (name: Str) =
    (let threshold =
        if Ashes.Text.length(name) / 2 > 3
        then Ashes.Text.length(name) / 2
        else 3
    in
        let candidates =
            placements
            |> stitchedConstructorNames
            |> appendTexts(builtinConstructorNames(builtins.constructors)([]))
        in
            let ranked =
                []
                |> closeNames(name)(threshold)(candidates)
                |> Ashes.Collection.List.sortBy(given (left) ->
                    given (right) ->
                        match (left, right) with
                            | ((leftDistance, _leftName), (rightDistance, _rightName)) -> leftDistance <= rightDistance)
            in
                match firstNames(ranked)(3) with
                    | [] -> ""
                    | names -> " Did you mean: " + Ashes.Text.join(", ")(names) + "?")

// A name no binding answers to, as stage 0 words it: a top-level binding of the same module that
// is declared further down, an unknown member of a standard-library module, an unknown
// constructor, or an undefined variable with the intrinsic member it may have meant.
let unknownValueDiagnostic (placements: List(StitchedDefinitionPlacement)) (builtins: TypeEnvironment) (location: Maybe(InferenceErrorLocation)) (name: Str) =
    match moduleAndMember(Ashes.Text.split(name)("."))([]) with
        | ("", _member) ->
            if inFileModule(location) && declaredInModule(placements)(locationModule(location))(name)
            then coded(forwardReferenceCode)("Binding '" + name + "' is not yet declared at this point.")
            else
                if startsUppercase(name)
                then uncoded("Unknown constructor '" + name + "'." + constructorHint(placements)(builtins)(name))
                else coded(unknownIdentifierCode)("Undefined variable '" + name + "'." + suggestionIn(builtins)(name)(suggestionModules))
        | (moduleName, member) -> uncoded("Unknown member '" + member + "' in module " + moduleName + ".")

let recursive capabilityNames (placements: List(StitchedDefinitionPlacement)) (entries: List(SemanticType)) (reversed: List(Str)) =
    match entries with
        | [] -> reverse(reversed)
        | SemCapability(name, []) :: rest -> capabilityNames(placements)(rest)(sourceNameOf(placements)(name) :: reversed)
        | SemCapability(name, arguments) :: rest -> capabilityNames(placements)(rest)(sourceNameOf(placements)(name) + "(" + typeListText(placements)(arguments)([]) + ")" :: reversed)
        | _ :: rest -> capabilityNames(placements)(rest)(reversed)

let recursive containsText (items: List(Str)) (wanted: Str) =
    match items with
        | [] -> false
        | head :: rest ->
            if head == wanted
            then true
            else containsText(rest)(wanted)

let recursive withoutTexts (items: List(Str)) (removed: List(Str)) =
    match items with
        | [] -> []
        | head :: rest ->
            if containsText(removed)(head)
            then withoutTexts(rest)(removed)
            else head :: withoutTexts(rest)(removed)

let sortedTexts (items: List(Str)) =
    Ashes.Collection.List.sortBy(given (left) ->
        given (right) -> left <= right)(items)

// A capability row that does not fit a closed row: the capabilities the closed row lacks, as
// stage 0 lists them, against the closed row's own.
let closedRowDiagnostic (placements: List(StitchedDefinitionPlacement)) (openEntries: List(SemanticType)) (closed: List(SemanticType)) =
    (let closedNames = capabilityNames(placements)(closed)([])
    in
        let missing =
            withoutTexts(capabilityNames(placements)(openEntries)([]))(closedNames)
        in
            match missing with
                | [] -> None
                | _ ->
                    let names =
                        missing
                        |> sortedTexts
                        |> Ashes.Text.join(", ")
                    in
                        let subject =
                            match missing with
                                | _single :: [] -> "Capability '" + names + "' is"
                                | _ -> "Capabilities '" + names + "' are"
                        in
                            coded(capabilityNotPermittedCode)(subject + " not permitted by the closed row needs {" + Ashes.Text.join(", ")(sortedTexts(closedNames)) + "}."))

// A row's capabilities, including those of a row its tail was bound to.
let recursive rowEntries (entries: List(SemanticType)) (tail: Maybe(SemanticType)) =
    match tail with
        | Some(SemRow(tailEntries, tailTail)) ->
            tailTail
            |> rowEntries(tailEntries)
            |> appendTypes(entries)
        | _ -> entries
and appendTypes (left: List(SemanticType)) (right: List(SemanticType)) =
    match left with
        | [] -> right
        | head :: rest -> head :: appendTypes(rest)(right)

let unificationDiagnostic (placements: List(StitchedDefinitionPlacement)) (error: UnificationError) =
    match error with
        | TypeMismatch(SemRow(left, leftTail), SemRow(right, None)) ->
            closedRowDiagnostic(placements)(rowEntries(left)(leftTail))(right)
        | TypeMismatch(SemRow(left, None), SemRow(right, rightTail)) ->
            closedRowDiagnostic(placements)(rowEntries(right)(rightTail))(left)
        | TypeMismatch(left, right) ->
            match right
            |> withSourceNames(placements)
            |> diagnosticTypePairText(withSourceNames(placements)(left)) with
                | (leftText, rightText) -> coded(typeMismatchCode)("Type mismatch: " + leftText + " vs " + rightText + ".")
        | InfiniteType(_variable, _semanticType) -> uncoded("Occurs check failed (recursive type).")
        | TypeArityMismatch(_left, _right) -> None

let typeResolutionDiagnostic (error: TypeResolutionError) =
    match error with
        | UnknownTypeName(name) -> uncoded("Unknown type name '" + name + "'.")
        | TypeNameArityMismatch(name, expected, actual) -> uncoded("Type '" + name + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s) but got " + Ashes.Text.fromInt(actual) + ".")

// `capability.operation` as the inference spells an operation in a handler error.
let operationParts (placements: List(StitchedDefinitionPlacement)) (text: Str) =
    match moduleAndMember(Ashes.Text.split(text)("."))([]) with
        | (capabilityName, operation) -> (sourceNameOf(placements)(capabilityName), operation)

let afterPrefix (text: Str) (prefix: Str) =
    prefix
    |> Ashes.Text.length
    |> Ashes.Text.drop(text)

// The handler errors stage 0 words: a missing arm, an arm for an operation the capability does
// not declare, and a repeated arm or `return` arm.
let handlerDiagnostic (placements: List(StitchedDefinitionPlacement)) (text: Str) =
    if Ashes.Text.startsWith(text)("missing operation arm ")
    then
        match "missing operation arm "
        |> afterPrefix(text)
        |> operationParts(placements) with
            | (capabilityName, operation) -> coded(invalidHandlerCode)("Handler for capability '" + capabilityName + "' must handle operation '" + operation + "'.")
    else
        if Ashes.Text.startsWith(text)("unknown operation ")
        then
            match "unknown operation "
            |> afterPrefix(text)
            |> operationParts(placements) with
                | (capabilityName, operation) -> coded(invalidHandlerCode)("Handler arm '" + capabilityName + "." + operation + "' does not name a declared capability operation.")
        else
            if Ashes.Text.startsWith(text)("duplicate operation arm ")
            then
                match "duplicate operation arm "
                |> afterPrefix(text)
                |> operationParts(placements) with
                    | (capabilityName, operation) -> coded(invalidHandlerCode)("Duplicate handler arm for '" + capabilityName + "." + operation + "'.")
            else
                if text == "duplicate return arm"
                then coded(invalidHandlerCode)("Duplicate 'return' arm in handler.")
                else
                    if text == "the return arm takes exactly one parameter"
                    then coded(invalidHandlerCode)("The 'return' arm takes exactly one parameter.")
                    else None

let recursive quotedNames (names: List(Str)) (reversed: List(Str)) =
    match names with
        | [] ->
            reversed
            |> reverse
            |> Ashes.Text.join(", ")
        | name :: rest -> quotedNames(rest)("'" + name + "'" :: reversed)

let expressionDiagnostic (placements: List(StitchedDefinitionPlacement)) (builtins: TypeEnvironment) (location: Maybe(InferenceErrorLocation)) (error: TypeInferenceError) =
    match error with
        | UnknownValue(name) -> unknownValueDiagnostic(placements)(builtins)(location)(name)
        | InferenceUnificationError(unification) -> unificationDiagnostic(placements)(unification)
        | InferenceTypeResolutionError(resolution) -> typeResolutionDiagnostic(resolution)
        | DuplicatePatternBinding(name) -> uncoded("Duplicate binding '" + name + "' in pattern.")
        | UnknownPatternConstructor(name) -> uncoded("Unknown constructor '" + name + "' in pattern." + constructorHint(placements)(builtins)(name))
        | ConstructorPatternArityMismatch(name, expected, actual) -> uncoded("Constructor '" + name + "' expects " + Ashes.Text.fromInt(expected) + " argument(s) but pattern has " + Ashes.Text.fromInt(actual) + ".")
        | UnknownRecordPatternField(typeName, field) -> uncoded("Record type '" + sourceNameOf(placements)(typeName) + "' has no field '" + field + "'.")
        | UnknownRecordType(typeName) -> uncoded("Unknown record type '" + sourceNameOf(placements)(typeName) + "'.")
        | RecordUpdateRequiresRecord(target) -> uncoded("Record update requires a record type, got " + typeText(placements)(target) + ".")
        | MissingRecordField(typeName, field) -> uncoded("Missing field '" + field + "' in record literal for '" + sourceNameOf(placements)(typeName) + "'.")
        | UnknownRecordField(typeName, field) -> uncoded("Record type '" + sourceNameOf(placements)(typeName) + "' has no field '" + field + "'.")
        | PerformRequiresCapabilityOperation -> coded(unknownCapabilityCode)("'perform' must be applied to a capability operation call.")
        | UnknownCapabilityOperation(capabilityName, operation) -> coded(unknownCapabilityCode)("Capability '" + sourceNameOf(placements)(capabilityName) + "' has no operation '" + operation + "'.")
        | InvalidHandler(text) -> handlerDiagnostic(placements)(text)
        | AmbiguousCapabilitySatisfaction(capabilityName) -> coded(ambiguousSatisfactionCode)("Capability '" + sourceNameOf(placements)(capabilityName) + "' is satisfied both by a provider and by an enclosing handler. Choose one.")
        | UnsatisfiedCapability(capabilityName) -> coded(unhandledCapabilityCode)("Unsatisfied capability '" + sourceNameOf(placements)(capabilityName) + "': no handler or provider satisfies it.")
        | UnknownCapabilityInRow(capabilityName) -> coded(unknownCapabilityCode)("Unknown capability '" + capabilityName + "' in needs row.")
        | InconsistentOrPatternBindings -> uncoded("Every alternative of an or-pattern must bind exactly the same names.")
        | UnknownWrittenTraitRequirement(traitName) -> coded(invalidTraitDeclarationCode)("Unknown trait '" + traitName + "' in requires clause.")
        | WrittenTraitRequirementArityMismatch(traitName, expected, actual) -> coded(invalidTraitDeclarationCode)("Trait '" + traitName + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s), but got " + Ashes.Text.fromInt(actual) + ".")
        | MissingWrittenTraitRequirement(traitName, types) -> coded(invalidTraitDeclarationCode)("Written requires clause does not include inferred requirement '" + requirementText(placements)(traitName)(types) + "'.")
        | UnjustifiedWrittenTraitRequirement(traitName, types) -> coded(invalidTraitDeclarationCode)("Written requires clause includes unjustified requirement '" + requirementText(placements)(traitName)(types) + "'.")
        | AmbiguousTraitRequirement(traitName, types) -> coded(invalidTraitDeclarationCode)("Trait requirement '" + requirementText(placements)(traitName)(types) + "' is ambiguous: its type cannot be determined from context, so no evidence can be supplied for it.")
        | NoTraitImplementation(traitName, types) -> missingImplementationDiagnostic(placements)(traitName)(types)
        | NonExhaustiveMatch(message) -> uncoded(message)
        | UnreachableMatchArm(message) -> uncoded(message)
        | ConstructorPatternsFromDifferentAdts(names) -> uncoded("Constructor patterns from different ADTs (" + quotedNames(names)([]) + ") cannot appear in the same match expression.")
        | TuplePatternArityMismatch(expected, actual) -> uncoded("Tuple pattern arity mismatch: expected " + Ashes.Text.fromInt(expected) + " element(s) but got " + Ashes.Text.fromInt(actual) + ".")
        | BuiltinIntArgumentMismatch(name, argumentType) -> uncoded(name + "() expects Int but got " + typeText(placements)(argumentType) + ".")
        | LetResultRequiresResult(valueType) -> uncoded("let? requires a Result(E, A) expression. Got " + typeText(placements)(valueType) + ".")
        | LetResultBodyRequiresResult(bodyType) -> uncoded("let? body must produce a Result(E, A) expression. Got " + typeText(placements)(bodyType) + ".")
        | ExpectedResultType(leftType) -> coded(typeMismatchCode)("Type mismatch: " + typeText(placements)(leftType) + " vs Result<a, b>.")
        | ExpectedTaskType(awaitedType) -> coded(typeMismatchCode)("Type mismatch: " + typeText(placements)(awaitedType) + " vs Task<a, b>.")
        | _ -> None

let ffiBufferCode = "ASH044"

let ffiOutParameterCode = "ASH045"

let ffiStringCode = "ASH046"

let externalResourceDestructorCode = "ASH041"

// The external-declaration errors stage 0 words with the same facts; one whose stage-0 message
// names the function or the parameter number the error does not carry keeps its structural form.
let externalTypingDiagnostic (error: ExternalTypingError) =
    match error with
        | InvalidExternalTypePosition("FfiBuffer") -> coded(ffiBufferCode)("FfiBuffer(T) is supported only as a direct external parameter.")
        | InvalidExternalTypePosition("out") -> coded(ffiOutParameterCode)("out T is supported only as a direct external parameter.")
        | InvalidExternalTypePosition("FfiStr") -> coded(ffiStringCode)("FfiStr(...) is supported only as an external return or out-parameter element.")
        | InvalidExternalTypePosition("FfiStr parameter") -> coded(ffiStringCode)("FfiStr(...) is supported only as an external return or out-parameter element.")
        | InvalidExternalTypePosition("nullable out FfiStr") -> coded(ffiStringCode)("out FfiStr(...) is already nullable and cannot also specify 'nullable'.")
        | InvalidExternalBufferElement(_element) -> coded(ffiBufferCode)("FfiBuffer(T) requires a copyable opaque external type T.")
        | InvalidExternalOutElement(_element) -> coded(ffiOutParameterCode)("out T requires an opaque external type or pointer type T.")
        | _ -> None

let externalAbiDiagnostic (error: ExternalAbiError) =
    match error with
        | ExternalAbiSourceTypingError(typing) -> externalTypingDiagnostic(typing)
        | InvalidExternalResourceDestructor(resource, destructor) -> coded(externalResourceDestructorCode)("External resource '" + resource + "' requires destructor '" + destructor + "' with signature (consume " + resource + ") -> void.")
        | SharedExternalResourceDestructor(destructor) -> coded(externalResourceDestructorCode)("External resource destructor '" + destructor + "' is assigned to more than one resource type.")
        | InvalidExternalNativeStringDestructor(destructor) -> coded(ffiStringCode)("Owned FfiStr destructor '" + destructor + "' must be an external (*u8) -> void function in the same file.")
        | _ -> None

let derivingDiagnostic (error: DerivingExpansionError) =
    match error with
        | UnsupportedDerivedTrait(_typeName, traitName) -> coded(invalidTraitImplementationCode)("Trait '" + traitName + "' cannot be derived; supported traits are Eq, Ord, Show, and Hash.")
        | DuplicateDerivedTrait(typeName, traitName) -> coded(invalidTraitImplementationCode)("Trait '" + traitName + "' appears more than once in the deriving clause for '" + typeName + "'.")
        | UnsupportedDerivedField(typeName, traitName) -> coded(invalidTraitImplementationCode)("Cannot derive '" + traitName + "' for '" + typeName + "'.")

// The diagnostic stage 0 reports for an inference error, as its code and message, when stage 0
// words that error.
let recursive inferenceErrorDiagnostic (placements: List(StitchedDefinitionPlacement)) (builtins: TypeEnvironment) (location: Maybe(InferenceErrorLocation)) (error: ProgramInferenceError) =
    match error with
        | ProgramErrorAt(_location, inner) -> inferenceErrorDiagnostic(placements)(builtins)(location)(inner)
        | ProgramExpressionError(InferenceErrorAt(_span, inner)) -> inferenceErrorDiagnostic(placements)(builtins)(location)(ProgramExpressionError(inner))
        | ProgramExpressionError(inner) -> expressionDiagnostic(placements)(builtins)(location)(inner)
        | ProgramTypeResolutionError(resolution) -> typeResolutionDiagnostic(resolution)
        | DuplicateCapabilityDeclaration(name) -> uncoded("Duplicate capability name '" + sourceNameOf(placements)(name) + "'.")
        | ReservedCapabilityDeclaration(name) -> uncoded("Capability name '" + sourceNameOf(placements)(name) + "' is reserved for a built-in runtime capability.")
        | DuplicateCapabilityOperation(capabilityName, operation) -> uncoded("Duplicate operation '" + operation + "' in capability '" + sourceNameOf(placements)(capabilityName) + "'.")
        | ProviderCapabilityArityMismatch(capabilityName, expected, actual) -> coded(unknownCapabilityCode)("Capability '" + sourceNameOf(placements)(capabilityName) + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s) but the provider supplies " + Ashes.Text.fromInt(actual) + ".")
        | DuplicateCapabilityProvider(instance) -> coded(duplicateProviderCode)("Duplicate provider for '" + typeText(placements)(instance) + "'.")
        | UnknownProviderOperation(capabilityName, operation) -> coded(unknownCapabilityCode)("Capability '" + sourceNameOf(placements)(capabilityName) + "' has no operation '" + operation + "'.")
        | MissingProviderOperation(capabilityName, operation) -> coded(duplicateProviderCode)("Provider for '" + sourceNameOf(placements)(capabilityName) + "' is missing operation '" + operation + "'.")
        | DuplicateTraitDeclaration(name) -> coded(invalidTraitDeclarationCode)("Duplicate trait name '" + name + "'.")
        | TraitRequiresTypeParameter(name) -> coded(invalidTraitDeclarationCode)("Trait '" + name + "' must declare at least one type parameter.")
        | DuplicateTraitTypeParameter(traitName, parameter) -> coded(invalidTraitDeclarationCode)("Duplicate type parameter '" + parameter + "' in trait '" + traitName + "'.")
        | DuplicateTraitMethod(traitName, method) -> coded(invalidTraitDeclarationCode)("Duplicate method '" + method + "' in trait '" + traitName + "'.")
        | UnknownSupertrait(traitName, supertrait) -> coded(invalidTraitDeclarationCode)("Unknown supertrait '" + supertrait + "' of trait '" + traitName + "'.")
        | SupertraitArityMismatch(supertrait, expected, actual) -> coded(invalidTraitDeclarationCode)("Trait '" + supertrait + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s), but got " + Ashes.Text.fromInt(actual) + ".")
        | CyclicSupertraitRequirements(trace) -> coded(traitCycleCode)("Cyclic supertrait requirements: " + trace + ".")
        | UnknownTraitImplementation(traitName) -> coded(invalidTraitImplementationCode)("Unknown trait '" + traitName + "' in implementation declaration.")
        | TraitImplementationArityMismatch(traitName, expected, actual) -> coded(invalidTraitImplementationCode)("Trait '" + traitName + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s), but the implementation supplies " + Ashes.Text.fromInt(actual) + ".")
        | UnknownTraitImplementationRequirement(_implementation, requirement) -> coded(invalidTraitImplementationCode)("Unknown trait '" + requirement + "' in implementation requirement.")
        | TraitImplementationRequirementArityMismatch(traitName, expected, actual) -> coded(invalidTraitImplementationCode)("Trait '" + traitName + "' expects " + Ashes.Text.fromInt(expected) + " type argument(s), but got " + Ashes.Text.fromInt(actual) + ".")
        | TraitImplementationRequirementVariableEscapes(_implementation, variable) -> coded(invalidTraitImplementationCode)("Implementation requirement variable '" + variable + "' does not occur in the implementation head.")
        | UnknownTraitImplementationMethod(traitName, method) -> coded(invalidTraitImplementationCode)("Unknown method '" + method + "' in implementation of '" + traitName + "'.")
        | DuplicateTraitImplementationMethod(traitName, method) -> coded(invalidTraitImplementationCode)("Method '" + method + "' is implemented more than once in implementation of '" + traitName + "'.")
        | MissingTraitImplementationMethod(traitName, method) -> coded(invalidTraitImplementationCode)("Implementation of '" + traitName + "' is missing required method '" + method + "'.")
        | OrphanTraitImplementation(traitName, head, packageId) -> coded(traitCoherenceCode)("Orphan implementation '" + requirementText(placements)(traitName)(head) + "' is illegal: package '" + packageId + "' owns neither the trait nor an outer nominal head type.")
        | OverlappingTraitImplementations(traitName) -> coded(traitCoherenceCode)("Overlapping implementations for trait '" + traitName + "'.")
        | ProgramDerivingExpansionError(expansion) -> derivingDiagnostic(expansion)
        | ProgramExternalTypingError(typing) -> externalTypingDiagnostic(typing)
        | ProgramExternalAbiError(abi) -> externalAbiDiagnostic(abi)
        | RecursiveTypeAlias(cycle) -> coded(recursiveTypeAliasCode)("Recursive type alias cycle: " + cycle + ".")
        | ReservedTypeDeclaration(_name) -> uncoded("'Ashes' and built-in runtime types are reserved")
        | _ -> None

// A unification error carries the two types but not the call, argument, constructor or operator
// it was raised at, which stage 0 words its message after (an argument count, a non-function
// callee, an operand pair). Lowering knows that site, so its own diagnostic, when it words one, is
// the closer rendering. A mismatch between two capability rows is the closed-row check, and one
// between two tuples of different lengths is a pattern's shape against its value's; the inference
// words both itself.
let recursive inferenceErrorWantsCallSiteWording (error: ProgramInferenceError) =
    match error with
        | ProgramErrorAt(_location, inner) -> inferenceErrorWantsCallSiteWording(inner)
        | ProgramExpressionError(InferenceErrorAt(_span, inner)) -> inferenceErrorWantsCallSiteWording(ProgramExpressionError(inner))
        | ProgramExpressionError(InferenceUnificationError(TypeMismatch(SemRow(_left, _leftTail), SemRow(_right, _rightTail)))) -> false
        | ProgramExpressionError(InferenceUnificationError(TypeMismatch(SemTuple(_leftElements), SemTuple(_rightElements)))) -> false
        | ProgramExpressionError(InferenceUnificationError(_unification)) -> true
        | _ -> false

let maybeText (value: Maybe(Str)) =
    match value with
        | Some(text) -> text
        | None -> ""

// An inline module's region is named `<file>#<module>`. Its spans are offsets into the module's
// own text, which keeps the file's lines and columns (blank lines stand before it), so that text,
// lifted from the file again, gives the position in the file.
let locationFilePath (path: Str) =
    (let index = Ashes.Text.indexOf(path)("#")
    in
        if index < 0
        then path
        else Ashes.Text.take(path)(index))

let inlineModuleName (path: Str) =
    (let index = Ashes.Text.indexOf(path)("#")
    in
        if index < 0
        then ""
        else Ashes.Text.drop(path)(index + 1))

let locationOffset (location: InferenceErrorLocation) =
    match (location.span, location.declarationSpan) with
        | (Some(TextSpan { start = start }), _) -> Some(start)
        | (None, Some(TextSpan { start = start })) -> Some(start)
        | _ -> None

let fileText (entryPath: Str) (entrySource: Str) (path: Str) =
    if path == entryPath
    then Some(entrySource)
    else
        match readSourceText(path) with
            | Ok(text) -> Some(text)
            | Error(_) -> None

let textEndsWith (text: Str) (suffix: Str) =
    if Ashes.Text.length(suffix) > Ashes.Text.length(text)
    then false
    else Ashes.Text.drop(text)(Ashes.Text.length(text) - Ashes.Text.length(suffix)) == suffix

let recursive inlineModuleText (modules: List(InlineModuleInfo)) (name: Str) =
    match modules with
        | [] -> None
        | InlineModuleInfo { name = candidate, source = source } :: rest ->
            if candidate == name || textEndsWith(candidate)("." + name) || textEndsWith(name)("." + candidate)
            then Some(source)
            else inlineModuleText(rest)(name)

// The text a region's spans are offsets into: the file's, or an inline module's lifted text.
let regionText (entryPath: Str) (entrySource: Str) (sourcePath: Str) =
    match sourcePath
    |> locationFilePath
    |> fileText(entryPath)(entrySource) with
        | None -> None
        | Some(text) ->
            match inlineModuleName(sourcePath) with
                | "" -> Some(text)
                | name ->
                    match expandInlineModules("")(text) with
                        | Ok(InlineModuleExpansion { modules = modules }) -> inlineModuleText(modules)(name)
                        | Error(_) -> None

// `path:line:column ` for a located error, from the text of the file the error was raised in.
let locationPrefix (entryPath: Str) (entrySource: Str) (location: Maybe(InferenceErrorLocation)) =
    match location with
        | None -> ""
        | Some(located) ->
            match (located.sourcePath, locationOffset(located)) with
                | (Some(sourcePath), Some(offset)) ->
                    let path = locationFilePath(sourcePath)
                    in
                        match regionText(entryPath)(entrySource)(sourcePath) with
                            | None -> ""
                            | Some(text) ->
                                match resolvePositionInIndex(buildSourceTextIndex(text))(offset) with
                                    | (line, column) -> path + ":" + Ashes.Text.fromInt(line) + ":" + Ashes.Text.fromInt(column) + " "
                | _ -> ""

let codePrefix (code: Maybe(Str)) =
    match code with
        | Some(text) -> text + " "
        | None -> ""

// The rejected program's diagnostic line: `path:line:column CODE message` as stage 0 prints it, or
// the error's structural form with its location when stage 0 has no wording for it.
let inferenceErrorText (stitched: StitchedSyntaxProject) (builtins: TypeEnvironment) (entryPath: Str) (entrySource: Str) (result: ProgramInferenceResult) =
    match result.error with
        | None -> "Type inference reported no error."
        | Some(error) ->
            let prefix = locationPrefix(entryPath)(entrySource)(result.errorLocation)
            in
                match inferenceErrorDiagnostic(stitched.definitionPlacements)(builtins)(result.errorLocation)(error) with
                    | Some((code, message)) -> prefix + codePrefix(code) + message
                    | None -> prefix + "Type inference failed: " + Ashes.Trait.Show.show(error)
