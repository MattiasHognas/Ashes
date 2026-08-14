import AshesCompiler.Frontend.Syntax
export (
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

let recursive formatterBinary : Int -> Int -> Str -> Expr -> Expr -> Str =
    given (parent) ->
        given (precedence) ->
            given (operator) ->
                given (left) ->
                    given (right) -> formatterWrap(parent)(precedence)(formatterExpr(left)(precedence)(0) + " " + operator + " " + formatterExpr(right)(precedence + 1)(0))
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
                    | ExprCons(head, tail) -> formatterWrap(parent)(8)(formatterExpr(head)(9)(indent) + " :: " + formatterExpr(tail)(8)(indent))
                    | ExprBitwiseNot(operand) -> formatterWrap(parent)(12)("~" + formatterExpr(operand)(12)(indent))
                    | ExprLogicalNot(operand) -> formatterWrap(parent)(12)("!" + formatterExpr(operand)(12)(indent))
                    | ExprCall(function, argument, whitespace) ->
                        let rendered =
                            if whitespace
                            then formatterExpr(function)(13)(indent) + " " + formatterExpr(argument)(14)(indent)
                            else formatterExpr(function)(13)(indent) + "(" + formatterExpr(argument)(0)(indent) + ")"
                        in formatterWrap(parent)(13)(rendered)
                    | ExprTuple(elements) -> "(" + formatterJoin(", ")(formatterExpressionInline)(elements) + ")"
                    | ExprList(elements) -> "[" + formatterJoin(", ")(formatterExpressionInline)(elements) + "]"
                    | ExprRecord(name, fields) -> name + "(" + formatterJoin(", ")(formatterExprField)(fields) + ")"
                    | ExprRecordUpdate(target, fields) -> formatterWrap(parent)(2)(formatterExpr(target)(3)(indent) + " with " + formatterJoin(", ")(formatterExprField)(fields))
                    | ExprAwait(task) -> formatterWrap(parent)(12)("await " + formatterExpr(task)(12)(indent))
                    | ExprPerform(operation) -> formatterWrap(parent)(12)("perform " + formatterExpr(operation)(12)(indent))
                    | ExprIf(condition, thenBranch, elseBranch) -> formatterWrap(parent)(1)("if " + formatterExpr(condition)(0)(indent) + "\n" + formatterIndent(indent) + "then " + formatterExpr(thenBranch)(0)(indent) + "\n" + formatterIndent(indent) + "else " + formatterExpr(elseBranch)(0)(indent))
                    | ExprLambda(name, body, annotation) ->
                        let parameter =
                            match annotation with
                                | None -> name
                                | Some(typeExpression) -> name + ": " + formatterType(typeExpression)
                        in formatterWrap(parent)(1)("given (" + parameter + ") -> " + formatterExpr(body)(0)(indent))
                    | ExprLet(name, value, body, parameters, annotation, requirements) -> formatterLet(parent)(indent)("let ")(name)(value)(body)(parameters)(annotation)(requirements)
                    | ExprLetRecursive(name, value, body, parameters, annotation, requirements) -> formatterLet(parent)(indent)("let recursive ")(name)(value)(body)(parameters)(annotation)(requirements)
                    | ExprLetResult(name, value, body) -> formatterWrap(parent)(1)("let? " + name + " = " + formatterExpr(value)(0)(indent) + "\nin " + formatterExpr(body)(0)(indent))
                    | ExprMatch(value, cases, _position) -> formatterWrap(parent)(1)("match " + formatterExpr(value)(0)(indent) + " with\n" + formatterCases(cases)(indent + 4))
                    | ExprHandle(body, arms) -> formatterWrap(parent)(1)("handle " + formatterExpr(body)(0)(indent) + " with\n" + formatterArms(arms)(indent + 4))
                    | ExprAt(_span, inner) -> formatterExpr(inner)(parent)(indent)
and formatterExpressionInline : Expr -> Str =
    given (expression) -> formatterExpr(expression)(0)(0)
and formatterExprField : (Str, Expr) -> Str =
    given (field) ->
        match field with
            | (name, value) -> name + " = " + formatterExpressionInline(value)
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
                                                | Some(typeExpression) -> " : " + formatterType(typeExpression) + formatterRequirements(requirements)
                                        in
                                            match formatterSugarParameters(parameters)(value)("") with
                                                | (parameterText, formattedValue) -> formatterWrap(parent)(1)(prefix + name + parameterText + annotationText + " = " + formatterExpr(formattedValue)(0)(indent) + "\nin " + formatterExpr(body)(0)(indent))
and formatterSugarParameters : List(Str) -> Expr -> Str -> (Str, Expr) =
    given (parameters) ->
        given (value) ->
            given (rendered) ->
                match parameters with
                    | [] -> (rendered, value)
                    | parameter :: tail ->
                        match formatterUnspanExpr(value) with
                            | ExprLambda(_name, lambdaBody, _annotation) -> formatterSugarParameters(tail)(lambdaBody)(rendered + " " + parameter)
                            | _ -> (rendered, value)
and formatterRequirements : List(TraitConstraintSyntax) -> Str =
    given (requirements) ->
        let render : TraitConstraintSyntax -> Str =
            given (requirement) ->
                match requirement with
                    | TraitConstraintSyntax { traitName = name, typeArguments = arguments } -> name + "(" + formatterJoin(", ")(formatterType)(arguments) + ")"
        in
            match requirements with
                | [] -> ""
                | _ -> " requires {" + formatterJoin(", ")(render)(requirements) + "}"
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
                    in formatterIndent(indent) + "| " + formatterPatternAt(pattern)(0) + guardText + " -> " + formatterExpr(body)(0)(indent) + formatterFollowingCases(tail)(indent)
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
                        formatterIndent(indent) + "| " + prefix + "(" + formatterJoin(", ")(given (item) -> formatterPatternAt(item)(0))(parameters) + ") -> " + formatterExpr(body)(0)(indent) + formatterFollowingArms(tail)(indent)
and formatterFollowingArms : List((Maybe(Str), Str, List(Pattern), Expr)) -> Int -> Str =
    given (arms) ->
        given (indent) ->
            match arms with
                | [] -> ""
                | _ -> "\n" + formatterArms(arms)(indent)

let formatExpression expression = formatterExpr(expression)(0)(0) + "\n"

let formatTypeExpression typeExpression = formatterType(typeExpression)

let formatPattern pattern = formatterPatternAt(pattern)(0)
