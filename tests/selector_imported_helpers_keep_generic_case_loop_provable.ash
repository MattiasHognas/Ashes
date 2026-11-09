// expect: 6
// A generic loop over match cases expands each pattern through helpers that call the selector-imported
// append and reverse. The ownership analysis resolves those imports to the shipped functions, so the
// loop's taken-over record is proven and every expanded pattern is released.
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
type TextSpan =
    | start: Int
    | end: Int

type Pattern =
    | PatternAt(TextSpan, Pattern)
    | PatternEmptyList
    | PatternVar(Str)
    | PatternWildcard
    | PatternCons(Pattern, Pattern)
    | PatternTuple(List(Pattern))
    | PatternConstructor(Str, List(Pattern))
    | PatternRecord(Str, List((Str, Pattern)))
    | PatternAs(Pattern, Str)
    | PatternOr(List(Pattern))
    | PatternInt(Int)
    | PatternString(Str)
    | PatternRune(Int)
    | PatternBool(Bool)

type ConstructorInferenceDefinition =
    | name: Str
    | fieldNames: List(Str)

let recursive resolveConstructorBinding (name: Str) (environment: List(ConstructorInferenceDefinition)) =
    match environment with
        | [] -> None
        | definition :: rest ->
            if definition.name == name
            then Some(definition)
            else resolveConstructorBinding(name)(rest)

let recursive coverageLength items (acc: Int) =
    match items with
        | [] -> acc
        | _ :: rest -> coverageLength(rest)(acc + 1)

let recursive coverageWildcards (count: Int) acc =
    if count <= 0
    then acc
    else coverageWildcards(count - 1)(PatternWildcard :: acc)

let recursive coverageReplaceAt patterns (index: Int) replacement acc =
    match patterns with
        | [] -> reverse(acc)
        | pattern :: rest ->
            if index == 0
            then coverageReplaceAt(rest)(index - 1)(replacement)(replacement :: acc)
            else coverageReplaceAt(rest)(index - 1)(replacement)(pattern :: acc)

let recursive coverageFieldIndex (fieldName: Str) (fieldNames: List(Str)) (index: Int) =
    match fieldNames with
        | [] -> None
        | candidate :: rest ->
            if candidate == fieldName
            then Some(index)
            else coverageFieldIndex(fieldName)(rest)(index + 1)

let recursive coveragePlaceRecordFields (fields: List((Str, Pattern))) (fieldNames: List(Str)) positional =
    match fields with
        | [] -> positional
        | (fieldName, fieldPattern) :: rest ->
            match coverageFieldIndex(fieldName)(fieldNames)(0) with
                | Some(index) ->
                    []
                    |> coverageReplaceAt(positional)(index)(fieldPattern)
                    |> coveragePlaceRecordFields(rest)(fieldNames)
                | None -> coveragePlaceRecordFields(rest)(fieldNames)(positional)

let recursive coverageExpandPattern pattern environment =
    match pattern with
        | PatternAt(_span, inner) -> coverageExpandPattern(inner)(environment)
        | PatternOr(alternatives) -> coverageExpandAlternatives(alternatives)(environment)([])
        | PatternAs(inner, _name) -> coverageExpandPattern(inner)(environment)
        | PatternCons(head, tail) ->
            coverageConsCombinations(coverageCombineChildren([head, tail])(environment))([])
        | PatternTuple(elements) ->
            coverageTupleCombinations(coverageCombineChildren(elements)(environment))([])
        | PatternConstructor(name, patterns) ->
            coverageConstructorCombinations(name)(coverageCombineChildren(patterns)(environment))([])
        | PatternRecord(name, fields) ->
            match resolveConstructorBinding(name)(environment) with
                | Some(ConstructorInferenceDefinition { fieldNames = fieldNames }) ->
                    match fieldNames with
                        | [] -> [pattern]
                        | _ ->
                            let positional =
                                []
                                |> coverageWildcards(coverageLength(fieldNames)(0))
                                |> coveragePlaceRecordFields(fields)(fieldNames)
                            in
                                coverageConstructorCombinations(name)(coverageCombineChildren(positional)(environment))([])
                | None -> [pattern]
        | other -> [other]
and coverageExpandAlternatives alternatives environment acc =
    match alternatives with
        | [] -> acc
        | alternative :: rest ->
            environment
            |> coverageExpandPattern(alternative)
            |> append(acc)
            |> coverageExpandAlternatives(rest)(environment)
and coverageCombineChildren children environment =
    match children with
        | [] -> [[]]
        | child :: rest ->
            coverageProduct(coverageExpandPattern(child)(environment))(coverageCombineChildren(rest)(environment))([])
and coverageProduct heads (tails: List(List(Pattern))) acc =
    match heads with
        | [] -> acc
        | head :: rest ->
            []
            |> coveragePrefixAll(head)(tails)
            |> append(acc)
            |> coverageProduct(rest)(tails)
and coveragePrefixAll head (tails: List(List(Pattern))) acc =
    match tails with
        | [] -> reverse(acc)
        | tail :: rest -> coveragePrefixAll(head)(rest)((head :: tail) :: acc)
and coverageConsCombinations (combinations: List(List(Pattern))) acc =
    match combinations with
        | [] -> reverse(acc)
        | (head :: tail :: []) :: rest -> coverageConsCombinations(rest)(PatternCons(head)(tail) :: acc)
        | _ :: rest -> coverageConsCombinations(rest)(acc)
and coverageTupleCombinations (combinations: List(List(Pattern))) acc =
    match combinations with
        | [] -> reverse(acc)
        | elements :: rest -> coverageTupleCombinations(rest)(PatternTuple(elements) :: acc)
and coverageConstructorCombinations (name: Str) (combinations: List(List(Pattern))) acc =
    match combinations with
        | [] -> reverse(acc)
        | arguments :: rest -> coverageConstructorCombinations(name)(rest)(PatternConstructor(name)(arguments) :: acc)

let recursive coverageExpandCases cases environment acc =
    match cases with
        | [] -> reverse(acc)
        | (pattern, body, guard) :: rest ->
            acc
            |> coverageExpandedArms(coverageExpandPattern(pattern)(environment))(body)(guard)
            |> coverageExpandCases(rest)(environment)
and coverageExpandedArms patterns body guard acc =
    match patterns with
        | [] -> acc
        | pattern :: rest -> coverageExpandedArms(rest)(body)(guard)((pattern, body, guard) :: acc)

let probe (n: Int) =
    (let environment = [ConstructorInferenceDefinition(name = "Item", fieldNames = ["name", "weight"])]
    in
        let cases =
            [(PatternEmptyList, n, None), (PatternCons(PatternRecord("Item")([("name", PatternVar("item" + Ashes.Text.fromInt(n % 3)))]))(PatternVar("rest")), n + 1, None)]
        in
            []
            |> coverageExpandCases(cases)(environment)
            |> Ashes.Collection.List.length)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.Text.fromInt
|> Ashes.IO.print
