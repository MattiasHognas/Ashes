// expect: 152
type St =
    | instructions: List(Int)
    | names: List(Str)

let recursive recordAndList (n: Int) (pair: (St, List(Str))) =
    match pair with
        | (state, extra) ->
            if n == 0
            then state
            else recordAndList(n - 1)((St(instructions = n :: state.instructions, names = extra), state.names))

let recursive updateAndList (n: Int) (pair: (St, List(Str))) =
    match pair with
        | (state, extra) ->
            if n == 0
            then state
            else updateAndList(n - 1)(((state with instructions = n :: state.instructions, names = extra), state.names))

let recursive passThrough (n: Int) (pair: (List(Int), List(Str))) =
    match pair with
        | (xs, extra) ->
            if n == 0
            then Ashes.Collection.List.length(xs) + Ashes.Collection.List.length(extra)
            else passThrough(n - 1)((xs, extra))

let recursive reverse (pair: (List(Str), List(Str))) =
    match pair with
        | ([], acc) -> acc
        | (h :: t, acc) -> reverse((t, h :: acc))

let measure (state: St) =
    match state with
        | St { instructions = is, names = names } -> Ashes.Collection.List.length(is) + Ashes.Collection.List.length(names)

let run (seed: Int) =
    measure(recordAndList(4)((St(instructions = [seed], names = ["a"]), ["b", "c"]))) + measure(updateAndList(4)((St(instructions = [seed], names = ["a"]), ["b", "c"]))) + passThrough(3)(([seed, 2], ["b"])) + Ashes.Collection.List.length(reverse((["x", "y"], ["b", "c"])))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

0
|> rounds(8)
|> Ashes.IO.print
