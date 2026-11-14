import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ResultReachSummaries
import ReferenceResultReachSummaries
import Ashes.Collection.Map.MapTree
// The constructor lookup as it stood before the registry indexed its constructors by name.
let recursive frozenLookupConstructor (name: Str) (constructors: List(ReachConstructor)) =
    match constructors with
        | [] -> None
        | (ReachConstructor { name = candidate } as constructor) :: rest ->
            if candidate == name
            then Some(constructor)
            else frozenLookupConstructor(name)(rest)

let mix (left: Int) (right: Int) = (left * 1103515245 + right * 12345 + 1013904223) % 2147483647

let roll (seed: Int) (slot: Int) (bound: Int) =
    mix(mix(seed)(slot))(slot * 31 + 7) / 8 % bound

// Every name is a fresh literal: the generated syntax owns its strings outright.
let constructorAt (index: Int) =
    match index with
        | 0 -> "P"
        | 1 -> "Q"
        | 2 -> "Some"
        | 3 -> "None"
        | 4 -> "Unit"
        | 5 -> "R"
        | 6 -> "Ok"
        | _ -> "mk"

let queriedAt (index: Int) =
    match index with
        | 8 -> "Error"
        | 9 -> "Missing"
        | 10 -> "W"
        | 11 -> "p"
        | _ -> constructorAt(index)

let fieldTypeAt (index: Int) =
    match index with
        | 0 -> TypeNamed("Int")
        | 1 -> TypeNamed("Str")
        | 2 -> TypeNamed("W")
        | _ -> TypeNamed("Bool")

let recursive parametersOf (seed: Int) (slot: Int) (arity: Int) =
    if arity <= 0
    then []
    else
        fieldTypeAt(roll(seed)(slot + arity)(4)) :: parametersOf(seed)(slot)(arity - 1)

let variant (seed: Int) (slot: Int) =
    TypeConstructor(name = 8
    |> roll(seed)(slot)
    |> constructorAt, parameters = 3
    |> roll(seed)(slot + 1)
    |> parametersOf(seed)(slot + 1), fieldNames = [])

let recordConstructor (seed: Int) (slot: Int) =
    TypeConstructor(name = 8
    |> roll(seed)(slot)
    |> constructorAt, parameters = [4
    |> roll(seed)(slot + 1)
    |> fieldTypeAt, 4
    |> roll(seed)(slot + 2)
    |> fieldTypeAt], fieldNames = ["x", "y"])

let typeItemsAt (seed: Int) (slot: Int) =
    match roll(seed)(slot)(7) with
        | 0 -> []
        | 1 -> [TopLevelType(TypeDecl(name = "T" + Ashes.Text.fromInt(slot), typeParameters = [], constructors = [variant(seed)(slot + 10), variant(seed)(slot + 20)], isRecord = false, derivingTraits = []))]
        | 2 -> [TopLevelType(TypeDecl(name = "T" + Ashes.Text.fromInt(slot), typeParameters = [], constructors = [variant(seed)(slot + 10), variant(seed)(slot + 20), variant(seed)(slot + 30)], isRecord = false, derivingTraits = []))]
        | 3 ->
            [TopLevelType(TypeDecl(name = 8
            |> roll(seed)(slot + 10)
            |> constructorAt, typeParameters = [], constructors = [recordConstructor(seed)(slot + 10)], isRecord = true, derivingTraits = []))]
        | 4 ->
            [TopLevelZeroCostType(ZeroCostTypeDecl(name = "W", typeParameters = [], constructor = TypeConstructor(name = 8
            |> roll(seed)(slot + 10)
            |> constructorAt, parameters = [2
            |> roll(seed)(slot + 11)
            |> fieldTypeAt], fieldNames = []), derivingTraits = []))]
        | 5 ->
            [TopLevelTypeAlias(TypeAliasDecl(name = "W", typeParameters = [], target = 2
            |> roll(seed)(slot + 11)
            |> fieldTypeAt))]
        | _ -> [TopLevelType(TypeDecl(name = "T" + Ashes.Text.fromInt(slot), typeParameters = [], constructors = [variant(seed)(slot + 10)], isRecord = false, derivingTraits = []))]

let call (function: Expr) (argument: Expr) = ExprCall(function)(argument)(false)(callArgumentsInline)

let named (seed: Int) (slot: Int) =
    8
    |> roll(seed)(slot)
    |> constructorAt

// One body for each place the reach walk asks for a constructor by name.
let bodyAt (seed: Int) (slot: Int) (index: Int) =
    match roll(seed)(slot)(10) with
        | 0 ->
            slot + 1
            |> named(seed)
            |> ExprVar
        | 1 ->
            call(slot + 1
            |> named(seed)
            |> ExprVar)(ExprVar("p"))
        | 2 ->
            call(call(slot + 1
            |> named(seed)
            |> ExprVar)(ExprVar("p")))(ExprVar("q"))
        | 3 ->
            ExprRecord(named(seed)(slot + 1))([("x", ExprVar("p")), ("y", ExprVar("q"))])(false)
        | 4 ->
            ExprMatch(ExprVar("p"))([(PatternRecord(named(seed)(slot + 1))([("x", PatternVar("a")), ("y", PatternVar("b"))]), ExprVar("a"), None), (PatternWildcard, ExprVar("q"), None)])(None)
        | 5 ->
            ExprMatch(ExprVar("p"))([(slot + 1
            |> named(seed)
            |> PatternVar, ExprVar("q"), None), (PatternVar("other"), ExprVar("other"), None)])(None)
        | 6 ->
            ExprMatch(ExprVar("p"))([(PatternConstructor(named(seed)(slot + 1))([PatternVar("a")]), ExprVar("a"), None), (PatternWildcard, ExprVar("q"), None)])(None)
        | 7 ->
            call(ExprVar("p")
            |> call(slot + 1
            |> named(seed)
            |> ExprVar)
            |> call(ExprVar("f" + Ashes.Text.fromInt((index + 1) % 3))))(ExprVar("q"))
        | 8 ->
            ExprTuple([slot + 1
            |> named(seed)
            |> ExprVar, call(slot + 2
            |> named(seed)
            |> ExprVar)(ExprVar("q"))])
        | _ ->
            ExprMatch(ExprVar("p"))([(slot + 1
            |> named(seed)
            |> PatternVar, call(slot + 2
            |> named(seed)
            |> ExprVar)(ExprVar("q")), None), (PatternWildcard, ExprRecord(named(seed)(slot + 3))([("x", ExprVar("p")), ("y", ExprVar("p"))])(false), None)])(None)

let functionItem (seed: Int) (index: Int) =
    TopLevelLet(LetBindingSyntax(name = "f" + Ashes.Text.fromInt(index), value = ExprLambda("p")(ExprLambda("q")(bodyAt(seed)(500 + index * 10)(index))(None))(None), sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1))(roll(seed)(600 + index)(2) == 0)

let programOf (seed: Int) =
    ProgramSyntax(items = [functionItem(seed)(0), functionItem(seed)(1), functionItem(seed)(2)]
    |> Ashes.Collection.List.append(typeItemsAt(seed)(300))
    |> Ashes.Collection.List.append(typeItemsAt(seed)(200))
    |> Ashes.Collection.List.append(typeItemsAt(seed)(100)), body = None)

let recursive lookupsAgree (index: Int) (constructors: List(ReachConstructor)) (registry: ReachRegistry) =
    if index > 11
    then true
    else
        frozenLookupConstructor(queriedAt(index))(constructors) == Ashes.Collection.Map.getStr(queriedAt(index))(registry.constructorsByName) && lookupsAgree(index + 1)(constructors)(registry)

let recursive constructorsAgree (new: List(ReachConstructor)) (old: List(RefReachConstructor)) =
    match (new, old) with
        | ([], []) -> true
        | (ReachConstructor { name = newName, arity = newArity, fieldNames = newFields, copyFields = newCopies, soleNullary = newSole } :: newRest, RefReachConstructor { name = oldName, arity = oldArity, fieldNames = oldFields, copyFields = oldCopies, soleNullary = oldSole } :: oldRest) -> newName == oldName && newArity == oldArity && newFields == oldFields && newCopies == oldCopies && newSole == oldSole && constructorsAgree(newRest)(oldRest)
        | _ -> false

let statesAgree (new: ResultReachState) (old: RefResultReachState) =
    match (new, old) with
        | (ResultReachState { counts = newCounts, causes = newCauses, isPoisoned = newPoisoned }, RefResultReachState { counts = oldCounts, causes = oldCauses, isPoisoned = oldPoisoned }) -> newCounts == oldCounts && newCauses == oldCauses && newPoisoned == oldPoisoned

let recursive summariesAgree (new: List(ReachSummary)) (old: List(RefReachSummary)) =
    match (new, old) with
        | ([], []) -> true
        | (ReachSummary { function = ReachFunction { key = newKey }, reach = newReach, mustReach = newMust } :: newRest, RefReachSummary { function = RefReachFunction { key = oldKey }, reach = oldReach, mustReach = oldMust } :: oldRest) -> newKey == oldKey && newMust == oldMust && statesAgree(newReach)(oldReach) && summariesAgree(newRest)(oldRest)
        | _ -> false

let differs (seed: Int) (what: Str) = Ashes.IO.panic("the indexed constructor lookup differs from the frozen oracle at seed " + Ashes.Text.fromInt(seed) + ": " + what)

let recursive poisonedIn (summaries: List(ReachSummary)) =
    match summaries with
        | [] -> 0
        | ReachSummary { reach = ResultReachState { isPoisoned = true } } :: rest -> 1 + poisonedIn(rest)
        | _ :: rest -> poisonedIn(rest)

// Twelve lookups, the constructor list and the summaries of each program; the second count is the
// summaries that came out poisoned, to show both outcomes are exercised.
let checkSeed (seed: Int) (tally: (Int, Int, Int)) =
    match (seed
    |> programOf
    |> buildReachRegistry, seed
    |> programOf
    |> ref_buildReachRegistry, tally) with
        | (registry, RefReachRegistry { constructors = oldConstructors }, (comparisons, poisoned, constructors)) ->
            if lookupsAgree(0)(registry.constructors)(registry)
            then
                if constructorsAgree(registry.constructors)(oldConstructors)
                then
                    let summaries =
                        seed
                        |> programOf
                        |> programReachSummaries
                    in
                        if seed
                        |> programOf
                        |> ref_programReachSummaries
                        |> summariesAgree(summaries)
                        then (comparisons + 14, poisoned + poisonedIn(summaries), constructors + Ashes.Collection.List.length(registry.constructors))
                        else differs(seed)("summaries")
                else differs(seed)("constructor list")
            else differs(seed)("lookup")

let recursive checkSeeds (seed: Int) (last: Int) (tally: (Int, Int, Int)) =
    if seed > last
    then tally
    else
        tally
        |> checkSeed(seed)
        |> checkSeeds(seed + 1)(last)

let report (tally: (Int, Int, Int)) =
    match tally with
        | (comparisons, poisoned, constructors) -> Ashes.IO.print("ok comparisons=" + Ashes.Text.fromInt(comparisons) + " poisonedSummaries=" + Ashes.Text.fromInt(poisoned) + " constructors=" + Ashes.Text.fromInt(constructors))

// The benchmark program: `count` two-constructor types, and `functions` functions each applying a
// constructor to its parameters and reading a free name.
let recursive benchTypes (index: Int) (count: Int) =
    if index >= count
    then []
    else TopLevelType(TypeDecl(name = "T" + Ashes.Text.fromInt(index), typeParameters = [], constructors = [TypeConstructor(name = "C" + Ashes.Text.fromInt(index) + "a", parameters = [TypeNamed("Str"), TypeNamed("Str")], fieldNames = []), TypeConstructor(name = "C" + Ashes.Text.fromInt(index) + "b", parameters = [TypeNamed("Int")], fieldNames = [])], isRecord = false, derivingTraits = [])) :: benchTypes(index + 1)(count)

let recursive benchFunctions (index: Int) (functions: Int) (count: Int) =
    if index >= functions
    then []
    else
        TopLevelLet(LetBindingSyntax(name = "g" + Ashes.Text.fromInt(index), value = ExprLambda("p")(ExprLambda("q")(ExprTuple([call(call(ExprVar("C" + Ashes.Text.fromInt(index * 7 % count) + "a"))(ExprVar("p")))(ExprVar("q")), ExprVar("free" + Ashes.Text.fromInt(index)), call(ExprVar("helper" + Ashes.Text.fromInt(index)))(ExprVar("p"))]))(None))(None), sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1))(false) :: benchFunctions(index + 1)(functions)(count)

let benchProgram (count: Int) (functions: Int) =
    ProgramSyntax(items = count
    |> benchFunctions(0)(functions)
    |> Ashes.Collection.List.append(benchTypes(0)(count)), body = None)

let arityOf (found: Maybe(ReachConstructor)) =
    match found with
        | Some(ReachConstructor { arity = arity }) -> arity + 1
        | None -> 0

// Each round asks for one declared constructor and three names that are none, the mix a walk over
// function bodies produces: most bare names are variables.
let recursive oldLookups (constructors: List(ReachConstructor)) (count: Int) (index: Int) (rounds: Int) (sum: Int) =
    if index >= rounds
    then sum
    else
        oldLookups(constructors)(count)(index + 1)(rounds)(sum + arityOf(frozenLookupConstructor("C" + Ashes.Text.fromInt(index % count) + "b")(constructors)) + arityOf(frozenLookupConstructor("value" + Ashes.Text.fromInt(index))(constructors)) + arityOf(frozenLookupConstructor("p")(constructors)) + arityOf(frozenLookupConstructor("accumulator")(constructors)))

let recursive newLookups (constructorsByName: MapTree(Str, ReachConstructor)) (count: Int) (index: Int) (rounds: Int) (sum: Int) =
    if index >= rounds
    then sum
    else
        newLookups(constructorsByName)(count)(index + 1)(rounds)(sum + arityOf(Ashes.Collection.Map.getStr("C" + Ashes.Text.fromInt(index % count) + "b")(constructorsByName)) + arityOf(Ashes.Collection.Map.getStr("value" + Ashes.Text.fromInt(index))(constructorsByName)) + arityOf(Ashes.Collection.Map.getStr("p")(constructorsByName)) + arityOf(Ashes.Collection.Map.getStr("accumulator")(constructorsByName)))

let benchLookups (old: Bool) (count: Int) (rounds: Int) =
    match 0
    |> benchProgram(count)
    |> buildReachRegistry with
        | ReachRegistry { constructors = constructors, constructorsByName = constructorsByName } ->
            Ashes.IO.print("checksum " + Ashes.Text.fromInt(if old
            then oldLookups(constructors)(count)(0)(rounds)(0)
            else newLookups(constructorsByName)(count)(0)(rounds)(0)))

let recursive newPoisoned (summaries: List(ReachSummary)) (sum: Int) =
    match summaries with
        | [] -> sum
        | ReachSummary { reach = ResultReachState { counts = counts, isPoisoned = poisoned } } :: rest ->
            newPoisoned(rest)(sum + Ashes.Collection.List.length(counts) + (if poisoned
            then 100
            else 0))

let recursive oldPoisoned (summaries: List(RefReachSummary)) (sum: Int) =
    match summaries with
        | [] -> sum
        | RefReachSummary { reach = RefResultReachState { counts = counts, isPoisoned = poisoned } } :: rest ->
            oldPoisoned(rest)(sum + Ashes.Collection.List.length(counts) + (if poisoned
            then 100
            else 0))

let benchSummaries (old: Bool) (count: Int) (functions: Int) =
    Ashes.IO.print("checksum " + Ashes.Text.fromInt(if old
    then
        oldPoisoned(functions
        |> benchProgram(count)
        |> ref_programReachSummaries)(0)
    else
        newPoisoned(functions
        |> benchProgram(count)
        |> programReachSummaries)(0)))

let number (text: Str) =
    match Ashes.Text.parseInt(text) with
        | Ok(value) -> value
        | Error(_message) -> Ashes.IO.panic("expected a number")

match Ashes.IO.args with
    | "check" :: first :: last :: [] ->
        (0, 0, 0)
        |> checkSeeds(number(first))(number(last))
        |> report
    | "old-lookup" :: count :: rounds :: [] ->
        rounds
        |> number
        |> benchLookups(true)(number(count))
    | "new-lookup" :: count :: rounds :: [] ->
        rounds
        |> number
        |> benchLookups(false)(number(count))
    | "old-summaries" :: count :: functions :: [] ->
        functions
        |> number
        |> benchSummaries(true)(number(count))
    | "new-summaries" :: count :: functions :: [] ->
        functions
        |> number
        |> benchSummaries(false)(number(count))
    | _ -> Ashes.IO.panic("usage: reach-constructor-index check <first seed> <last seed> | old-lookup|new-lookup <types> <rounds> | old-summaries|new-summaries <types> <functions>")
