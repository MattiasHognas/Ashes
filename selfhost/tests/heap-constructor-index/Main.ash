import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.HeapLayoutClassification
import Ashes.Collection.List.append
import Ashes.Collection.List.length
let definition name id typeName arguments fields fieldNames =
    (let recursive arrow remaining =
        match remaining with
            | [] -> SemNamed(id)(typeName)(arguments)
            | field :: rest ->
                SemFunction(field)(arrow(rest))(None)
    in ConstructorInferenceDefinition(name = name, scheme = TypeScheme(quantified = [], body = arrow(fields), constraints = []), fieldNames = fieldNames))

let fixtures =
    [
        definition("A0")(1)("A")([])([SemInt])(["value"]),
        definition("A1")(1)("A")([SemVariable(3)])([SemVariable(3)])([]),
        definition("B0")(1)("B")([])([SemString])([]),
        definition("A2")(2)("A")([])([SemInt])([]),
        definition("A3")(2)("A")([])([SemInt])(["value"])
    ]

let oldExtend (environment: TypeEnvironment) definitions =
    environment with constructors = append(environment.constructors)(definitions), constructorFieldGroups = append(environment.constructorFieldGroups)(heapConstructorFieldGroups(definitions))

let queries = [SemNamed(1)("A")([SemString]), SemNamed(1)("B")([]), SemNamed(2)("A")([]), SemNamed(2)("B")([]), SemNamed(99)("Missing")([])]

let recursive compareQueries old indexed remaining =
    match remaining with
        | [] -> Unit
        | query :: rest ->
            if heapNamedTypeConstructors(query)(old) == heapNamedTypeConstructors(query)(indexed) && classifyHeapLayout(query)(old) == classifyHeapLayout(query)(indexed)
            then compareQueries(old)(indexed)(rest)
            else Ashes.IO.panic("indexed environment mismatch")

let compare definitions =
    (let empty = emptyTypeEnvironment(Unit)
    in
        let old = oldExtend(empty)(definitions)
        in
            let indexed = heapExtendConstructorEnvironment(empty)(definitions)
            in
                queries
                |> compareQueries(old)(indexed)
                |> (given (_) ->
                    compareQueries(oldExtend(old)(fixtures))(heapExtendConstructorEnvironment(indexed)(fixtures))(queries)))

let boundaryCases unit =
    (let effectful =
        ConstructorInferenceDefinition(name = "Effectful", scheme = TypeScheme(quantified = [], body = None
        |> SemRow([])
        |> Some
        |> SemFunction(SemInt)(SemNamed(1)("A")([])), constraints = []), fieldNames = ["ignored"])
    in
        effectful :: fixtures
        |> compare
        |> (given (_) ->
            let empty = emptyTypeEnvironment(Unit)
            in
                let scheme =
                    TypeScheme(quantified = [], body = SemFunction(SemInt)(SemNamed(2)("A")([]))(None), constraints = [])
                in
                    let old =
                        fixtures
                        |> oldExtend(empty)
                        |> addConstructorBinding("NewRecord")(scheme)(["value"])
                    in
                        let indexed =
                            fixtures
                            |> heapExtendConstructorEnvironment(empty)
                            |> addConstructorBinding("NewRecord")(scheme)(["value"])
                        in
                            queries
                            |> compareQueries(old)(indexed)
                            |> (given (_) ->
                                compareQueries(oldExtend(old)(fixtures))(heapExtendConstructorEnvironment(indexed)(fixtures))(queries))))

let recursive selections depth prefix =
    if depth == 0
    then compare(prefix)
    else walk(depth)(prefix)(fixtures)
and walk depth prefix remaining =
    match remaining with
        | [] -> Unit
        | head :: rest ->
            head :: prefix
            |> selections(depth - 1)
            |> (given (_) -> walk(depth)(prefix)(rest))

let recursive fresh count useIndex total =
    if count == 0
    then
        total
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        let name = Ashes.Text.fromInt(count)
        in
            let definitions = [definition(name)(count)(name)([])([SemInt])(["field"])]
            in
                let environment =
                    if useIndex
                    then
                        heapExtendConstructorEnvironment(emptyTypeEnvironment(Unit))(definitions)
                    else
                        oldExtend(emptyTypeEnvironment(Unit))(definitions)
                in
                    let fields =
                        heapNamedTypeConstructors(SemNamed(count)(name)([]))(environment)
                    in
                        let facts =
                            classifyHeapLayout(SemNamed(count)(name)([]))(environment)
                        in
                            if facts.containsResource || facts.containsUnresolvedType
                            then Ashes.IO.panic("fresh classification mismatch")
                            else fresh(count - 1)(useIndex)(total + length(fields))

match Ashes.IO.args with
    | "check" :: [] ->
        Unit
        |> boundaryCases
        |> (given (_) -> selections(3)([]))
        |> (given (_) -> Ashes.IO.print("125 indexed environments ok"))
    | "old1000" :: [] -> fresh(1000)(false)(0)
    | "old10000" :: [] -> fresh(10000)(false)(0)
    | "new1000" :: [] -> fresh(1000)(true)(0)
    | "new10000" :: [] -> fresh(10000)(true)(0)
    | _ -> Ashes.IO.panic("mode required")
