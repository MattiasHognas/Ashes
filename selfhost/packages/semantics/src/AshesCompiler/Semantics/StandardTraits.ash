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
import AshesCompiler.Semantics.CoreBuiltinLowering
export (
    value standardTraitEnvironment,
    value withIntrinsicBuiltinSignatures,
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
                    ), Unit
                    |> notEqualDefault
                    |> Some
                    |> constrained("notEqual")(binaryType(parameter)(SemBool))]
                | "Ord" ->
                    [constrained(
                        "compare",
                        binaryType(parameter)(orderingType),
                        None
                    ), ["Less"]
                    |> ordPredicateDefault
                    |> Some
                    |> constrained("less")(binaryType(parameter)(SemBool)), ["Less", "Equal"]
                    |> ordPredicateDefault
                    |> Some
                    |> constrained("lessOrEqual")(binaryType(parameter)(SemBool)), ["Greater"]
                    |> ordPredicateDefault
                    |> Some
                    |> constrained("greater")(binaryType(parameter)(SemBool)), ["Greater", "Equal"]
                    |> ordPredicateDefault
                    |> Some
                    |> constrained("greaterOrEqual")(binaryType(parameter)(SemBool))]
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

// The compiler-provided nominal types. The builtin layouts write every one of them with symbol id
// 0, which lowering tells apart by name; inference identifies a nominal type by its id alone, so
// each gets its own here and the builtin signatures are rewritten to it. `Maybe`, `Result` and
// `Task` keep the ids `standardTraitEnvironment` gives them.
let intrinsicTypeSymbolId name =
    match name with
        | "Maybe" -> 1
        | "Result" -> 2
        | "Task" -> 3
        | "Unit" -> 4
        | "FileHandle" -> 5
        | "Process" -> 6
        | "Socket" -> 7
        | "TlsSocket" -> 8
        | _ -> -1

let recursive withIntrinsicTypeIds semanticType =
    match semanticType with
        | SemList(element) ->
            element
            |> withIntrinsicTypeIds
            |> SemList
        | SemTuple(elements) ->
            elements
            |> withIntrinsicTypeIdsOf
            |> SemTuple
        | SemFunction(argument, result, row) ->
            SemFunction(withIntrinsicTypeIds(argument))(withIntrinsicTypeIds(result))(
                match row with
                    | Some(rowType) ->
                        rowType
                        |> withIntrinsicTypeIds
                        |> Some
                    | None -> None
            )
        | SemCapability(name, arguments) ->
            arguments
            |> withIntrinsicTypeIdsOf
            |> SemCapability(name)
        | SemRow(capabilities, tail) ->
            SemRow(withIntrinsicTypeIdsOf(capabilities))(
                match tail with
                    | Some(tailType) ->
                        tailType
                        |> withIntrinsicTypeIds
                        |> Some
                    | None -> None
            )
        | SemNamed(symbolId, name, arguments) ->
            let intrinsicId = intrinsicTypeSymbolId(name)
            in
                if intrinsicId < 0
                then
                    arguments
                    |> withIntrinsicTypeIdsOf
                    |> SemNamed(symbolId)(name)
                else
                    arguments
                    |> withIntrinsicTypeIdsOf
                    |> SemNamed(intrinsicId)(name)
        | SemPointer(pointee) ->
            pointee
            |> withIntrinsicTypeIds
            |> SemPointer
        | other -> other
and withIntrinsicTypeIdsOf semanticTypes =
    match semanticTypes with
        | [] -> []
        | head :: tail -> withIntrinsicTypeIds(head) :: withIntrinsicTypeIdsOf(tail)

let schemeWithIntrinsicTypeIds scheme =
    match scheme with
        | TypeScheme { quantified = quantified, body = body, constraints = constraints } -> TypeScheme(quantified = quantified, body = withIntrinsicTypeIds(body), constraints = constraints)

let intrinsicMaybeType = SemNamed(1)("Maybe")([SemVariable(1)])

let intrinsicResultType = SemNamed(2)("Result")([SemVariable(2), SemVariable(3)])

let intrinsicResultScheme payload = TypeScheme(quantified = [(2, "e"), (3, "a")], body = SemFunction(payload)(intrinsicResultType)(None), constraints = [])

let withIntrinsicTypes environment =
    environment
    |> addInferenceTypeDefinition(1)("Maybe")(1)
    |> addInferenceTypeDefinition(2)("Result")(2)
    |> addInferenceTypeDefinition(3)("Task")(2)
    |> addInferenceTypeDefinition(4)("Unit")(0)
    |> addInferenceTypeDefinition(5)("FileHandle")(0)
    |> addInferenceTypeDefinition(6)("Process")(0)
    |> addInferenceTypeDefinition(7)("Socket")(0)
    |> addInferenceTypeDefinition(8)("TlsSocket")(0)
    |> addConstructorBinding("Unit")(TypeScheme(quantified = [], body = SemNamed(4)("Unit")([]), constraints = []))([])
    |> addConstructorBinding("None")(TypeScheme(quantified = [(1, "a")], body = intrinsicMaybeType, constraints = []))([])
    |> addConstructorBinding("Some")(TypeScheme(quantified = [(1, "a")], body = SemFunction(SemVariable(1))(intrinsicMaybeType)(None), constraints = []))([])
    |> addConstructorBinding("Ok")(intrinsicResultScheme(SemVariable(3)))([])
    |> addConstructorBinding("Error")(intrinsicResultScheme(SemVariable(2)))([])

// The task builtins and `async`. Lowering has no layout for them yet, so they are not among
// `standardBuiltinLayouts`; their signatures are the language reference's (19, Async/Await).
let intrinsicTaskOf errorType valueType = SemNamed(3)("Task")([errorType, valueType])

let intrinsicJoinHandleType = SemNamed(9)("JoinHandle")([SemVariable(2), SemVariable(3)])

let taskScheme argument result = TypeScheme(quantified = [(2, "e"), (3, "a")], body = SemFunction(argument)(result)(None), constraints = [])

let polymorphicTask = intrinsicTaskOf(SemVariable(2))(SemVariable(3))

let asyncTaskScheme =
    TypeScheme(quantified = [(3, "a")], body = SemFunction(SemVariable(3))(intrinsicTaskOf(SemString)(SemVariable(3)))(None), constraints = [])

// The two members of `Ashes.Task.Parallel` the compiler provides take thunks: `both` runs two and
// pairs their results, `withWorkers` runs one under a worker count.
let parallelThunkType result =
    SemFunction(SemNamed(4)("Unit")([]))(result)(None)

let withTaskSignatures environment =
    environment
    |> addInferenceTypeDefinition(9)("JoinHandle")(2)
    |> addTypeBinding("Ashes.Task.run")(taskScheme(polymorphicTask)(intrinsicResultType))
    |> addTypeBinding("Ashes.Task.task")(asyncTaskScheme)
    |> addTypeBinding("async")(asyncTaskScheme)
    |> addTypeBinding("Ashes.Task.fromResult")(taskScheme(intrinsicResultType)(polymorphicTask))
    |> addTypeBinding("Ashes.Task.sleep")(TypeScheme(quantified = [], body = SemFunction(SemInt)(intrinsicTaskOf(SemString)(SemInt))(None), constraints = []))
    |> addTypeBinding("Ashes.Task.all")(SemList(SemVariable(3))
    |> intrinsicTaskOf(SemVariable(2))
    |> taskScheme(SemList(polymorphicTask)))
    |> addTypeBinding("Ashes.Task.race")(taskScheme(SemList(polymorphicTask))(polymorphicTask))
    |> addTypeBinding("Ashes.Task.spawn")([]
    |> SemNamed(4)("Unit")
    |> taskScheme(polymorphicTask))
    |> addTypeBinding("Ashes.Task.scope")(taskScheme(polymorphicTask)(polymorphicTask))
    |> addTypeBinding("Ashes.Task.fork")(intrinsicJoinHandleType
    |> intrinsicTaskOf(SemVariable(2))
    |> taskScheme(polymorphicTask))
    |> addTypeBinding("Ashes.Task.join")(taskScheme(intrinsicJoinHandleType)(polymorphicTask))
    |> addTypeBinding("Ashes.Task.Parallel.both")(TypeScheme(quantified = [(2, "a"), (3, "b")], body = SemFunction(parallelThunkType(SemVariable(2)))(SemFunction(parallelThunkType(SemVariable(3)))(SemTuple([SemVariable(2), SemVariable(3)]))(None))(None), constraints = []))
    |> addTypeBinding("Ashes.Task.Parallel.withWorkers")(TypeScheme(quantified = [(3, "a")], body = SemFunction(SemInt)(SemFunction(parallelThunkType(SemVariable(3)))(SemVariable(3))(None))(None), constraints = []))
    |> addTypeBinding("Stop.stop")(TypeScheme(quantified = [], body = None
    |> SemRow([SemCapability("Stop")([])])
    |> Some
    |> SemFunction(SemNamed(4)("Unit")([]))(SemNamed(4)("Unit")([])), constraints = []))

// The runtime capability a builtin acquires, per the language reference's built-in runtime
// capabilities. The layout schemes carry no rows, so inference adds the row here.
let builtinRuntimeCapability (moduleName: Str) (memberName: Str) =
    match moduleName with
        | "Ashes.IO" ->
            match memberName with
                | "args" -> None
                | "exit" -> Some("ProcessExit")
                | _ -> Some("ConsoleIO")
        | "Ashes.IO.Console" ->
            match memberName with
                | "monotonicMillis" -> Some("TimeRead")
                | _ -> Some("ConsoleIO")
        | "Ashes.IO.File" ->
            match memberName with
                | "readText" -> Some("FileRead")
                | "readAllBytes" -> Some("FileRead")
                | "mmap" -> Some("FileRead")
                | "exists" -> Some("FileRead")
                | "open" -> Some("FileRead")
                | "writeText" -> Some("FileWrite")
                | "writeBytes" -> Some("FileWrite")
                | "replace" -> Some("FileWrite")
                | "makeExecutable" -> Some("FileWrite")
                | _ -> None
        | "Ashes.IO.Directory" ->
            match memberName with
                | "entries" -> Some("FileRead")
                | "createAll" -> Some("FileWrite")
                | "removeTree" -> Some("FileWrite")
                | _ -> None
        | "Ashes.IO.Environment" -> Some("EnvironmentRead")
        | "Ashes.IO.Process" ->
            match memberName with
                | "spawn" -> Some("ProcessSpawn")
                | _ -> None
        | "Ashes.Ffi" ->
            match memberName with
                | "copyBytes" -> Some("UnsafeFfi")
                | _ -> None
        | "Ashes.Net.Tcp.Server" ->
            match memberName with
                | "listen" -> Some("NetListen")
                | "forkWorkers" -> Some("NetListen")
                | _ -> None
        | "Ashes.Net.Tcp" ->
            match memberName with
                | "connect" -> Some("NetConnect")
                | _ -> None
        | "Ashes.Net.Http" -> Some("NetConnect")
        | "Ashes.Net.Tls" ->
            match memberName with
                | "connect" -> Some("NetConnect")
                | _ -> None
        | _ -> None

let recursive withRowOnInnermostArrow row semanticType =
    match semanticType with
        | SemFunction(argument, SemFunction(_, _, _) as result, outerRow) ->
            SemFunction(argument)(withRowOnInnermostArrow(row)(result))(outerRow)
        | SemFunction(argument, result, _row) -> SemFunction(argument)(result)(Some(row))
        | other -> other

let schemeWithRuntimeCapability moduleName memberName scheme =
    match (builtinRuntimeCapability(moduleName)(memberName), scheme) with
        | (Some(capabilityName), TypeScheme { quantified = quantified, body = body, constraints = constraints }) ->
            TypeScheme(quantified = quantified, body = withRowOnInnermostArrow(SemRow([SemCapability(capabilityName)([])])(None))(body), constraints = constraints)
        | (None, _) -> scheme

// `standardTraitEnvironment` plus the intrinsic builtins' own signatures. The intrinsic modules
// (`Ashes.IO`, `Ashes.Byte`, `Ashes.Text` and the rest of `coreBuiltinKind`'s) have no shipped
// `.ash` source, so a stitched program never declares them: lowering knows them through
// `standardBuiltinLayouts`, and without the same knowledge inference stops at the first one a real
// program mentions. `Unit` is here for the same reason — it is a built-in type name that
// `resolveTypeName` does not special-case, so nothing else would define it.
let recursive addBuiltinSignatures layouts environment =
    match layouts with
        | [] -> environment
        | CoreBuiltinLayout { moduleName = moduleName, memberName = memberName, scheme = scheme } :: rest ->
            environment
            |> addTypeBinding(moduleName + "." + memberName)(scheme
            |> schemeWithIntrinsicTypeIds
            |> schemeWithRuntimeCapability(moduleName)(memberName))
            |> addBuiltinSignatures(rest)

// Three intrinsics accept more than one argument type at a saturated call, which neither compiler
// expresses in the layout scheme: `Ashes.Number.UInt.toInt` takes any unsigned width,
// `Ashes.Text.fromInt` an `Int` or an unsigned width up to 32 bits, and `Ashes.Text.toHex` an `Int`
// or any unsigned width. Inference gives each a variable argument constrained by a
// compiler-private trait with one implementation per accepted type, so the rule holds whatever
// order the argument's type is discovered in. The names cannot be written in source.
let unsignedIntegerArgumentTrait = "$UnsignedIntegerArgument"

let textFromIntArgumentTrait = "$TextFromIntArgument"

let textToHexArgumentTrait = "$TextToHexArgument"

// `Ashes.IO.print` takes an `Int`, an unsigned width below 64 bits, a `Str` or a `Bool`.
let printArgumentTrait = "$PrintArgument"

let recursive addArgumentWidthImplementations traitName heads environment =
    match heads with
        | [] -> environment
        | head :: tail ->
            environment
            |> addTraitImplementation(traitName)([head])([])([])
            |> addArgumentWidthImplementations(traitName)(tail)

let addArgumentWidthTrait traitName heads environment =
    environment
    |> addTraitBinding(traitName)(1)([SemVariable(0)])([])([])
    |> addArgumentWidthImplementations(traitName)(heads)

let argumentWidthScheme traitName resultType =
    TypeScheme(quantified = [(0, "a")], body = SemFunction(SemVariable(0))(resultType)(None), constraints = [TraitConstraint(traitName = traitName, typeArguments = [SemVariable(
        0
    )])])

let withArgumentWidthSignatures environment =
    environment
    |> addArgumentWidthTrait(unsignedIntegerArgumentTrait)([SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64)])
    |> addArgumentWidthTrait(textFromIntArgumentTrait)([SemInt, SemUInt(8), SemUInt(16), SemUInt(32)])
    |> addArgumentWidthTrait(textToHexArgumentTrait)([SemInt, SemUInt(8), SemUInt(16), SemUInt(32), SemUInt(64)])
    |> addArgumentWidthTrait(printArgumentTrait)([SemInt, SemUInt(8), SemUInt(16), SemUInt(32), SemString, SemBool])
    |> addTypeBinding("Ashes.Number.UInt.toInt")(argumentWidthScheme(unsignedIntegerArgumentTrait)(SemInt))
    |> addTypeBinding("Ashes.Text.fromInt")(argumentWidthScheme(textFromIntArgumentTrait)(SemString))
    |> addTypeBinding("Ashes.Text.toHex")(argumentWidthScheme(textToHexArgumentTrait)(SemString))
    |> addTypeBinding("Ashes.IO.print")([]
    |> SemNamed(4)("Unit")
    |> argumentWidthScheme(printArgumentTrait)
    |> schemeWithRuntimeCapability("Ashes.IO")("print"))

let withIntrinsicBuiltinSignatures environment =
    environment
    |> withIntrinsicTypes
    |> addBuiltinSignatures(standardBuiltinLayouts)
    |> withArgumentWidthSignatures
    |> withTaskSignatures
