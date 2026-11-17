import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import Ashes.Collection.List.append
import Ashes.Collection.List.map
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
import Ashes.Collection.Map.MapTree
// PRODUCTION_INDEX
// The actual field helpers only inspect these state fields. Complete Core module
// validation remains separate from this focused relation and lifetime fixture.
type CoreProgramState =
    | constructorIndex: CoreConstructorLookupIndex

type CoreLoweringState =
    | typeSupply: TypeVariableSupply
    | substitution: MapTree(Int, SemanticType)
    | programState: CoreProgramState

// PRODUCTION_HELPERS
let raw name quantified body = CoreConstructorLayout(name = name, tag = 0, scheme = TypeScheme(quantified = quantified, body = body, constraints = []), fieldNames = [], isZeroCost = false, tagless = false)

let named = SemNamed(10)("Tree")([])

let closed =
    None
    |> SemFunction(SemString)(named)
    |> raw("Leaf")([])

let recursiveClosed =
    None
    |> SemFunction(SemList(named))(SemFunction(SemTuple([SemInt, named]))(named)(None))
    |> raw("Branch")([])

let open =
    None
    |> SemFunction(SemVariable(7))(named)
    |> raw("Open")([])

let generic =
    None
    |> SemFunction(SemVariable(7))(SemNamed(11)("Generic")([SemVariable(7)]))
    |> raw("Generic")([(7, "a")])

let rigid =
    None
    |> SemFunction(SemParameter(7)("a"))(named)
    |> raw("Rigid")([])

let quantifiedClosed =
    None
    |> SemFunction(SemInt)(named)
    |> raw("UnusedQuantifier")([(7, "a")])

let missingResult =
    None
    |> SemFunction(SemString)(SemVariable(7))
    |> raw("Missing")([])

let sameNameOtherId =
    None
    |> SemFunction(SemBytes)(SemNamed(99)("Tree")([]))
    |> raw("OtherId")([])

let functionField =
    None
    |> SemFunction(None
    |> SemRow([SemCapability("IO")([])])
    |> Some
    |> SemFunction(SemInt)(SemString))(named)
    |> raw("Fn")([])

let nullary = raw("Empty")([])(named)

let fixtures = [closed, recursiveClosed, open, generic, rigid, quantifiedClosed, missingResult, sameNameOtherId, functionField, nullary]

let state layouts supply substitution = CoreLoweringState(typeSupply = typeVariableSupplyFrom(supply), substitution = extendSubstitutionMap(substitution)(Ashes.Collection.Map.empty), programState = CoreProgramState(constructorIndex = constructorLookupIndex(layouts)))

let check message condition =
    if condition
    then Unit
    else Ashes.IO.panic(message)

let checkState layouts supply substitution =
    (let current = state(layouts)(supply)(substitution)
    in
        let recursive names remaining =
            match remaining with
                | [] -> Unit
                | name :: rest ->
                    let bucket = stateConstructorsOfType(name)(current)
                    in
                        let originalDeclared = namedTypeConstructorFieldTypes(name)(bucket)(current)(false)([])
                        in
                            originalDeclared == namedTypeDeclaredFields(name)(current)
                            |> check("declared fields changed")
                            |> (given (_) ->
                                let recursive arguments remaining =
                                    match remaining with
                                        | [] -> names(rest)
                                        | values :: tail ->
                                            namedTypeInstantiatedFieldTypes(name)(values)(bucket)(current)([]) == namedTypeFieldTypesAs(true)(name)(values)(current)
                                            |> check("instantiated fields changed")
                                            |> (given (_) -> arguments(tail))
                                in arguments([[], [SemInt], [SemString], [SemVariable(7)], [SemNamed(10)("Tree")([])]]))
        in names(["Tree", "Generic", "missing"]))

let checkLayouts layouts =
    []
    |> checkState(layouts)(100)
    |> (given (_) -> checkState(layouts)(500)([(7, SemInt), (100, SemString)]))
    |> (given (_) -> checkState(layouts)(1000)([(7, SemBytes), (100, SemBool)]))

let recursive combinations depth prefix =
    if depth == 0
    then checkLayouts(prefix)
    else choices(depth)(prefix)(fixtures)
and choices depth prefix remaining =
    match remaining with
        | [] -> Unit
        | head :: rest ->
            head :: prefix
            |> combinations(depth - 1)
            |> (given (_) -> choices(depth)(prefix)(rest))

let checkAdmission unit =
    lookupStableConstructorFields("Tree")(constructorLookupIndex([closed, recursiveClosed, sameNameOtherId, nullary])) == Some([SemString, SemList(named), SemTuple([SemInt, named]), SemBytes])
    |> check("closed recursive order")
    |> (given (_) ->
        check("open bucket cached")(lookupStableConstructorFields("Tree")(constructorLookupIndex([closed, open])) == None))
    |> (given (_) ->
        check("rigid bucket cached")(lookupStableConstructorFields("Tree")(constructorLookupIndex([closed, rigid])) == None))
    |> (given (_) ->
        check("quantified bucket cached")(lookupStableConstructorFields("Tree")(constructorLookupIndex([closed, quantifiedClosed])) == None))
    |> (given (_) ->
        check("generic bucket cached")(lookupStableConstructorFields("Generic")(constructorLookupIndex([generic])) == None))
    |> (given (_) ->
        check("nullary missing distinction")(lookupStableConstructorFields("Tree")(constructorLookupIndex([nullary])) == Some([]) && lookupStableConstructorFields("missing")(constructorLookupIndex([nullary])) == None))
    |> (given (_) ->
        check("replacement stale")(lookupStableConstructorFields("Tree")(constructorLookupIndex([closed])) != lookupStableConstructorFields("Tree")(constructorLookupIndex([sameNameOtherId]))))

let recursive repeated remaining useOld current (total: Int) =
    if remaining == 0
    then Ashes.IO.print(total)
    else
        let fields =
            if useOld
            then
                namedTypeInstantiatedFieldTypes("Tree")([])(stateConstructorsOfType("Tree")(current))(current)([])
            else namedTypeFieldTypesAs(true)("Tree")([])(current)
        in repeated(remaining - 1)(useOld)(current)(total + length(fields))

let fresh current = state([closed, recursiveClosed, sameNameOtherId])(current)([])

let recursive plateau remaining (total: Int) =
    if remaining == 0
    then Ashes.IO.print(total)
    else
        let fields =
            remaining
            |> fresh
            |> namedTypeFieldTypesAs(true)("Tree")([])
        in plateau(remaining - 1)(total + length(fields))

match Ashes.IO.args with
    | "check" :: [] ->
        []
        |> checkLayouts
        |> (given (_) -> combinations(3)([]))
        |> (given (_) -> checkAdmission(Unit))
        |> (given (_) -> Ashes.IO.print("1000 field relations ok"))
    | "old" :: [] ->
        repeated(100000)(true)(fresh(100))(0)
    | "new" :: [] ->
        repeated(100000)(false)(fresh(100))(0)
    | "1000" :: [] -> plateau(1000)(0)
    | "10000" :: [] -> plateau(10000)(0)
    | _ -> Ashes.IO.panic("mode required")
