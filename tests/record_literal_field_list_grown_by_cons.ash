// expect: 27

type Item =
    | Named(Str)
    | Plain

type Labels =
    | labels: List(Str)
    | current: Maybe(Int)

type Seen =
    | seen: List(Item)
    | count: Int

let recursive label (items: List(Str)) (state: Labels) =
    match items with
        | [] -> Ashes.Collection.List.length(state.labels)
        | name :: rest -> label(rest)(Labels(labels = name :: state.labels, current = state.current))

let recursive collect (items: List(Item)) (state: Seen) =
    match items with
        | [] -> Ashes.Collection.List.length(state.seen) + state.count
        | item :: rest -> collect(rest)(Seen(seen = item :: rest, count = state.count + 1))

let words (n: Int) = ["coal" + Ashes.Text.fromInt(n), "ember" + Ashes.Text.fromInt(n)]

let items (n: Int) = [Named("coal" + Ashes.Text.fromInt(n)), Plain, Named("ember")]

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + label(words(n))(Labels(labels = ["flint"], current = None)) + collect(items(n))(Seen(seen = [], count = n)))

0
|> rounds(3)
|> Ashes.IO.print
