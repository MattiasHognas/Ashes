// expect: 6
type Pat =
    | PAt(Int, Pat)
    | PEmpty
    | PVar(Str)
    | PWild
    | PCons(Pat, Pat)
    | POr(List(Pat))

let recursive consCombinations (combinations: List(List(Pat))) acc =
    match combinations with
        | [] -> Ashes.Collection.List.reverse(acc)
        | (head :: tail :: []) :: rest -> consCombinations(rest)(PCons(head)(tail) :: acc)
        | _ :: rest -> consCombinations(rest)(acc)

let recursive prefixAll head (tails: List(List(Pat))) acc =
    match tails with
        | [] -> acc
        | t :: rest -> prefixAll(head)(rest)((head :: t) :: acc)

let recursive expand pattern environment =
    match pattern with
        | PAt(_span, inner) -> expand(inner)(environment)
        | POr(alternatives) -> expandAlternatives(alternatives)(environment)([])
        | PCons(head, tail) ->
            consCombinations(combineChildren([head, tail])(environment))([])
        | other -> [other]
and expandAlternatives alternatives environment acc =
    match alternatives with
        | [] -> acc
        | alternative :: rest ->
            environment
            |> expand(alternative)
            |> Ashes.Collection.List.append(acc)
            |> expandAlternatives(rest)(environment)
and combineChildren children environment =
    match children with
        | [] -> [[]]
        | child :: rest ->
            product(expand(child)(environment))(combineChildren(rest)(environment))([])
and product heads (tails: List(List(Pat))) acc =
    match heads with
        | [] -> acc
        | head :: rest ->
            []
            |> prefixAll(head)(tails)
            |> Ashes.Collection.List.append(acc)
            |> product(rest)(tails)

let recursive expandCases cases environment acc =
    match cases with
        | [] -> Ashes.Collection.List.reverse(acc)
        | (pattern, body, guard) :: rest ->
            acc
            |> expandedArms(expand(pattern)(environment))(body)(guard)
            |> expandCases(rest)(environment)
and expandedArms patterns body guard acc =
    match patterns with
        | [] -> acc
        | pattern :: rest -> expandedArms(rest)(body)(guard)((pattern, body, guard) :: acc)

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let probe (n: Int) =
    []
    |> expandCases([(PAt(n)(PEmpty), 1, None), (PVar("tail")
    |> PCons(PVar("head"))
    |> PAt(n), 2, Some(n))])(["env"])
    |> count

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(3)
|> Ashes.IO.print
