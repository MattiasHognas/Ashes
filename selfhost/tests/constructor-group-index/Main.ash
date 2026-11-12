import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.HeapLayoutClassification.heapConstructorFieldGroups
import Ashes.Collection.List.length
import Ashes.Collection.List.reverse
import Ashes.Collection.List.map
let recursive heapConstructorFieldShape (body: SemanticType) (fields: List(SemanticType)) =
    match body with
        | SemFunction(field, result, _row) -> heapConstructorFieldShape(result)(field :: fields)
        | result -> (reverse(fields), result)

let recursive heapAddToGroups (symbolId: Int) (typeName: Str) (constructorName: Str) (fieldTypes: List(SemanticType)) (resultArguments: List(SemanticType)) (groups: List(ConstructorFieldGroup)) =
    match groups with
        | [] ->
            [
                ConstructorFieldGroup(
                    groupSymbolId = symbolId,
                    groupTypeName = typeName,
                    groupIsGeneric = length(resultArguments) > 0,
                    groupFields = [(constructorName, fieldTypes)],
                    groupGenericFields = [(constructorName, fieldTypes, resultArguments)]
                )
            ]
        | (ConstructorFieldGroup { groupSymbolId = candidateId, groupTypeName = candidateName, groupIsGeneric = isGeneric, groupFields = fields, groupGenericFields = genericFields } as group) :: rest ->
            if candidateId == symbolId && candidateName == typeName
            then
                ConstructorFieldGroup(
                    groupSymbolId = candidateId,
                    groupTypeName = candidateName,
                    groupIsGeneric = isGeneric || length(resultArguments) > 0,
                    groupFields = (constructorName, fieldTypes) :: fields,
                    groupGenericFields = (constructorName, fieldTypes, resultArguments) :: genericFields
                ) :: rest
            else group :: heapAddToGroups(symbolId)(typeName)(constructorName)(fieldTypes)(resultArguments)(rest)

let recursive heapCollectGroups (constructors: List(ConstructorInferenceDefinition)) (groups: List(ConstructorFieldGroup)) =
    match constructors with
        | [] -> groups
        | ConstructorInferenceDefinition { name = constructorName, scheme = TypeScheme { body = body } } :: rest ->
            match heapConstructorFieldShape(body)([]) with
                | (fieldTypes, SemNamed(symbolId, typeName, resultArguments)) ->
                    groups
                    |> heapAddToGroups(symbolId)(typeName)(constructorName)(fieldTypes)(resultArguments)
                    |> heapCollectGroups(rest)
                | _ -> heapCollectGroups(rest)(groups)

let orderGroup (group: ConstructorFieldGroup) = group with groupFields = reverse(group.groupFields), groupGenericFields = reverse(group.groupGenericFields)

let oldGroups (definitions: List(ConstructorInferenceDefinition)) =
    []
    |> heapCollectGroups(definitions)
    |> map(orderGroup)

let definition name id typeName arguments fields =
    (let recursive arrow remaining =
        match remaining with
            | [] -> SemNamed(id)(typeName)(arguments)
            | field :: rest ->
                SemFunction(field)(arrow(rest))(None)
    in ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = arrow(fields), constraints = []), fieldNames = []))

let fixtures = [definition("A0")(1)("A")([])([SemInt]), definition("B0")(1)("B")([])([SemString]), definition("A1")(1)("A")([SemVariable(3)])([SemVariable(3)]), definition("A2")(2)("A")([])([]), definition("B1")(1)("B")([SemVariable(4)])([SemList(SemVariable(4))]), ConstructorInferenceDefinition(name = "Invalid", scheme = TypeScheme(quantified = [], body = SemInt, constraints = []), fieldNames = [])]

let recursive sameFields (left: List((Str, List(SemanticType)))) (right: List((Str, List(SemanticType)))) =
    match (left, right) with
        | ([], []) -> true
        | ((leftName, leftTypes) :: leftRest, (rightName, rightTypes) :: rightRest) -> leftName == rightName && leftTypes == rightTypes && sameFields(leftRest)(rightRest)
        | _ -> false

let recursive sameGenericFields (left: List((Str, List(SemanticType), List(SemanticType)))) (right: List((Str, List(SemanticType), List(SemanticType)))) =
    match (left, right) with
        | ([], []) -> true
        | ((leftName, leftTypes, leftArguments) :: leftRest, (rightName, rightTypes, rightArguments) :: rightRest) -> leftName == rightName && leftTypes == rightTypes && leftArguments == rightArguments && sameGenericFields(leftRest)(rightRest)
        | _ -> false

let recursive sameGroups (left: List(ConstructorFieldGroup)) (right: List(ConstructorFieldGroup)) =
    match (left, right) with
        | ([], []) -> true
        | (a :: leftRest, b :: rightRest) -> a.groupSymbolId == b.groupSymbolId && a.groupTypeName == b.groupTypeName && a.groupIsGeneric == b.groupIsGeneric && sameFields(a.groupFields)(b.groupFields) && sameGenericFields(a.groupGenericFields)(b.groupGenericFields) && sameGroups(leftRest)(rightRest)
        | _ -> false

let compare (definitions: List(ConstructorInferenceDefinition)) =
    if definitions
    |> heapConstructorFieldGroups
    |> sameGroups(oldGroups(definitions))
    then Unit
    else Ashes.IO.panic("group mismatch")

let recursive selections depth prefix =
    if depth == 0
    then compare(prefix)
    else walkSelections(depth)(prefix)(fixtures)
and walkSelections depth prefix remaining =
    match remaining with
        | [] -> Unit
        | item :: rest ->
            item :: prefix
            |> selections(depth - 1)
            |> (given (_) -> walkSelections(depth)(prefix)(rest))

let recursive unique remaining =
    if remaining == 0
    then []
    else
        definition(Ashes.Text.fromInt(remaining))(remaining)(Ashes.Text.fromInt(remaining))([])([SemInt]) :: unique(remaining - 1)

let recursive run count useOld definitions total =
    if count == 0
    then
        total
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        let groups =
            if useOld
            then oldGroups(definitions)
            else heapConstructorFieldGroups(definitions)
        in run(count - 1)(useOld)(definitions)(total + length(groups))

let recursive fresh count useOld total =
    if count == 0
    then
        total
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        let definitions = unique(12)
        in
            let groups =
                if useOld
                then oldGroups(definitions)
                else heapConstructorFieldGroups(definitions)
            in fresh(count - 1)(useOld)(total + length(groups))

match Ashes.IO.args with
    | "check" :: [] ->
        []
        |> compare
        |> (given (_) -> selections(4)([]))
        |> (given (_) -> Ashes.IO.print("1296 groups ok"))
    | "old" :: [] ->
        run(20)(true)(unique(2000))(0)
    | "new" :: [] ->
        run(20)(false)(unique(2000))(0)
    | "old1000" :: [] -> fresh(1000)(true)(0)
    | "old10000" :: [] -> fresh(10000)(true)(0)
    | "new1000" :: [] -> fresh(1000)(false)(0)
    | "new10000" :: [] -> fresh(10000)(false)(0)
    | _ -> Ashes.IO.panic("mode required")
