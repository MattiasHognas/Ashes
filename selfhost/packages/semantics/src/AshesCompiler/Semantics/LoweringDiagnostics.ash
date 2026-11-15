// Renders a lowering error as the diagnostic stage 0 reports for it: the same code, message text,
// and span, so a diagnostic parity fixture compares the two compilers byte for byte. A type reads
// as stage 0 prints it in a message (`List<Int>`, `(a, b)`, `a -> b`), with the type variables of
// one message named `a`, `b`, ... in order of first appearance across both sides.

import Ashes.Collection.List.append
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import Ashes.Collection.List.sortBy
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
export (
    value loweringErrorDiagnostic,
    value loweringErrorLocation,
    value diagnosticTypeText,
    value diagnosticTypePairText,
)

let typeMismatchCode = "ASH002"

let arrowPrecedence = 1

let atomPrecedence = 2

// Spreadsheet-style base-26 names: a .. z, aa, ab, ...
let recursive variableNameFrom (index: Int) (suffix: Str) =
    (let name = Ashes.Text.substring("abcdefghijklmnopqrstuvwxyz")(index % 26)(1) + suffix
    in
        if index / 26 - 1 < 0
        then name
        else variableNameFrom(index / 26 - 1)(name))

let recursive lookupVariableName (names: List((Int, Str))) (variableId: Int) =
    match names with
        | [] -> None
        | (id, name) :: rest ->
            if id == variableId
            then Some(name)
            else lookupVariableName(rest)(variableId)

// The name a variable already has, or the next unused one, recorded in order of appearance.
let variableName (names: List((Int, Str))) (variableId: Int) =
    match lookupVariableName(names)(variableId) with
        | Some(name) -> (name, names)
        | None ->
            let name =
                variableNameFrom(length(names))("")
            in (name, append(names)([(variableId, name)]))

let capabilityEntryName (entry: SemanticType) =
    match entry with
        | SemCapability(name, _arguments) -> name
        | _ -> ""

let capabilityBefore (left: SemanticType) (right: SemanticType) = capabilityEntryName(left) <= capabilityEntryName(right)

let recursive typeText (names: List((Int, Str))) (parentPrecedence: Int) (semanticType: SemanticType) =
    match renderedType(names)(semanticType) with
        | (rendered, precedence, nextNames) ->
            if precedence < parentPrecedence
            then ("(" + rendered + ")", nextNames)
            else (rendered, nextNames)
and renderedType (names: List((Int, Str))) (semanticType: SemanticType) =
    match semanticType with
        | SemInt -> ("Int", atomPrecedence, names)
        | SemUInt(bits) -> ("u" + Ashes.Text.fromInt(bits), atomPrecedence, names)
        | SemFloat -> ("Float", atomPrecedence, names)
        | SemBigInt -> ("BigInt", atomPrecedence, names)
        | SemString -> ("Str", atomPrecedence, names)
        | SemRune -> ("Rune", atomPrecedence, names)
        | SemBytes -> ("Bytes", atomPrecedence, names)
        | SemBool -> ("Bool", atomPrecedence, names)
        | SemNever -> ("Never", atomPrecedence, names)
        | SemList(element) ->
            match typeText(names)(atomPrecedence)(element) with
                | (elementText, nextNames) -> ("List<" + elementText + ">", atomPrecedence, nextNames)
        | SemTuple(elements) ->
            match typeListText(names)(0)(elements) with
                | (elementsText, nextNames) -> ("(" + elementsText + ")", atomPrecedence, nextNames)
        | SemFunction(argument, result, row) ->
            match typeText(names)(atomPrecedence)(argument) with
                | (argumentText, argumentNames) ->
                    match typeText(argumentNames)(arrowPrecedence)(result) with
                        | (resultText, resultNames) ->
                            match rowSuffixText(resultNames)(row) with
                                | (rowText, rowNames) -> (argumentText + " -> " + resultText + rowText, arrowPrecedence, rowNames)
        | SemVariable(variableId) ->
            match variableName(names)(variableId) with
                | (name, nextNames) -> (name, atomPrecedence, nextNames)
        | SemCapability(name, arguments) ->
            match capabilityText(names)(name)(arguments) with
                | (text, nextNames) -> (text, atomPrecedence, nextNames)
        | SemRow(capabilities, tail) ->
            match rowText(names)(capabilities)(tail) with
                | (text, nextNames) -> (text, atomPrecedence, nextNames)
        | SemNamed(_id, name, []) -> (name, atomPrecedence, names)
        | SemNamed(_id, name, arguments) ->
            match typeListText(names)(atomPrecedence)(arguments) with
                | (argumentsText, nextNames) -> (name + "<" + argumentsText + ">", atomPrecedence, nextNames)
        | SemParameter(_id, name) -> (name, atomPrecedence, names)
        | SemOpaque(name) -> (name, atomPrecedence, names)
        | SemPointer(pointee) ->
            match typeText(names)(atomPrecedence)(pointee) with
                | (pointeeText, nextNames) -> ("*" + pointeeText, atomPrecedence, nextNames)
and typeListText (names: List((Int, Str))) (precedence: Int) (types: List(SemanticType)) = typeListTextInto(names)(precedence)(types)([])
and typeListTextInto (names: List((Int, Str))) (precedence: Int) (types: List(SemanticType)) (reversed: List(Str)) =
    match types with
        | [] ->
            (reversed
            |> reverse
            |> Ashes.Text.join(", "), names)
        | head :: rest ->
            match typeText(names)(precedence)(head) with
                | (headText, nextNames) -> typeListTextInto(nextNames)(precedence)(rest)(headText :: reversed)
and capabilityText (names: List((Int, Str))) (name: Str) (arguments: List(SemanticType)) =
    match arguments with
        | [] -> (name, names)
        | _ ->
            match typeListText(names)(0)(arguments) with
                | (argumentsText, nextNames) -> (name + "(" + argumentsText + ")", nextNames)
and rowText (names: List((Int, Str))) (capabilities: List(SemanticType)) (tail: Maybe(SemanticType)) =
    match capabilities
    |> sortBy(capabilityBefore)
    |> typeListText(names)(0) with
        | (itemsText, itemNames) ->
            match tail with
                | Some(SemVariable(variableId)) ->
                    match variableName(itemNames)(variableId) with
                        | (tailName, tailNames) -> ("{" + itemsText + " | " + tailName + "}", tailNames)
                | _ -> ("{" + itemsText + "}", itemNames)
// An arrow's capability row shows only when it names a capability; a pure arrow or a bare row
// variable renders as an ordinary function type.
and rowSuffixText (names: List((Int, Str))) (row: Maybe(SemanticType)) =
    match row with
        | Some(SemRow(capabilities, tail)) ->
            match capabilities with
                | [] -> ("", names)
                | _ ->
                    match rowText(names)(capabilities)(tail) with
                        | (text, nextNames) -> (" needs " + text, nextNames)
        | _ -> ("", names)

let diagnosticTypeText (semanticType: SemanticType) =
    match typeText([])(0)(semanticType) with
        | (text, _names) -> text

// Both sides of one message share their variable names.
let diagnosticTypePairText (left: SemanticType) (right: SemanticType) =
    match typeText([])(0)(left) with
        | (leftText, names) ->
            match typeText(names)(0)(right) with
                | (rightText, _names) -> (leftText, rightText)

let siteSpan (site: CoreMismatchSite) =
    match site with
        | CoreMismatchSite { span = Some(span) } -> span
        | _ -> TextSpan(start = 0, end = 0)

// Stage 0's argument context, appended to the message.
let siteContext (site: CoreMismatchSite) =
    match site with
        | CoreMismatchSite { argument = None } -> ""
        | CoreMismatchSite { argument = Some(CoreCallArgument { ordinal = ordinal, callee = Some(callee) }) } -> " Context: in argument #" + Ashes.Text.fromInt(ordinal) + " of call to '" + callee + "'."
        | CoreMismatchSite { argument = Some(CoreCallArgument { ordinal = ordinal, callee = None }) } -> " Context: in argument #" + Ashes.Text.fromInt(ordinal) + " of function call."

let mismatchDiagnostic (left: SemanticType) (right: SemanticType) (site: CoreMismatchSite) =
    match diagnosticTypePairText(left)(right) with
        | (leftText, rightText) ->
            DiagnosticEntry(
                span = siteSpan(site),
                message = "Type mismatch: " + leftText + " vs " + rightText + "." + siteContext(site),
                code = Some(typeMismatchCode)
            )

let unknownIdentifierCode = "ASH001"

let useAfterCloseCode = "ASH006"

let doubleCloseCode = "ASH007"

let useAfterMoveCode = "ASH008"

let forwardReferenceCode = "ASH014"

let traitResolutionCode = "ASH036"

// A diagnostic no site locates: the command line prints it without a position.
let unlocatedDiagnostic (code: Maybe(Str)) (message: Str) =
    Some(
        DiagnosticEntry(
            span = TextSpan(start = 0, end = 0),
            message = message,
            code = code
        )
    )

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

// A name no binding answers to, as stage 0 words it: an unknown member of a standard-library
// module, an unknown constructor, or an undefined variable. A dotted name outside the standard
// library's own modules has no stage-0 wording and keeps its structural form.
let unknownBindingDiagnostic (name: Str) =
    match moduleAndMember(Ashes.Text.split(name)("."))([]) with
        | ("", _member) ->
            if startsUppercase(name)
            then unlocatedDiagnostic(None)("Unknown constructor '" + name + "'.")
            else unlocatedDiagnostic(Some(unknownIdentifierCode))("Undefined variable '" + name + "'.")
        | (moduleName, member) ->
            if moduleName == "Ashes" || Ashes.Text.startsWith(moduleName)("Ashes.")
            then unlocatedDiagnostic(None)("Unknown member '" + member + "' in module " + moduleName + ".")
            else None

let printUnsupportedPrefix = "print does not support "

// The builtin failures stage 0 words for the user; every other one keeps its structural form.
let builtinFailureDiagnostic (text: Str) =
    if Ashes.Text.startsWith(text)(printUnsupportedPrefix)
    then
        unlocatedDiagnostic(None)("print() does not support type " + Ashes.Text.drop(text)(Ashes.Text.length(printUnsupportedPrefix)) + " yet.")
    else None

// The operator failures stage 0 words for the user: `+` over operands of two types, and a logical
// negation of a type that supplies none.
let operatorMismatchDiagnostic (operator: Str) (left: SemanticType) (right: SemanticType) =
    match operator with
        | "+" ->
            match diagnosticTypePairText(left)(right) with
                | (leftText, rightText) -> unlocatedDiagnostic(Some(typeMismatchCode))("'+' requires Int+Int, Float+Float, or Str+Str, got " + leftText + " and " + rightText + ".")
        | "not" ->
            let requirement = "Ashes.Trait.Not(" + diagnosticTypeText(left) + ")"
            in unlocatedDiagnostic(Some(traitResolutionCode))("No implementation supplies '" + requirement + "'. Requirement trace: " + requirement + ".")
        | _ -> None

// A call passing more arguments than its callee takes, as stage 0 words it for a constructor, for
// a value that is no function at all, and for a function.
let callArityDiagnostic (callee: Str) (expected: Int) (provided: Int) (actual: SemanticType) =
    if startsUppercase(callee)
    then unlocatedDiagnostic(None)("Constructor '" + callee + "' expects " + Ashes.Text.fromInt(expected) + " argument(s) but got " + Ashes.Text.fromInt(provided) + ".")
    else
        if expected == 0
        then unlocatedDiagnostic(None)("Attempted to call '" + callee + "' with " + Ashes.Text.fromInt(provided) + " argument(s), but its type is " + diagnosticTypeText(actual) + ", not a function.")
        else unlocatedDiagnostic(None)("Call to '" + callee + "' expects " + Ashes.Text.fromInt(expected) + " argument(s) but got " + Ashes.Text.fromInt(provided) + ".")

// The diagnostic stage 0 reports for the error, when the error has a stage-0 rendering. A type
// arity mismatch keeps only its counts, so it has none.
let loweringErrorDiagnostic (error: CoreLoweringError) =
    match error with
        | CoreCallTypeMismatch(TypeMismatch(left, right), site) ->
            site
            |> mismatchDiagnostic(left)(right)
            |> Some
        | CoreCallTypeMismatch(InfiniteType(_variableId, _semanticType), site) ->
            Some(
                DiagnosticEntry(
                    span = siteSpan(site),
                    message = "Occurs check failed (recursive type)." + siteContext(site),
                    code = None
                )
            )
        | ResourceUseAfterMove(message) -> unlocatedDiagnostic(Some(useAfterMoveCode))(message)
        | ResourceUseAfterClose(message) -> unlocatedDiagnostic(Some(useAfterCloseCode))(message)
        | ResourceDoubleClose(message) -> unlocatedDiagnostic(Some(doubleCloseCode))(message)
        | ForwardTopLevelReference(name) -> unlocatedDiagnostic(Some(forwardReferenceCode))("Binding '" + name + "' is not yet declared at this point.")
        | UnknownLoweringBinding(name) -> unknownBindingDiagnostic(name)
        | UnsupportedCoreBuiltinLowering(text) -> builtinFailureDiagnostic(text)
        | CoreOperatorTypeMismatch(operator, left, right) -> operatorMismatchDiagnostic(operator)(left)(right)
        | CoreCallArityMismatch(callee, expected, provided, actual) -> callArityDiagnostic(callee)(expected)(provided)(actual)
        | CoreConstructorArityMismatch(name, expected, actual) -> unlocatedDiagnostic(None)("Constructor '" + name + "' expects " + Ashes.Text.fromInt(expected) + " argument(s) but got " + Ashes.Text.fromInt(actual) + ".")
        | CoreConstructorPatternArityMismatch(name, expected, actual) -> unlocatedDiagnostic(None)("Constructor '" + name + "' expects " + Ashes.Text.fromInt(expected) + " argument(s) but pattern has " + Ashes.Text.fromInt(actual) + ".")
        | UnknownCoreRecordPatternField(typeName, field) -> unlocatedDiagnostic(None)("Record type '" + typeName + "' has no field '" + field + "'.")
        | _ -> None

let loweringErrorLocation (error: CoreLoweringError) =
    match error with
        | CoreCallTypeMismatch(_unificationError, CoreMismatchSite { location = location }) -> location
        | _ -> None
