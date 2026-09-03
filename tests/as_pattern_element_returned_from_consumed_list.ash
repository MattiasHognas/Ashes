// expect: 0:2/0 1:2/1 2:2/2
// An `as` alias names the whole element of a consumed list parameter; returning it from the lookup
// must retain it before the loop releases the list, or a later lookup reuses the freed cells.
type Facts =
    | factsBlock: Int
    | factsLoads: List(Int)

let recursive factsOf (blockIndex: Int) (facts: List(Facts)) =
    match facts with
        | [] -> Facts(factsBlock = blockIndex, factsLoads = [])
        | (Facts { factsBlock = candidate } as entry) :: rest ->
            if candidate == blockIndex
            then entry
            else factsOf(blockIndex)(rest)

let recursive buildFacts (index: Int) (count: Int) =
    if index >= count
    then []
    else Facts(factsBlock = index, factsLoads = [index, index + 1]) :: buildFacts(index + 1)(count)

let recursive count (items: List(Int)) =
    match items with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let recursive filler (n: Int) =
    if n == 0
    then []
    else Facts(factsBlock = n, factsLoads = [n]) :: filler(n - 1)

let recursive loop (remaining: List(Int)) (facts: List(Facts)) (acc: List(Str)) =
    match remaining with
        | [] -> acc
        | blockIndex :: rest ->
            match factsOf(blockIndex)(facts) with
                | Facts { factsLoads = loads } ->
                    match factsOf(blockIndex + 100)(facts) with
                        | Facts { factsBlock = missing } -> loop(rest)(facts)(Ashes.Text.fromInt(blockIndex) + ":" + Ashes.Text.fromInt(count(loads)) + "/" + Ashes.Text.fromInt(missing - 100) :: acc)

let recursive show (items: List(Str)) =
    match items with
        | [] -> ""
        | item :: rest ->
            match rest with
                | [] -> item
                | _ -> show(rest) + " " + item

Ashes.IO.print(show(loop([0, 1, 2])(buildFacts(0)(3))([])))
