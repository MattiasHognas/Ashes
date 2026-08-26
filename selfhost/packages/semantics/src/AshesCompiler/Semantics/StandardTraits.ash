// Seeds the semantic contract exported by the shipped Ashes.Trait module.
//
// Invariants:
// - Trait methods and implementation heads match lib/Ashes/Trait.ash.
// - Generic structural heads retain their recursive evidence requirements.
// - Implementation bodies use deterministic compiler-private references until module stitching
//   replaces them with the corresponding shipped source bindings.

import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
export (
    value standardTraitEnvironment,
    value standardTraitImplementationBindingName,
)

let orderingType = SemNamed(0)("Ordering")([])

let orderingConstructorScheme = TypeScheme(quantified = [], body = orderingType, constraints = [])

let maybeTypeSymbolId = 1

let resultTypeSymbolId = 2

let taskTypeSymbolId = 3

let binaryType argument result =
    SemFunction(argument)(SemFunction(argument)(result)(None))(None)

let unaryType argument result = SemFunction(argument)(result)(None)

let standardTraitMethodScheme traitName parameterId methodType =
    TypeScheme(quantified = [(parameterId, "a")], body = methodType, constraints = [TraitConstraint(traitName = traitName, typeArguments = [SemVariable(
        parameterId
    )])])

let notEqualDefault unit =
    ExprLambda("left")(ExprLambda("right")(callArgumentsInline
    |> ExprCall(
        ExprCall(ExprQualifiedVar("Eq")("equal"))(ExprVar("left"))(false)(callArgumentsInline),
        ExprVar("right"),
        false
    )
    |> ExprLogicalNot)(None))(None)

let orderingCase name = (PatternConstructor(name)([]), ExprBool(true), None)

let ordPredicateDefault accepted =
    (let recursive acceptedCases names =
        match names with
            | [] -> [(PatternWildcard, ExprBool(false), None)]
            | name :: tail -> orderingCase(name) :: acceptedCases(tail)
    in
        ExprLambda(
            "left",
            ExprLambda(
                "right",
                ExprMatch(
                    ExprCall(
                        ExprCall(ExprQualifiedVar("Ord")("compare"))(ExprVar("left"))(false)(callArgumentsInline),
                        ExprVar("right"),
                        false,
                        callArgumentsInline
                    ),
                    acceptedCases(accepted),
                    None
                ),
                None
            ),
            None
        ))

let standardTraitMethods traitName parameterId =
    (let parameter = SemVariable(parameterId)
    in
        let constrained methodName methodType defaultImplementation =
            TraitMethodInferenceDefinition(name = methodName, scheme = standardTraitMethodScheme(
                traitName,
                parameterId,
                methodType
            ), defaultImplementation = defaultImplementation)
        in
            match traitName with
                | "Eq" ->
                    [constrained(
                        "equal",
                        binaryType(parameter)(SemBool),
                        None
                    ), constrained("notEqual")(binaryType(parameter)(SemBool))(Unit
                    |> notEqualDefault
                    |> Some)]
                | "Ord" ->
                    [constrained(
                        "compare",
                        binaryType(parameter)(orderingType),
                        None
                    ), constrained("less")(binaryType(parameter)(SemBool))(["Less"]
                    |> ordPredicateDefault
                    |> Some), constrained("lessOrEqual")(binaryType(parameter)(SemBool))(["Less", "Equal"]
                    |> ordPredicateDefault
                    |> Some), constrained("greater")(binaryType(parameter)(SemBool))(["Greater"]
                    |> ordPredicateDefault
                    |> Some), constrained("greaterOrEqual")(binaryType(parameter)(SemBool))(["Greater", "Equal"]
                    |> ordPredicateDefault
                    |> Some)]
                | "Show" ->
                    [constrained("show")(unaryType(parameter)(SemString))(None)]
                | "Hash" ->
                    [constrained("hash")(unaryType(parameter)(SemInt))(None)]
                | "Default" ->
                    [constrained("default")(unaryType(SemTuple([]))(parameter))(None)]
                | "Add" ->
                    [constrained("add")(binaryType(parameter)(parameter))(None)]
                | "Subtract" ->
                    [constrained("subtract")(binaryType(parameter)(parameter))(None)]
                | "Multiply" ->
                    [constrained("multiply")(binaryType(parameter)(parameter))(None)]
                | "Divide" ->
                    [constrained("divide")(binaryType(parameter)(parameter))(None)]
                | "Remainder" ->
                    [constrained("remainder")(binaryType(parameter)(parameter))(None)]
                | "Negate" ->
                    [constrained("negate")(unaryType(parameter)(parameter))(None)]
                | "Not" ->
                    [constrained("not")(unaryType(parameter)(parameter))(None)]
                | "BitAnd" ->
                    [constrained("bitAnd")(binaryType(parameter)(parameter))(None)]
                | "BitOr" ->
                    [constrained("bitOr")(binaryType(parameter)(parameter))(None)]
                | "BitXor" ->
                    [constrained("bitXor")(binaryType(parameter)(parameter))(None)]
                | "ShiftLeft" ->
                    [constrained("shiftLeft")(binaryType(parameter)(parameter))(None)]
                | "ShiftRight" ->
                    [constrained("shiftRight")(binaryType(parameter)(parameter))(None)]
                | "BitwiseNot" ->
                    [constrained("bitwiseNot")(unaryType(parameter)(parameter))(None)]
                | _ -> [])

let recursive addStandardTraitMethodBindings traitName methods environment =
    match methods with
        | [] -> environment
        | TraitMethodInferenceDefinition { name = methodName, scheme = scheme, defaultImplementation = _defaultImplementation } :: tail ->
            environment
            |> addTypeBinding(traitName + "." + methodName)(scheme)
            |> addStandardTraitMethodBindings(traitName)(tail)

let addStandardTrait traitName parameterId supertraits environment =
    (let parameter = SemVariable(parameterId)
    in
        let methods = standardTraitMethods(traitName)(parameterId)
        in
            environment
            |> addStandardTraitMethodBindings(traitName)(methods)
            |> addTraitBinding(traitName)(1)([parameter])(methods)(supertraits))

let registerStandardTraits environment =
    environment
    |> addStandardTrait("Eq")(1000)([])
    |> addStandardTrait("Ord")(1001)([TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(1001)])])
    |> addStandardTrait("Show")(1002)([])
    |> addStandardTrait("Hash")(1003)([])
    |> addStandardTrait("Default")(1004)([])
    |> addStandardTrait("Add")(1005)([])
    |> addStandardTrait("Subtract")(1006)([])
    |> addStandardTrait("Multiply")(1007)([])
    |> addStandardTrait("Divide")(1008)([])
    |> addStandardTrait("Remainder")(1009)([])
    |> addStandardTrait("Negate")(1010)([])
    |> addStandardTrait("Not")(1011)([])
    |> addStandardTrait("BitAnd")(1012)([])
    |> addStandardTrait("BitOr")(1013)([])
    |> addStandardTrait("BitXor")(1014)([])
    |> addStandardTrait("ShiftLeft")(1015)([])
    |> addStandardTrait("ShiftRight")(1016)([])
    |> addStandardTrait("BitwiseNot")(1017)([])

let standardTraitImplementationMethodName traitName =
    match traitName with
        | "Eq" -> "equal"
        | "Ord" -> "compare"
        | "Show" -> "show"
        | "Hash" -> "hash"
        | "Default" -> "default"
        | "Add" -> "add"
        | "Subtract" -> "subtract"
        | "Multiply" -> "multiply"
        | "Divide" -> "divide"
        | "Remainder" -> "remainder"
        | "Negate" -> "negate"
        | "Not" -> "not"
        | "BitAnd" -> "bitAnd"
        | "BitOr" -> "bitOr"
        | "BitXor" -> "bitXor"
        | "ShiftLeft" -> "shiftLeft"
        | "ShiftRight" -> "shiftRight"
        | "BitwiseNot" -> "bitwiseNot"
        | _ -> ""

let recursive standardTypeKey semanticType =
    match semanticType with
        | SemInt -> "int"
        | SemUInt(bits) -> "u" + Ashes.Text.fromInt(bits)
        | SemFloat -> "float"
        | SemBigInt -> "bigint"
        | SemString -> "str"
        | SemRune -> "rune"
        | SemBool -> "bool"
        | SemList(element) -> "list_" + standardTypeKey(element)
        | SemTuple(first :: second :: []) -> "tuple2_" + standardTypeKey(first) + "_" + standardTypeKey(second)
        | SemNamed(_symbolId, name, arguments) ->
            let recursive argumentKeys values =
                match values with
                    | [] -> ""
                    | head :: tail -> "_" + standardTypeKey(head) + argumentKeys(tail)
            in name + argumentKeys(arguments)
        | SemParameter(parameterId, _name) -> "parameter" + Ashes.Text.fromInt(parameterId)
        | _ -> "type"

let standardTraitImplementationBindingName traitName methodName head =
    "__ashes_standard_trait_" + traitName + "_" + methodName + "_" + standardTypeKey(
        head
    )

let standardImplementationMethodType traitName head =
    match traitName with
        | "Eq" -> binaryType(head)(SemBool)
        | "Ord" -> binaryType(head)(orderingType)
        | "Show" -> unaryType(head)(SemString)
        | "Hash" -> unaryType(head)(SemInt)
        | "Default" -> unaryType(SemTuple([]))(head)
        | "Negate" -> unaryType(head)(head)
        | "Not" -> unaryType(head)(head)
        | "BitwiseNot" -> unaryType(head)(head)
        | _ -> binaryType(head)(head)

let addStandardImplementation traitName head requirements environment =
    (let methodName = standardTraitImplementationMethodName(traitName)
    in
        let methodType = standardImplementationMethodType(traitName)(head)
        in
            let implementation =
                head
                |> standardTraitImplementationBindingName(traitName)(methodName)
                |> ExprVar
            in
                addTraitImplementation(
                    traitName,
                    [head],
                    canonicalizeTraitConstraints(requirements),
                    [TraitImplementationMethodInferenceDefinition(name = methodName, implementation = implementation, semanticType = methodType)],
                    environment
                ))

let recursive addStandardImplementations traitName heads environment =
    match heads with
        | [] -> environment
        | head :: tail ->
            environment
            |> addStandardImplementation(traitName)(head)([])
            |> addStandardImplementations(traitName)(tail)

let equalityTypes =
    [SemInt, SemFloat, SemBigInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(
        64
    ), SemBool, SemString, SemRune]

let orderedTypes = [SemInt, SemFloat, SemBigInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64), SemString, SemRune]

let defaultTypes = [SemInt, SemFloat, SemBigInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64), SemBool, SemString]

let additiveTypes = [SemInt, SemFloat, SemBigInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64), SemString]

let arithmeticTypes = [SemInt, SemFloat, SemBigInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64)]

let integralTypes = [SemInt, SemBigInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64)]

let bitwiseTypes = [SemInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64)]

let registerPrimitiveImplementations environment =
    environment
    |> addStandardImplementations("Eq")(equalityTypes)
    |> addStandardImplementations("Ord")(orderedTypes)
    |> addStandardImplementations("Show")(equalityTypes)
    |> addStandardImplementations("Hash")(equalityTypes)
    |> addStandardImplementations("Default")(defaultTypes)
    |> addStandardImplementations("Not")([SemBool])
    |> addStandardImplementations("Add")(additiveTypes)
    |> addStandardImplementations("Subtract")(arithmeticTypes)
    |> addStandardImplementations("Multiply")(arithmeticTypes)
    |> addStandardImplementations("Divide")(arithmeticTypes)
    |> addStandardImplementations("Remainder")(integralTypes)
    |> addStandardImplementations("Negate")(arithmeticTypes)
    |> addStandardImplementations("BitAnd")(bitwiseTypes)
    |> addStandardImplementations("BitOr")(bitwiseTypes)
    |> addStandardImplementations("BitXor")(bitwiseTypes)
    |> addStandardImplementations("ShiftLeft")(bitwiseTypes)
    |> addStandardImplementations("ShiftRight")(bitwiseTypes)
    |> addStandardImplementations("BitwiseNot")(bitwiseTypes)

let structuralHead shape parameters =
    match (shape, parameters) with
        | ("list", element :: []) -> SemList(element)
        | ("maybe", element :: []) -> SemNamed(maybeTypeSymbolId)("Maybe")([element])
        | ("result", _errorType :: _successType :: []) -> SemNamed(resultTypeSymbolId)("Result")(parameters)
        | ("tuple2", _first :: _second :: []) -> SemTuple(parameters)
        | _ -> SemNever

let recursive structuralRequirements traitName parameters =
    match parameters with
        | [] -> []
        | head :: tail ->
            TraitConstraint(traitName = traitName, typeArguments = [head]) :: structuralRequirements(
                traitName,
                tail
            )

let addStructuralImplementation traitName shape parameters requiresEvidence environment =
    (let head = structuralHead(shape)(parameters)
    in
        let requirements =
            if requiresEvidence
            then structuralRequirements(traitName)(parameters)
            else []
        in addStandardImplementation(traitName)(head)(requirements)(environment))

let registerEvidenceStructuralTrait traitName environment =
    (let a = SemParameter(2000)("a")
    in
        let e = SemParameter(2001)("e")
        in
            environment
            |> addStructuralImplementation(traitName)("list")([a])(true)
            |> addStructuralImplementation(traitName)("maybe")([a])(true)
            |> addStructuralImplementation(traitName)("result")([e, a])(true)
            |> addStructuralImplementation(traitName)("tuple2")([a, e])(true))

let registerDefaultStructuralImplementations environment =
    (let a = SemParameter(2000)("a")
    in
        let b = SemParameter(2001)("b")
        in
            environment
            |> addStructuralImplementation("Default")("list")([a])(false)
            |> addStructuralImplementation("Default")("maybe")([a])(false)
            |> addStructuralImplementation("Default")("tuple2")([a, b])(true))

let registerStructuralImplementations environment =
    environment
    |> registerEvidenceStructuralTrait("Eq")
    |> registerEvidenceStructuralTrait("Ord")
    |> registerEvidenceStructuralTrait("Show")
    |> registerEvidenceStructuralTrait("Hash")
    |> registerDefaultStructuralImplementations

let standardTraitEnvironment unit =
    "ashes-standard-library"
    |> emptyTypeEnvironmentForPackage
    |> addInferenceTypeDefinition(0)("Ordering")(0)
    |> addConstructorBinding("Less")(orderingConstructorScheme)([])
    |> addConstructorBinding("Equal")(orderingConstructorScheme)([])
    |> addConstructorBinding("Greater")(orderingConstructorScheme)([])
    |> addConstructorBinding("Unordered")(orderingConstructorScheme)([])
    |> addInferenceTypeDefinition(maybeTypeSymbolId)("Maybe")(1)
    |> addInferenceTypeDefinition(resultTypeSymbolId)("Result")(2)
    |> addInferenceTypeDefinition(taskTypeSymbolId)("Task")(2)
    |> registerStandardTraits
    |> registerPrimitiveImplementations
    |> registerStructuralImplementations
