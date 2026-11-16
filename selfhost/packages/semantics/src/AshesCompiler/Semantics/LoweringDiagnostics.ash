// Renders a lowering error as the diagnostic stage 0 reports for it: the same code, message text,
// and span, so a diagnostic parity fixture compares the two compilers byte for byte. Types read as
// `DiagnosticTypeText` renders them.

import Ashes.Collection.List.reverse
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.DiagnosticTypeText
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
export (
    value loweringErrorDiagnostic,
    value loweringErrorLocation,
)

let typeMismatchCode = "ASH002"

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

let structuredTaskEscapeCode = "ASH043"

// The builtin failures stage 0 words for the user: an unsupported `print`, a join handle escaping
// its task scope (`ASH043`), and a fork outside an `async` body; every other one keeps its
// structural form.
let builtinFailureDiagnostic (text: Str) =
    if Ashes.Text.startsWith(text)(printUnsupportedPrefix)
    then
        unlocatedDiagnostic(None)("print() does not support type " + Ashes.Text.drop(text)(Ashes.Text.length(printUnsupportedPrefix)) + " yet.")
    else
        if Ashes.Text.startsWith(text)("A JoinHandle cannot escape")
        then unlocatedDiagnostic(Some(structuredTaskEscapeCode))(text)
        else
            if Ashes.Text.startsWith(text)("Ashes.Task.fork() must be used inside an async task")
            then unlocatedDiagnostic(None)(text)
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
        | CoreCapabilityDiagnostic(code, message) -> unlocatedDiagnostic(Some(code))(message)
        | ResourcePatternAliasConflict(message) -> unlocatedDiagnostic(None)(message)
        | _ -> None

let loweringErrorLocation (error: CoreLoweringError) =
    match error with
        | CoreCallTypeMismatch(_unificationError, CoreMismatchSite { location = location }) -> location
        | _ -> None
