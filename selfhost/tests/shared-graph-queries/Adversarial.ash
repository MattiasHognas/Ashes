import LegacyClassification
import Oracle
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.TypeGraphQueries
import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
export (
    value adversarialCases,
    value freshGenericCase,
)

let genericA value = SemNamed(40)("A")([value])

let genericB value = SemNamed(41)("B")([value])

let genericEnvironment leaf = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 40, groupTypeName = "A", groupIsGeneric = true, groupFields = [], groupGenericFields = [("A", [SemVariable(7), genericB(SemVariable(7))], [SemVariable(7)])]), ConstructorFieldGroup(groupSymbolId = 41, groupTypeName = "B", groupIsGeneric = true, groupFields = [], groupGenericFields = [("B", [genericA(leaf)], [SemVariable(7)])])]

let recursive compareIndexed (types: List(SemanticType)) (index: TypeGraphIndex) =
    match types with
        | [] -> true
        | head :: rest ->
            let baseline = legacy_classifyHeapAdmissibility(head)(index.environment)
            in
                match typeGraphResource(head)(index) with
                    | (resource, next) ->
                        match typeGraphUnresolved(head)(next) with
                            | (unresolved, completed) -> resource == baseline.containsResource && unresolved == baseline.containsUnresolvedType && rootViewsAgree(head)(index.environment) && compareIndexed(rest)(completed)

let compareTypes (types: List(SemanticType)) (environment: TypeEnvironment) =
    environment
    |> emptyTypeGraphIndex
    |> compareIndexed(types
    |> Ashes.Collection.List.reverse
    |> Ashes.Collection.List.append(types))

let genericCase leaf =
    (let environment = genericEnvironment(leaf)
    in
        let roots =
            [genericA(SemInt), genericA(leaf), genericB(SemInt), genericB(leaf), SemInt
            |> genericA
            |> SemList, SemTuple([genericB(SemInt), genericA(leaf)])]
        in compareTypes(roots)(environment) && compareTypes(roots)((environment with constructorFieldGroups = Ashes.Collection.List.reverse(environment.constructorFieldGroups))))

let identityCase unit =
    (let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 1, groupTypeName = "Same", groupIsGeneric = false, groupFields = [("One", [SemString])], groupGenericFields = []), ConstructorFieldGroup(groupSymbolId = 2, groupTypeName = "Same", groupIsGeneric = false, groupFields = [("Two", [SemVariable(91)])], groupGenericFields = []), ConstructorFieldGroup(groupSymbolId = 1, groupTypeName = "Other", groupIsGeneric = false, groupFields = [("Three", [SemNamed(99)("FileHandle")([])])], groupGenericFields = [])]
    in
        let wrapped =
            environment
            |> addInferenceZeroCostTypeDefinition(2000)("TupleWrapper")([])(SemTuple([SemString, SemList(SemInt)]))
            |> addInferenceZeroCostTypeDefinition(2001)("ResourceWrapper")([])(SemNamed(99)("FileHandle")([]))
        in
            let roots =
                [SemNamed(1)("Same")([]), SemNamed(2)("Same")([]), SemNamed(1)("Other")([]), SemNamed(2000)("TupleWrapper")([]), SemNamed(2001)("ResourceWrapper")([]), []
                |> SemNamed(2000)("TupleWrapper")
                |> SemList, SemNamed(999)("Missing")([]), SemNamed(99)("FileHandle")([]), SemOpaque("Handle")]
            in
                compareTypes(roots)(wrapped) && compareTypes(roots)(emptyTypeEnvironment(Unit)) && compareTypes(roots)(wrapped))

let adversarialCases unit =
    genericCase(SemInt) && genericCase(SemVariable(88)) && genericCase(SemString) && genericCase(SemNamed(99)("FileHandle")([])) && identityCase(Unit)

// Fresh free identities never overlap the constructor's generalized binder7.
let freshGenericCase round nonregular =
    (let argument = SemVariable(100000 + round)
    in
        let leaf =
            if nonregular
            then SemString
            else argument
        in
            leaf
            |> genericEnvironment
            |> rootViewsAgree(genericA(argument)))
