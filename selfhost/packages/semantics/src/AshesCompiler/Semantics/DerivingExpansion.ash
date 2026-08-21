// Expands deriving clauses into ordinary implementation declarations before inference.
//
// Invariants:
// - Generated implementations retain declaration order and use ordinary trait calls.
// - Only payload-contributing type parameters become implementation requirements.
// - Function fields, unbound variables, and non-regular self recursion are rejected before coherence.

import AshesCompiler.Frontend.Syntax
import Ashes.Collection.List.append as appendList
import Ashes.Collection.List.reverse
import Ashes.Text
import Ashes.Byte
import Ashes.Number.UInt
export (
    type DerivingExpansionError(..),
    value expandDerivedImplementations,
)

type DerivingExpansionError =
    | UnsupportedDerivedTrait(Str, Str)
    | DuplicateDerivedTrait(Str, Str)
    | UnsupportedDerivedField(Str, Str)
    deriving {Eq, Show}

type DerivingExpansionResult =
    | items: List(TopLevelItem)
    | error: Maybe(DerivingExpansionError)

type DerivedPattern =
    | pattern: Pattern
    | fields: List(Str)

let recursive lastDerivedNamePart parts =
    match parts with
        | [] -> ""
        | name :: [] -> name
        | _head :: tail -> lastDerivedNamePart(tail)

let derivedTraitLeafName name = lastDerivedNamePart(Ashes.Text.split(name)("."))

let sameText left right = Ashes.Byte.compare(Ashes.Byte.fromText(left))(Ashes.Byte.fromText(right)) == 0

let derivedTraitIsSupported (name: Str) =
    if sameText(name)("Eq")
    then true
    else
        if sameText(name)("Ord")
        then true
        else
            if sameText(name)("Show")
            then true
            else sameText(name)("Hash")

let recursive textExists (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | head :: tail ->
            if sameText(name)(head)
            then true
            else textExists(name)(tail)

let recursive parameterNames parameters =
    match parameters with
        | [] -> []
        | TypeParameter { name = name } :: tail -> name :: parameterNames(tail)

let recursive exactTypeParameterArguments (expected: List(Str)) (arguments: List(TypeExpr)) =
    match (expected, arguments) with
        | ([], []) -> true
        | (name :: nameTail, TypeNamed(argumentName) :: argumentTail) ->
            if sameText(name)(argumentName)
            then exactTypeParameterArguments(nameTail)(argumentTail)
            else false
        | _ -> false

let recursive derivedTypeIsSupported (declarationName: Str) (declaredParameters: List(Str)) (typeExpression: TypeExpr) =
    match typeExpression with
        | TypeAt(_span, inner) -> derivedTypeIsSupported(declarationName)(declaredParameters)(inner)
        | TypeArrow(_argument, _result, _capabilities, _tail) -> false
        | TypeTuple(elements) -> derivedTypesAreSupported(declarationName)(declaredParameters)(elements)
        | TypeApplied(name, arguments) ->
            if sameText(name)(declarationName)
            then exactTypeParameterArguments(declaredParameters)(arguments)
            else
                if sameText(name)("Ptr")
                then false
                else
                    if sameText(name)("Task")
                    then false
                    else derivedTypesAreSupported(declarationName)(declaredParameters)(arguments)
        | TypeNamed(name) ->
            if sameText(name)(declarationName)
            then
                match declaredParameters with
                    | [] -> true
                    | _ -> false
            else
                let bytes = Ashes.Byte.fromText(name)
                in
                    if Ashes.Byte.length(bytes) <= 0
                    then false
                    else
                        let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0))
                        in
                            if first >= 97
                            then
                                if first <= 122
                                then textExists(name)(declaredParameters)
                                else true
                            else true
        | TypeUnit -> true
and derivedTypesAreSupported declarationName declaredParameters types =
    match types with
        | [] -> true
        | head :: tail ->
            if derivedTypeIsSupported(declarationName)(declaredParameters)(head)
            then derivedTypesAreSupported(declarationName)(declaredParameters)(tail)
            else false

let recursive derivedConstructorsAreSupported declarationName declaredParameters constructors =
    match constructors with
        | [] -> true
        | TypeConstructor { name = _name, parameters = fields, fieldNames = _fieldNames } :: tail ->
            if derivedTypesAreSupported(declarationName)(declaredParameters)(fields)
            then derivedConstructorsAreSupported(declarationName)(declaredParameters)(tail)
            else false

let recursive typeMentionsParameter (parameter: Str) (typeExpression: TypeExpr) =
    match typeExpression with
        | TypeAt(_span, inner) -> typeMentionsParameter(parameter)(inner)
        | TypeNamed(name) -> sameText(name)(parameter)
        | TypeApplied(_name, arguments) -> typesMentionParameter(parameter)(arguments)
        | TypeArrow(argument, result, capabilities, tail) ->
            if typeMentionsParameter(parameter)(argument)
            then true
            else
                if typeMentionsParameter(parameter)(result)
                then true
                else
                    if capabilityTypesMentionParameter(parameter)(capabilities)
                    then true
                    else
                        match tail with
                            | Some(name) -> sameText(name)(parameter)
                            | None -> false
        | TypeTuple(elements) -> typesMentionParameter(parameter)(elements)
        | TypeUnit -> false
and typesMentionParameter parameter types =
    match types with
        | [] -> false
        | head :: tail ->
            if typeMentionsParameter(parameter)(head)
            then true
            else typesMentionParameter(parameter)(tail)
and capabilityTypesMentionParameter parameter capabilities =
    match capabilities with
        | [] -> false
        | (_name, arguments) :: tail ->
            if typesMentionParameter(parameter)(arguments)
            then true
            else capabilityTypesMentionParameter(parameter)(tail)

let recursive constructorFieldsMentionParameter parameter constructors =
    match constructors with
        | [] -> false
        | TypeConstructor { name = _name, parameters = fields, fieldNames = _fieldNames } :: tail ->
            if typesMentionParameter(parameter)(fields)
            then true
            else constructorFieldsMentionParameter(parameter)(tail)

let recursive derivedRequirements traitName parameters constructors =
    match parameters with
        | [] -> []
        | TypeParameter { name = name } :: tail ->
            let rest = derivedRequirements(traitName)(tail)(constructors)
            in
                if constructorFieldsMentionParameter(name)(constructors)
                then TraitConstraintSyntax(traitName = traitName, typeArguments = [TypeNamed(name)]) :: rest
                else rest

let recursive generatedFieldNames prefix count index =
    if index >= count
    then []
    else prefix + "_" + Ashes.Text.fromInt(index) :: generatedFieldNames(prefix)(count)(index + 1)

let recursive wildcardPatterns count =
    if count <= 0
    then []
    else PatternWildcard :: wildcardPatterns(count - 1)

let recursive variablePatterns names =
    match names with
        | [] -> []
        | name :: tail -> PatternVar(name) :: variablePatterns(tail)

let recursive typeCount values =
    match values with
        | [] -> 0
        | _head :: tail -> 1 + typeCount(tail)

let derivedConstructorPattern constructor prefix bindFields =
    match constructor with
        | TypeConstructor { name = name, parameters = parameters, fieldNames = _fieldNames } ->
            if bindFields
            then
                let fields = generatedFieldNames(prefix)(typeCount(parameters))(0)
                in DerivedPattern(pattern = PatternConstructor(name)(variablePatterns(fields)), fields = fields)
            else DerivedPattern(pattern = PatternConstructor(name)(wildcardPatterns(typeCount(parameters))), fields = [])

let recursive applyDerivedTraitCall expression arguments =
    match arguments with
        | [] -> expression
        | head :: tail -> applyDerivedTraitCall(ExprCall(expression)(head)(false))(tail)

let derivedTraitCall traitName methodName arguments = applyDerivedTraitCall(ExprQualifiedVar(traitName)(methodName))(arguments)

let recursive buildDerivedEquality fieldsLeft fieldsRight result =
    match (fieldsLeft, fieldsRight) with
        | ([], []) -> result
        | (left :: leftTail, right :: rightTail) -> ExprIf(derivedTraitCall("Eq")("equal")([ExprVar(left), ExprVar(right)]))(buildDerivedEquality(leftTail)(rightTail)(result))(ExprBool(false))
        | _ -> ExprBool(false)

let recursive derivedEqCases constructors index =
    match constructors with
        | [] -> [(PatternWildcard, ExprBool(false), None)]
        | constructor :: tail ->
            match derivedConstructorPattern(constructor)("__derived_left_" + Ashes.Text.fromInt(index))(true) with
                | DerivedPattern { pattern = leftPattern, fields = leftFields } ->
                    match derivedConstructorPattern(constructor)("__derived_right_" + Ashes.Text.fromInt(index))(true) with
                        | DerivedPattern { pattern = rightPattern, fields = rightFields } -> (PatternTuple([leftPattern, rightPattern]), buildDerivedEquality(leftFields)(rightFields)(ExprBool(true)), None) :: derivedEqCases(tail)(index + 1)

let derivedEqBody constructors = ExprLambda("__derived_left")(ExprLambda("__derived_right")(ExprMatch(ExprTuple([ExprVar("__derived_left"), ExprVar("__derived_right")]))(derivedEqCases(constructors)(0))(None))(None))(None)

let recursive buildDerivedOrdering fieldsLeft fieldsRight fieldIndex result =
    match (fieldsLeft, fieldsRight) with
        | ([], []) -> result
        | (left :: leftTail, right :: rightTail) ->
            let orderingName = "__derived_ordering_" + Ashes.Text.fromInt(fieldIndex)
            in ExprMatch(derivedTraitCall("Ord")("compare")([ExprVar(left), ExprVar(right)]))([(PatternConstructor("Equal")([]), buildDerivedOrdering(leftTail)(rightTail)(fieldIndex + 1)(result), None), (PatternVar(orderingName), ExprVar(orderingName), None)])(None)
        | _ -> result

let derivedOrdPairCase leftConstructor leftIndex rightConstructor rightIndex same differentResult =
    match derivedConstructorPattern(leftConstructor)("__derived_left_" + Ashes.Text.fromInt(leftIndex))(same) with
        | DerivedPattern { pattern = leftPattern, fields = leftFields } ->
            match derivedConstructorPattern(rightConstructor)("__derived_right_" + Ashes.Text.fromInt(rightIndex))(same) with
                | DerivedPattern { pattern = rightPattern, fields = rightFields } ->
                    let result =
                        if same
                        then buildDerivedOrdering(leftFields)(rightFields)(0)(ExprVar("Equal"))
                        else differentResult
                    in (PatternTuple([leftPattern, rightPattern]), result, None)

let recursive derivedOrdDifferentCases leftConstructor leftIndex rightConstructors rightIndex result =
    match rightConstructors with
        | [] -> []
        | rightConstructor :: tail -> derivedOrdPairCase(leftConstructor)(leftIndex)(rightConstructor)(rightIndex)(false)(result) :: derivedOrdDifferentCases(leftConstructor)(leftIndex)(tail)(rightIndex + 1)(result)

let recursive derivedOrdCases constructors beforeReversed leftIndex =
    match constructors with
        | [] -> []
        | leftConstructor :: tail ->
            let earlier = derivedOrdDifferentCases(leftConstructor)(leftIndex)(reverse(beforeReversed))(0)(ExprVar("Greater"))
            in
                let same = [derivedOrdPairCase(leftConstructor)(leftIndex)(leftConstructor)(leftIndex)(true)(ExprVar("Equal"))]
                in
                    let later = derivedOrdDifferentCases(leftConstructor)(leftIndex)(tail)(leftIndex + 1)(ExprVar("Less"))
                    in appendList(appendList(earlier)(same))(appendList(later)(derivedOrdCases(tail)(leftConstructor :: beforeReversed)(leftIndex + 1)))

let derivedOrdBody constructors = ExprLambda("__derived_left")(ExprLambda("__derived_right")(ExprMatch(ExprTuple([ExprVar("__derived_left"), ExprVar("__derived_right")]))(derivedOrdCases(constructors)([])(0))(None))(None))(None)

let recursive appendDerivedShowFields fields fieldNames isRecord first result =
    match fields with
        | [] -> ExprAdd(result)(ExprString(")"))
        | field :: tail ->
            let separator =
                if first
                then ""
                else ", "
            in
                let prefix =
                    if isRecord
                    then
                        match fieldNames with
                            | name :: _ -> separator + name + " = "
                            | [] -> separator
                    else separator
                in
                    let remainingNames =
                        match fieldNames with
                            | _head :: rest -> rest
                            | [] -> []
                    in appendDerivedShowFields(tail)(remainingNames)(isRecord)(false)(ExprAdd(ExprAdd(result)(ExprString(prefix)))(derivedTraitCall("Show")("show")([ExprVar(field)])))

let derivedShowCase constructor isRecord index =
    match constructor with
        | TypeConstructor { name = name, parameters = parameters, fieldNames = fieldNames } ->
            match derivedConstructorPattern(constructor)("__derived_field_" + Ashes.Text.fromInt(index))(true) with
                | DerivedPattern { pattern = pattern, fields = fields } ->
                    let result =
                        match parameters with
                            | [] -> ExprString(name)
                            | _ -> appendDerivedShowFields(fields)(fieldNames)(isRecord)(true)(ExprString(name + "("))
                    in (pattern, result, None)

let recursive derivedShowCases constructors isRecord index =
    match constructors with
        | [] -> []
        | constructor :: tail -> derivedShowCase(constructor)(isRecord)(index) :: derivedShowCases(tail)(isRecord)(index + 1)

let derivedShowBody constructors isRecord = ExprLambda("__derived_value")(ExprMatch(ExprVar("__derived_value"))(derivedShowCases(constructors)(isRecord)(0))(None))(None)

let recursive foldDerivedHash fields result =
    match fields with
        | [] -> result
        | field :: tail -> foldDerivedHash(tail)(ExprAdd(ExprMultiply(result)(ExprInt(16777619)))(derivedTraitCall("Hash")("hash")([ExprVar(field)])))

let derivedHashCase constructor index =
    match derivedConstructorPattern(constructor)("__derived_field_" + Ashes.Text.fromInt(index))(true) with
        | DerivedPattern { pattern = pattern, fields = fields } -> (pattern, foldDerivedHash(fields)(ExprInt(index + 1)), None)

let recursive derivedHashCases constructors index =
    match constructors with
        | [] -> []
        | constructor :: tail -> derivedHashCase(constructor)(index) :: derivedHashCases(tail)(index + 1)

let derivedHashBody constructors = ExprLambda("__derived_value")(ExprMatch(ExprVar("__derived_value"))(derivedHashCases(constructors)(0))(None))(None)

let derivedImplementationHead name parameters =
    match parameters with
        | [] -> TypeNamed(name)
        | _ ->
            let recursive arguments values =
                match values with
                    | [] -> []
                    | TypeParameter { name = parameterName } :: tail -> TypeNamed(parameterName) :: arguments(tail)
            in TypeApplied(name)(arguments(parameters))

let derivedMethodName traitName =
    match traitName with
        | "Eq" -> "equal"
        | "Ord" -> "compare"
        | "Show" -> "show"
        | "Hash" -> "hash"
        | _ -> ""

let derivedMethodBody traitName constructors isRecord =
    match traitName with
        | "Eq" -> derivedEqBody(constructors)
        | "Ord" -> derivedOrdBody(constructors)
        | "Show" -> derivedShowBody(constructors)(isRecord)
        | "Hash" -> derivedHashBody(constructors)
        | _ -> ExprBool(false)

let createDerivedImplementation declaration traitName =
    match declaration with
        | TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = _derivingTraits } ->
            let binding = TraitImplementationMethodBinding(methodName = derivedMethodName(traitName), implementation = derivedMethodBody(traitName)(constructors)(isRecord))
            in TraitImplementationDecl(traitName = traitName, typeArguments = [derivedImplementationHead(name)(parameters)], requirements = derivedRequirements(traitName)(parameters)(constructors), bindings = [binding])

let recursive deriveTypeImplementations declaration remaining seen reversed =
    match remaining with
        | [] -> DerivingExpansionResult(items = reverse(reversed), error = None)
        | writtenName :: tail ->
            let traitName = derivedTraitLeafName(writtenName)
            in
                match declaration with
                    | TypeDecl { name = declarationName, typeParameters = parameters, constructors = constructors, isRecord = _isRecord, derivingTraits = _derivingTraits } ->
                        if !derivedTraitIsSupported(traitName)
                        then DerivingExpansionResult(items = [], error = Some(UnsupportedDerivedTrait(declarationName)(writtenName)))
                        else
                            if textExists(traitName)(seen)
                            then DerivingExpansionResult(items = [], error = Some(DuplicateDerivedTrait(declarationName)(traitName)))
                            else
                                if derivedConstructorsAreSupported(declarationName)(parameterNames(parameters))(constructors)
                                then deriveTypeImplementations(declaration)(tail)(traitName :: seen)(TopLevelImplementation(createDerivedImplementation(declaration)(traitName)) :: reversed)
                                else DerivingExpansionResult(items = [], error = Some(UnsupportedDerivedField(declarationName)(traitName)))

let ordinaryTypeWithoutDeriving declaration =
    match declaration with
        | TypeDecl { name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = _derivingTraits } -> TypeDecl(name = name, typeParameters = parameters, constructors = constructors, isRecord = isRecord, derivingTraits = [])

let zeroCostTypeWithoutDeriving declaration =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = constructor, derivingTraits = _derivingTraits } -> ZeroCostTypeDecl(name = name, typeParameters = parameters, constructor = constructor, derivingTraits = [])

let zeroCostAsOrdinary declaration =
    match declaration with
        | ZeroCostTypeDecl { name = name, typeParameters = parameters, constructor = constructor, derivingTraits = derivingTraits } -> TypeDecl(name = name, typeParameters = parameters, constructors = [constructor], isRecord = false, derivingTraits = derivingTraits)

let expandTypeItem declaration =
    match declaration with
        | TypeDecl { derivingTraits = [] } -> DerivingExpansionResult(items = [TopLevelType(declaration)], error = None)
        | TypeDecl { derivingTraits = derivingTraits } ->
            match deriveTypeImplementations(declaration)(derivingTraits)([])([]) with
                | DerivingExpansionResult { items = implementations, error = None } -> DerivingExpansionResult(items = TopLevelType(ordinaryTypeWithoutDeriving(declaration)) :: implementations, error = None)
                | failure -> failure

let expandZeroCostTypeItem declaration =
    match declaration with
        | ZeroCostTypeDecl { derivingTraits = [] } -> DerivingExpansionResult(items = [TopLevelZeroCostType(declaration)], error = None)
        | ZeroCostTypeDecl { derivingTraits = derivingTraits } ->
            match deriveTypeImplementations(zeroCostAsOrdinary(declaration))(derivingTraits)([])([]) with
                | DerivingExpansionResult { items = implementations, error = None } -> DerivingExpansionResult(items = TopLevelZeroCostType(zeroCostTypeWithoutDeriving(declaration)) :: implementations, error = None)
                | failure -> failure

let spanExpandedItems span items =
    (let recursive wrap values =
        match values with
            | [] -> []
            | head :: tail -> TopLevelAt(span)(head) :: wrap(tail)
    in wrap(items))

let recursive expandTopLevelItem item =
    match item with
        | TopLevelAt(span, inner) ->
            match expandTopLevelItem(inner) with
                | DerivingExpansionResult { items = items, error = None } -> DerivingExpansionResult(items = spanExpandedItems(span)(items), error = None)
                | failure -> failure
        | TopLevelType(declaration) -> expandTypeItem(declaration)
        | TopLevelZeroCostType(declaration) -> expandZeroCostTypeItem(declaration)
        | _ -> DerivingExpansionResult(items = [item], error = None)

let recursive expandTopLevelItems remaining reversed =
    match remaining with
        | [] -> DerivingExpansionResult(items = reverse(reversed), error = None)
        | head :: tail ->
            match expandTopLevelItem(head) with
                | DerivingExpansionResult { items = items, error = None } -> expandTopLevelItems(tail)(appendList(reverse(items))(reversed))
                | failure -> failure

let expandDerivedImplementations program =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            match expandTopLevelItems(items)([]) with
                | DerivingExpansionResult { items = expanded, error = None } -> Ok(ProgramSyntax(items = expanded, body = body))
                | DerivingExpansionResult { items = _items, error = Some(error) } -> Error(error)
