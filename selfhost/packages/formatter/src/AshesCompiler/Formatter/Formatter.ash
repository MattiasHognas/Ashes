// Renders parsed syntax in the canonical Ashes source form.
//
// Invariants:
// - Source spans never affect rendered text.
// - Parentheses preserve precedence and associativity; explicitly multiline call arguments retain that layout.
// - Top-level declarations use canonical spacing and the result has one terminal newline.

import AshesCompiler.Frontend.Syntax
import Ashes.Collection.List.sortBy
export (
    value formatProgram,
    value formatExpression,
    value formatTypeExpression,
    value formatPattern,
)

let recursive formatterIndent : Int -> Str =
    given (count) ->
        if count <= 0
        then ""
        else " " + formatterIndent(count - 1)

let formatterWrap : Int -> Int -> Str -> Str =
    given (parent) ->
        given (own) ->
            given (text) ->
                if parent > own
                then "(" + text + ")"
                else text

let recursive formatterEscape : Str -> Str =
    given (text) ->
        match Ashes.Text.uncons(text) with
            | None -> ""
            | Some((head, tail)) ->
                let escaped =
                    if head == '\\'
                    then "\\\\"
                    else
                        if head == '"'
                        then "\\\""
                        else
                            if head == '\n'
                            then "\\n"
                            else
                                if head == '\r'
                                then "\\r"
                                else
                                    if head == '\t'
                                    then "\\t"
                                    else Ashes.Rune.toText(head)
                in escaped + formatterEscape(tail)

let formatterRune : Int -> Str =
    given (value) ->
        match Ashes.Rune.fromInt(value) with
            | Some(rune) -> Ashes.Rune.toText(rune)
            | None -> "�"

let recursive formatterJoin : Str -> (a -> Str) -> List(a) -> Str =
    given (separator) ->
        given (render) ->
            given (values) ->
                match values with
                    | [] -> ""
                    | value :: tail ->
                        let recursive rest : List(a) -> Str =
                            given (remaining) ->
                                match remaining with
                                    | [] -> ""
                                    | next :: nextTail -> separator + render(next) + rest(nextTail)
                        in render(value) + rest(tail)

let recursive formatterUnspanType : TypeExpr -> TypeExpr =
    given (typeExpression) ->
        match typeExpression with
            | TypeAt(_span, inner) -> formatterUnspanType(inner)
            | _ -> typeExpression

let recursive formatterType : TypeExpr -> Str =
    given (typeExpression) ->
        match formatterUnspanType(typeExpression) with
            | TypeNamed(name) -> name
            | TypeApplied(name, arguments) -> name + "(" + formatterJoin(", ")(formatterType)(arguments) + ")"
            | TypeTuple(elements) -> "(" + formatterJoin(", ")(formatterType)(elements) + ")"
            | TypeUnit -> "()"
            | TypeArrow(source, destination, capabilities, tailVariable) ->
                let sourceText =
                    match formatterUnspanType(source) with
                        | TypeArrow(_, _, _, _) -> "(" + formatterType(source) + ")"
                        | _ -> formatterType(source)
                in
                    let destinationText =
                        match (capabilities, tailVariable, formatterUnspanType(destination)) with
                            | ([], None, _) -> formatterType(destination)
                            | (_, _, TypeArrow(_, _, _, _)) -> "(" + formatterType(destination) + ")"
                            | _ -> formatterType(destination)
                    in sourceText + " -> " + destinationText + formatterNeeds(capabilities)(tailVariable)
            | TypeAt(_span, inner) -> formatterType(inner)
and formatterNeeds : List((Str, List(TypeExpr))) -> Maybe(Str) -> Str =
    given (capabilities) ->
        given (tailVariable) ->
            match (capabilities, tailVariable) with
                | ([], None) -> ""
                | ([], Some(tail)) -> " needs " + tail
                | _ ->
                    let renderCapability : (Str, List(TypeExpr)) -> Str =
                        given (item) ->
                            match item with
                                | (name, []) -> name
                                | (name, arguments) -> name + "(" + formatterJoin(", ")(formatterType)(arguments) + ")"
                    in
                        let capabilitiesText = formatterJoin(", ")(renderCapability)(capabilities)
                        in
                            match tailVariable with
                                | None -> " needs {" + capabilitiesText + "}"
                                | Some(tail) -> " needs {" + capabilitiesText + " | " + tail + "}"

let recursive formatterPatternAt : Pattern -> Int -> Str =
    given (pattern) ->
        given (parent) ->
            match pattern with
                | PatternEmptyList -> "[]"
                | PatternVar(name) -> name
                | PatternWildcard -> "_"
                | PatternInt(value) -> Ashes.Text.fromInt(value)
                | PatternString(value) -> "\"" + formatterEscape(value) + "\""
                | PatternRune(value) ->
                    "'" + formatterEscape(formatterRune(value)) + "'"
                | PatternBool(value) ->
                    if value
                    then "true"
                    else "false"
                | PatternTuple(elements) ->
                    "(" + formatterJoin(", ")(given (item) -> formatterPatternAt(item)(0))(elements) + ")"
                | PatternConstructor(name, arguments) ->
                    if arguments == []
                    then name
                    else
                        name + "(" + formatterJoin(", ")(given (item) -> formatterPatternAt(item)(0))(arguments) + ")"
                | PatternRecord(name, fields) ->
                    let renderField : (Str, Pattern) -> Str =
                        given (field) ->
                            match field with
                                | (fieldName, fieldPattern) -> fieldName + " = " + formatterPatternAt(fieldPattern)(0)
                    in name + " { " + formatterJoin(", ")(renderField)(fields) + " }"
                | PatternCons(head, tail) ->
                    let renderedHead = formatterPatternAt(head)(4)
                    in
                        let renderedTail = formatterPatternAt(tail)(3)
                        in formatterWrap(parent)(3)(renderedHead + " :: " + renderedTail)
                | PatternAs(inner, name) ->
                    let renderedInner = formatterPatternAt(inner)(2)
                    in formatterWrap(parent)(2)(renderedInner + " as " + name)
                | PatternOr(alternatives) ->
                    alternatives
                    |> formatterJoin(" | ")(given (item) -> formatterPatternAt(item)(0))
                    |> formatterWrap(parent)(1)
                | PatternAt(_span, inner) -> formatterPatternAt(inner)(parent)

let recursive formatterUnspanExpr : Expr -> Expr =
    given (expression) ->
        match expression with
            | ExprAt(_span, inner) -> formatterUnspanExpr(inner)
            | _ -> expression

// Giving the right operand a tighter parent precedence preserves left associativity.
let recursive formatterBinary : Int -> Int -> Str -> Expr -> Expr -> Str =
    given (parent) ->
        given (precedence) ->
            given (operator) ->
                given (left) ->
                    given (right) ->
                        formatterWrap(
                            parent,
                            precedence,
                            formatterExpr(left)(precedence)(0) + " " + operator + " " + formatterExpr(
                                right,
                                precedence + 1,
                                0
                            )
                        )
and formatterExpr : Expr -> Int -> Int -> Str =
    given (expression) ->
        given (parent) ->
            given (indent) ->
                match formatterUnspanExpr(expression) with
                    | ExprInt(value) -> Ashes.Text.fromInt(value)
                    | ExprBigInt(value) -> value + "N"
                    | ExprUInt(_value, _bits, text) -> text
                    | ExprFloat(_value, text) -> text
                    | ExprString(value) -> "\"" + formatterEscape(value) + "\""
                    | ExprRune(value) ->
                        "'" + formatterEscape(formatterRune(value)) + "'"
                    | ExprBool(value) ->
                        if value
                        then "true"
                        else "false"
                    | ExprVar(name) -> name
                    | ExprQualifiedVar(moduleName, name) -> moduleName + "." + name
                    | ExprAdd(left, right) -> formatterBinary(parent)(10)("+")(left)(right)
                    | ExprSubtract(left, right) -> formatterBinary(parent)(10)("-")(left)(right)
                    | ExprMultiply(left, right) -> formatterBinary(parent)(11)("*")(left)(right)
                    | ExprDivide(left, right) -> formatterBinary(parent)(11)("/")(left)(right)
                    | ExprModulo(left, right) -> formatterBinary(parent)(11)("%")(left)(right)
                    | ExprBitwiseAnd(left, right) -> formatterBinary(parent)(7)("&")(left)(right)
                    | ExprBitwiseOr(left, right) -> formatterBinary(parent)(5)("|")(left)(right)
                    | ExprBitwiseXor(left, right) -> formatterBinary(parent)(6)("^")(left)(right)
                    | ExprShiftLeft(left, right) -> formatterBinary(parent)(9)("<<")(left)(right)
                    | ExprShiftRight(left, right) -> formatterBinary(parent)(9)(">>")(left)(right)
                    | ExprGreaterThan(left, right) -> formatterBinary(parent)(4)(">")(left)(right)
                    | ExprLessThan(left, right) -> formatterBinary(parent)(4)("<")(left)(right)
                    | ExprGreaterOrEqual(left, right) -> formatterBinary(parent)(4)(">=")(left)(right)
                    | ExprLessOrEqual(left, right) -> formatterBinary(parent)(4)("<=")(left)(right)
                    | ExprEqual(left, right) -> formatterBinary(parent)(4)("==")(left)(right)
                    | ExprNotEqual(left, right) -> formatterBinary(parent)(4)("!=")(left)(right)
                    | ExprResultPipe(left, right) -> formatterBinary(parent)(3)("|?>")(left)(right)
                    | ExprResultMapErrorPipe(left, right) -> formatterBinary(parent)(3)("|!>")(left)(right)
                    | ExprCons(head, tail) ->
                        formatterWrap(
                            parent,
                            8,
                            formatterExpr(head)(9)(indent) + " :: " + formatterExpr(tail)(8)(indent)
                        )
                    | ExprBitwiseNot(operand) -> formatterWrap(parent)(12)("~" + formatterExpr(operand)(12)(indent))
                    | ExprLogicalNot(operand) -> formatterWrap(parent)(12)("!" + formatterExpr(operand)(12)(indent))
                    | ExprCall(function, argument, whitespace, layout) ->
                        let rendered =
                            if whitespace
                            then formatterExpr(function)(13)(indent) + " " + formatterExpr(argument)(14)(indent)
                            else formatterParenthesizedCall(expression)(function)(argument)(layout)(indent)
                        in formatterWrap(parent)(13)(rendered)
                    | ExprTuple(elements) -> "(" + formatterJoin(", ")(formatterExpressionInline)(elements) + ")"
                    | ExprList(elements, isMultiline) ->
                        if isMultiline
                        then formatterMultilineList(elements)(indent)
                        else "[" + formatterJoin(", ")(formatterExpressionInline)(elements) + "]"
                    | ExprRecord(name, fields) -> name + "(" + formatterJoin(", ")(formatterExprField)(fields) + ")"
                    | ExprRecordUpdate(target, fields) ->
                        formatterWrap(
                            parent,
                            2,
                            formatterExpr(
                                target,
                                3,
                                indent
                            ) + " with " + formatterJoin(", ")(formatterExprField)(fields)
                        )
                    | ExprAwait(task) -> formatterWrap(parent)(12)("await " + formatterExpr(task)(12)(indent))
                    | ExprPerform(operation) ->
                        formatterWrap(
                            parent,
                            12,
                            "perform " + formatterExpr(operation)(12)(indent)
                        )
                    | ExprIf(condition, thenBranch, elseBranch) ->
                        formatterWrap(
                            parent,
                            1,
                            "if " + formatterExpr(
                                condition,
                                0,
                                indent
                            ) + "\n" + formatterIndent(indent) + "then " + formatterExpr(
                                thenBranch,
                                0,
                                indent
                            ) + "\n" + formatterIndent(indent) + "else " + formatterExpr(
                                elseBranch,
                                0,
                                indent
                            )
                        )
                    | ExprLambda(name, body, annotation) ->
                        let parameter =
                            match annotation with
                                | None -> name
                                | Some(typeExpression) -> name + ": " + formatterType(typeExpression)
                        in
                            if formatterExpressionIsMultiline(body)
                            then
                                formatterWrap(
                                    parent,
                                    1,
                                    "given (" + parameter + ") ->\n" + formatterIndent(indent + 4) + formatterExpr(
                                        body,
                                        0,
                                        indent + 4
                                    )
                                )
                            else
                                formatterWrap(
                                    parent,
                                    1,
                                    "given (" + parameter + ") -> " + formatterExpr(body)(0)(indent)
                                )
                    | ExprLet(name, value, body, parameters, annotation, requirements) ->
                        formatterLet(
                            parent,
                            indent,
                            "let ",
                            name,
                            value,
                            body,
                            parameters,
                            annotation,
                            requirements
                        )
                    | ExprLetRecursive(name, value, body, parameters, annotation, requirements) ->
                        formatterLet(
                            parent,
                            indent,
                            "let recursive ",
                            name,
                            value,
                            body,
                            parameters,
                            annotation,
                            requirements
                        )
                    | ExprLetResult(name, value, body) ->
                        formatterWrap(
                            parent,
                            1,
                            "let? " + name + " = " + formatterExpr(
                                value,
                                0,
                                indent
                            ) + "\nin " + formatterExpr(body)(0)(indent)
                        )
                    | ExprMatch(value, cases, _position) ->
                        formatterWrap(
                            parent,
                            1,
                            "match " + formatterExpr(value)(0)(indent) + " with\n" + formatterCases(cases)(indent + 4)
                        )
                    | ExprHandle(body, arms) ->
                        formatterWrap(
                            parent,
                            1,
                            "handle " + formatterExpr(body)(0)(indent) + " with\n" + formatterArms(arms)(indent + 4)
                        )
                    | ExprAt(_span, inner) -> formatterExpr(inner)(parent)(indent)
and formatterExpressionInline : Expr -> Str =
    given (expression) -> formatterExpr(expression)(0)(0)
and formatterParenthesizedCall : Expr -> Expr -> Expr -> CallArgumentListLayout -> Int -> Str =
    given (expression) ->
        given (function) ->
            given (argument) ->
                given (layout) ->
                    given (indent) ->
                        if layout == callArgumentsInline
                        then formatterExpr(function)(13)(indent) + "(" + formatterExpr(argument)(0)(indent) + ")"
                        else formatterMultilineCall(expression)(indent)
and formatterExprField : (Str, Expr) -> Str =
    given (field) ->
        match field with
            | (name, value) -> name + " = " + formatterExpressionInline(value)
and formatterMultilineCall : Expr -> Int -> Str =
    given (expression) ->
        given (indent) ->
            match formatterCollectMultilineCall(expression)([]) with
                | (function, arguments) ->
                    formatterExpr(function)(13)(indent) + "(\n" + formatterMultilineExpressions(
                        arguments,
                        indent + 4
                    ) + "\n" + formatterIndent(indent) + ")"
and formatterCollectMultilineCall : Expr -> List(Expr) -> (Expr, List(Expr)) =
    given (expression) ->
        given (laterArguments) ->
            match formatterUnspanExpr(expression) with
                | ExprCall(function, argument, _whitespace, layout) ->
                    match (layout == callArgumentsMultilineContinuation, layout == callArgumentsMultilineStart) with
                        | (true, _) -> formatterCollectMultilineCall(function)(argument :: laterArguments)
                        | (false, true) -> (function, argument :: laterArguments)
                        | _ -> (expression, laterArguments)
                | _ -> (expression, laterArguments)
and formatterMultilineList : List(Expr) -> Int -> Str =
    given (elements) ->
        given (indent) ->
            let renderedElements = formatterMultilineExpressions(elements)(indent + 4)
            in "[\n" + renderedElements + "\n" + formatterIndent(indent) + "]"
and formatterMultilineExpressions : List(Expr) -> Int -> Str =
    given (expressions) ->
        given (indent) ->
            match expressions with
                | [] -> ""
                | expression :: [] -> formatterIndent(indent) + formatterExpr(expression)(0)(indent)
                | expression :: tail ->
                    formatterIndent(indent) + formatterExpr(
                        expression,
                        0,
                        indent
                    ) + ",\n" + formatterMultilineExpressions(tail)(indent)
and formatterLet : Int -> Int -> Str -> Str -> Expr -> Expr -> List(Str) -> Maybe(TypeExpr) -> List(TraitConstraintSyntax) -> Str =
    given (parent) ->
        given (indent) ->
            given (prefix) ->
                given (name) ->
                    given (value) ->
                        given (body) ->
                            given (parameters) ->
                                given (annotation) ->
                                    given (requirements) ->
                                        let annotationText =
                                            match annotation with
                                                | None -> ""
                                                | Some(typeExpression) ->
                                                    " : " + formatterType(
                                                        typeExpression
                                                    ) + formatterRequirements(requirements)
                                        in
                                            match formatterSugarParameters(parameters)(value)("") with
                                                | (parameterText, formattedValue) ->
                                                    formatterWrap(
                                                        parent,
                                                        1,
                                                        prefix + name + parameterText + annotationText + " = " + formatterExpr(
                                                            formattedValue,
                                                            0,
                                                            indent
                                                        ) + "\nin " + formatterExpr(body)(0)(indent)
                                                    )
and formatterSugarParameters : List(Str) -> Expr -> Str -> (Str, Expr) =
    given (parameters) ->
        given (value) ->
            given (rendered) ->
                match parameters with
                    | [] -> (rendered, value)
                    | parameter :: tail ->
                        match formatterUnspanExpr(value) with
                            | ExprLambda(_name, lambdaBody, _annotation) ->
                                formatterSugarParameters(
                                    tail,
                                    lambdaBody,
                                    rendered + " " + parameter
                                )
                            | _ -> (rendered, value)
and formatterRequirements : List(TraitConstraintSyntax) -> Str =
    given (requirements) ->
        let sorted =
            sortBy(given (left) ->
                given (right) -> formatterRequirement(left) <= formatterRequirement(right))(requirements)
        in
            match requirements with
                | [] -> ""
                | _ -> " requires {" + formatterJoin(", ")(formatterRequirement)(sorted) + "}"
and formatterRequirement : TraitConstraintSyntax -> Str =
    given (requirement) ->
        match requirement with
            | TraitConstraintSyntax { traitName = name, typeArguments = arguments } ->
                name + "(" + formatterJoin(
                    ", ",
                    formatterType,
                    arguments
                ) + ")"
and formatterCases : List((Pattern, Expr, Maybe(Expr))) -> Int -> Str =
    given (cases) ->
        given (indent) ->
            match cases with
                | [] -> ""
                | (pattern, body, guard) :: tail ->
                    let guardText =
                        match guard with
                            | None -> ""
                            | Some(condition) -> " when " + formatterExpressionInline(condition)
                    in
                        formatterIndent(indent) + "| " + formatterPatternAt(
                            pattern,
                            0
                        ) + guardText + " -> " + formatterExpr(
                            body,
                            0,
                            indent
                        ) + formatterFollowingCases(tail)(indent)
and formatterFollowingCases : List((Pattern, Expr, Maybe(Expr))) -> Int -> Str =
    given (cases) ->
        given (indent) ->
            match cases with
                | [] -> ""
                | _ -> "\n" + formatterCases(cases)(indent)
and formatterArms : List((Maybe(Str), Str, List(Pattern), Expr)) -> Int -> Str =
    given (arms) ->
        given (indent) ->
            match arms with
                | [] -> ""
                | (moduleName, operationName, parameters, body) :: tail ->
                    let prefix =
                        match moduleName with
                            | None -> operationName
                            | Some(moduleValue) -> moduleValue + "." + operationName
                    in
                        formatterIndent(indent) + "| " + prefix + "(" + formatterJoin(
                            ", ",
                            given (item) -> formatterPatternAt(item)(0),
                            parameters
                        ) + ") -> " + formatterExpr(body)(0)(indent) + formatterFollowingArms(tail)(indent)
and formatterFollowingArms : List((Maybe(Str), Str, List(Pattern), Expr)) -> Int -> Str =
    given (arms) ->
        given (indent) ->
            match arms with
                | [] -> ""
                | _ -> "\n" + formatterArms(arms)(indent)
and formatterExpressionIsMultiline : Expr -> Bool =
    given (expression) ->
        match formatterUnspanExpr(expression) with
            | ExprLambda(_, _, _) -> true
            | ExprLet(_, _, _, _, _, _) -> true
            | ExprLetRecursive(_, _, _, _, _, _) -> true
            | ExprLetResult(_, _, _) -> true
            | ExprIf(_, _, _) -> true
            | ExprMatch(_, _, _) -> true
            | ExprHandle(_, _) -> true
            | ExprCall(_, _, _, layout) -> layout != callArgumentsInline
            | ExprList(_, isMultiline) -> isMultiline
            | _ -> false

let formatExpression expression = formatterExpr(expression)(0)(0) + "\n"

let formatTypeExpression typeExpression = formatterType(typeExpression)

let formatPattern pattern = formatterPatternAt(pattern)(0)

let formatterTypeParameter parameter =
    match parameter with
        | TypeParameter { name = name } -> name

let formatterTypeParameters parameters =
    match parameters with
        | [] -> ""
        | _ -> "(" + formatterJoin(", ")(formatterTypeParameter)(parameters) + ")"

let formatterDeriving traits =
    match traits with
        | [] -> ""
        | _ ->
            "    deriving {" + formatterJoin(", ")(given (name) -> name)(traits) + "}\n"

let formatterConstructor constructor =
    match constructor with
        | TypeConstructor { name = name, parameters = [], fieldNames = _fields } -> "    | " + name + "\n"
        | TypeConstructor { name = name, parameters = parameters, fieldNames = _fields } ->
            "    | " + name + "(" + formatterJoin(
                ", ",
                formatterType,
                parameters
            ) + ")\n"

let recursive formatterConstructors constructors =
    match constructors with
        | [] -> ""
        | constructor :: tail -> formatterConstructor(constructor) + formatterConstructors(tail)

let recursive formatterRecordFields names parameters =
    match (names, parameters) with
        | (name :: nameTail, parameter :: parameterTail) ->
            "    | " + name + ": " + formatterType(
                parameter
            ) + "\n" + formatterRecordFields(
                nameTail,
                parameterTail
            )
        | _ -> ""

let formatterTypeDeclaration declaration =
    match declaration with
        | TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = derivingTraits } ->
            let body =
                if isRecord
                then
                    match constructors with
                        | TypeConstructor { name = _constructorName, parameters = fieldTypes, fieldNames = fieldNames } :: _ ->
                            formatterRecordFields(
                                fieldNames,
                                fieldTypes
                            )
                        | [] -> ""
                else formatterConstructors(constructors)
            in "type " + name + formatterTypeParameters(parameters) + " =\n" + body + formatterDeriving(derivingTraits)

let formatterTypeAliasDeclaration declaration =
    match declaration with
        | TypeAliasDecl { name = name, typeParameters = parameters, target = target } ->
            "type alias " + name + formatterTypeParameters(
                parameters
            ) + " = " + formatterType(target) + "\n"

let formatterZeroCostTypeDeclaration declaration =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = TypeConstructor { name = constructorName, parameters = constructorType :: _tail, fieldNames = _fields }, derivingTraits = derivingTraits } ->
            "type " + name + formatterTypeParameters(
                parameters
            ) + " = " + constructorName + "(" + formatterType(
                constructorType
            ) + ")\n" + formatterDeriving(derivingTraits)
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = TypeConstructor { name = constructorName, parameters = [], fieldNames = _fields }, derivingTraits = derivingTraits } ->
            "type " + name + formatterTypeParameters(
                parameters
            ) + " = " + constructorName + "()\n" + formatterDeriving(
                derivingTraits
            )

let formatterExportConstructors constructors =
    match constructors with
        | ExportConstructorsHidden -> ""
        | ExportConstructorsAll -> "(..)"
        | ExportConstructorsSelected(names) ->
            "(" + formatterJoin(", ")(given (name) -> name)(names) + ")"

let formatterExportItem item =
    match item with
        | ExportValue(name) -> "    value " + name + ",\n"
        | ExportType(name, constructors) -> "    type " + name + formatterExportConstructors(constructors) + ",\n"
        | ExportModule(name) -> "    module " + name + ",\n"

let recursive formatterExportItems items =
    match items with
        | [] -> ""
        | item :: tail -> formatterExportItem(item) + formatterExportItems(tail)

let formatterExportDeclaration declaration =
    match declaration with
        | ExportDecl { items = items } -> "export (\n" + formatterExportItems(items) + ")\n"

let recursive formatterParsedType parsedType =
    match parsedType with
        | ParsedNamed(name) -> name
        | ParsedPointer(inner) -> "*" + formatterParsedType(inner)
        | ParsedBuffer(inner) -> "FfiBuffer(" + formatterParsedType(inner) + ")"
        | ParsedOut(inner) -> "out " + formatterParsedType(inner)
        | ParsedNativeString(nullable, ownership, destructor) ->
            let nullableText =
                if nullable
                then "nullable "
                else ""
            in
                let ownershipText =
                    match (ownership, destructor) with
                        | (FfiStringBorrowed, _) -> "borrowed"
                        | (FfiStringOwned, Some(name)) -> "owned " + name
                        | (FfiStringOwned, None) -> "owned"
                in "FfiStr(" + nullableText + ownershipText + ")"

let formatterExternalOwnership ownership =
    match ownership with
        | ExternalOwnershipUnspecified -> ""
        | ExternalOwnershipBorrow -> "borrow "
        | ExternalOwnershipConsume -> "consume "

let recursive formatterExternalParameters parameterTypes ownerships =
    match parameterTypes with
        | [] -> ""
        | parameter :: tail ->
            let ownership =
                match ownerships with
                    | head :: _ -> head
                    | [] -> ExternalOwnershipUnspecified
            in
                let remainingOwnerships =
                    match ownerships with
                        | _head :: rest -> rest
                        | [] -> []
                in
                    let suffix =
                        match tail with
                            | [] -> ""
                            | _ -> ", " + formatterExternalParameters(tail)(remainingOwnerships)
                    in formatterExternalOwnership(ownership) + formatterParsedType(parameter) + suffix

let formatterCapabilityRef capabilityReference =
    match capabilityReference with
        | CapabilityRefSyntax { name = name, args = [] } -> name
        | CapabilityRefSyntax { name = name, args = arguments } ->
            name + "(" + formatterJoin(
                ", ",
                formatterType,
                arguments
            ) + ")"

let formatterNeedsRow row =
    match row with
        | NeedsRowSyntax { capabilities = [], tailVariable = Some(tail) } -> " needs " + tail
        | NeedsRowSyntax { capabilities = capabilities, tailVariable = tailVariable } ->
            let capabilitiesText = formatterJoin(", ")(formatterCapabilityRef)(capabilities)
            in
                match tailVariable with
                    | None -> " needs {" + capabilitiesText + "}"
                    | Some(tail) -> " needs {" + capabilitiesText + " | " + tail + "}"

let formatterExternalDeclaration declaration =
    match declaration with
        | ExternalOpaqueType(name, None) -> "external type " + name + "\n"
        | ExternalOpaqueType(name, Some(destructor)) -> "external type " + name + " resource destructor " + destructor + "\n"
        | ExternalFunction(name, parameterTypes, returnType, symbol, ownerships, needsRow) ->
            let needsText =
                match needsRow with
                    | None -> ""
                    | Some(row) -> formatterNeedsRow(row)
            in
                let symbolText =
                    match symbol with
                        | None -> ""
                        | Some(value) -> " = \"" + formatterEscape(value) + "\""
                in
                    "external " + name + "(" + formatterExternalParameters(
                        parameterTypes,
                        ownerships
                    ) + ") -> " + formatterParsedType(returnType) + needsText + symbolText + "\n"

let formatterCapabilityOperation operation =
    match operation with
        | CapabilityOperation { name = name, signature = None } -> "    | " + name + "\n"
        | CapabilityOperation { name = name, signature = Some(signature) } ->
            "    | " + name + " : " + formatterType(
                signature
            ) + "\n"

let recursive formatterCapabilityOperations operations =
    match operations with
        | [] -> ""
        | operation :: tail -> formatterCapabilityOperation(operation) + formatterCapabilityOperations(tail)

let formatterCapabilityDeclaration declaration =
    match declaration with
        | CapabilityDecl { name = name, typeParameters = parameters, operations = operations } ->
            "capability " + name + formatterTypeParameters(
                parameters
            ) + " =\n" + formatterCapabilityOperations(
                operations
            )

let formatterIsMultiline expression = formatterExpressionIsMultiline(expression)

let formatterDeclarationValue expression indent =
    if formatterIsMultiline(expression)
    then "\n" + formatterIndent(indent) + formatterExpr(expression)(0)(indent) + "\n"
    else " " + formatterExpr(expression)(0)(0) + "\n"

let formatterProvideBinding binding =
    match binding with
        | ProvideBinding { operationName = name, implementation = implementation } ->
            "    | " + name + " =" + formatterDeclarationValue(
                implementation,
                8
            )

let recursive formatterProvideBindings bindings =
    match bindings with
        | [] -> ""
        | binding :: tail -> formatterProvideBinding(binding) + formatterProvideBindings(tail)

let formatterProvideDeclaration declaration =
    match declaration with
        | ProvideDecl { capabilityName = name, typeArguments = arguments, bindings = bindings } ->
            let argumentsText =
                match arguments with
                    | [] -> ""
                    | _ -> "(" + formatterJoin(", ")(formatterType)(arguments) + ")"
            in "provide " + name + argumentsText + " =\n" + formatterProvideBindings(bindings)

let formatterTraitMethod method =
    match method with
        | TraitMethodDecl { name = name, signature = signature, defaultImplementation = None } ->
            "    | " + name + " : " + formatterType(
                signature
            ) + "\n"
        | TraitMethodDecl { name = name, signature = signature, defaultImplementation = Some(implementation) } ->
            "    | " + name + " : " + formatterType(
                signature
            ) + " =" + formatterDeclarationValue(
                implementation,
                8
            )

let recursive formatterTraitMethods methods =
    match methods with
        | [] -> ""
        | method :: tail -> formatterTraitMethod(method) + formatterTraitMethods(tail)

let formatterTraitDeclaration declaration =
    match declaration with
        | TraitDecl { name = name, typeParameters = parameters, supertraits = supertraits, methods = methods } ->
            "trait " + name + formatterTypeParameters(
                parameters
            ) + formatterRequirements(
                supertraits
            ) + " =\n" + formatterTraitMethods(methods)

let formatterImplementationBinding binding =
    match binding with
        | TraitImplementationMethodBinding { methodName = name, implementation = implementation } ->
            "    | " + name + " =" + formatterDeclarationValue(
                implementation,
                8
            )

let recursive formatterImplementationBindings bindings =
    match bindings with
        | [] -> ""
        | binding :: tail -> formatterImplementationBinding(binding) + formatterImplementationBindings(tail)

let formatterImplementationDeclaration declaration =
    match declaration with
        | TraitImplementationDecl { traitName = name, typeArguments = arguments, requirements = requirements, bindings = bindings } ->
            "implement " + name + "(" + formatterJoin(
                ", ",
                formatterType,
                arguments
            ) + ")" + formatterRequirements(requirements) + " =\n" + formatterImplementationBindings(bindings)

let formatterLetBinding prefix binding =
    match binding with
        | LetBindingSyntax { name = name, value = value, sugarParameters = parameters, typeAnnotation = annotation, requirements = requirements } ->
            let annotationText =
                match annotation with
                    | None -> ""
                    | Some(typeExpression) ->
                        " : " + formatterType(
                            typeExpression
                        ) + formatterRequirements(requirements)
            in
                match formatterSugarParameters(parameters)(value)("") with
                    | (parameterText, formattedValue) ->
                        prefix + name + parameterText + annotationText + " =" + formatterDeclarationValue(
                            formattedValue,
                            4
                        )

let recursive formatterRecursiveBindings bindings first =
    match bindings with
        | [] -> ""
        | binding :: tail ->
            let prefix =
                if first
                then "let recursive "
                else "and "
            in formatterLetBinding(prefix)(binding) + formatterRecursiveBindings(tail)(false)

let recursive formatterUnspanTopLevel item =
    match item with
        | TopLevelAt(_span, inner) -> formatterUnspanTopLevel(inner)
        | _ -> item

let formatterIsExternal item =
    match formatterUnspanTopLevel(item) with
        | TopLevelExternal(_) -> true
        | _ -> false

let recursive formatterTopLevelItem item =
    match formatterUnspanTopLevel(item) with
        | TopLevelExport(declaration) -> formatterExportDeclaration(declaration)
        | TopLevelType(declaration) -> formatterTypeDeclaration(declaration)
        | TopLevelTypeAlias(declaration) -> formatterTypeAliasDeclaration(declaration)
        | TopLevelZeroCostType(declaration) -> formatterZeroCostTypeDeclaration(declaration)
        | TopLevelExternal(declaration) -> formatterExternalDeclaration(declaration)
        | TopLevelCapability(declaration) -> formatterCapabilityDeclaration(declaration)
        | TopLevelProvide(declaration) -> formatterProvideDeclaration(declaration)
        | TopLevelTrait(declaration) -> formatterTraitDeclaration(declaration)
        | TopLevelImplementation(declaration) -> formatterImplementationDeclaration(declaration)
        | TopLevelLet(binding, isRecursive) ->
            if isRecursive
            then formatterLetBinding("let recursive ")(binding)
            else formatterLetBinding("let ")(binding)
        | TopLevelRecursiveGroup(bindings) -> formatterRecursiveBindings(bindings)(true)
        | TopLevelAt(_span, inner) -> formatterTopLevelItem(inner)

// Consecutive externals form one declaration block; every other top-level boundary gets a blank line.
let recursive formatterProgramItems items first previousExternal =
    match items with
        | [] -> ""
        | item :: tail ->
            let currentExternal = formatterIsExternal(item)
            in
                let separator =
                    if first
                    then ""
                    else
                        if previousExternal
                        then
                            if currentExternal
                            then ""
                            else "\n"
                        else "\n"
                in separator + formatterTopLevelItem(item) + formatterProgramItems(tail)(false)(currentExternal)

let formatProgram program =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let declarations = formatterProgramItems(items)(true)(false)
            in
                match (items, body) with
                    | ([], None) -> "\n"
                    | ([], Some(expression)) -> formatExpression(expression)
                    | (_, None) -> declarations
                    | (_, Some(expression)) -> declarations + "\n" + formatExpression(expression)
