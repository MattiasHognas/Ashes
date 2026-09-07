// expect: 200020000 188898 item 1;item 2;item 3;|seed;item 20000;item 19999;
// A whole record loop parameter consed into the sibling list accumulator at the tail self-call
// is retained for the cell: the back edge releases the old parameter after copying its
// successor out of the arena, so an unretained record would be read back from freed memory once
// the next iteration's allocations reuse it. Every label survives, in both traversal orders.

type State =
    | count: Int
    | label: Str

let recursive collect (n: Int) (s: State) (acc: List(State)) =
    if n == 0
    then s :: acc
    else collect(n - 1)(State(count = n, label = "item " + Ashes.Text.fromInt(n)))(s :: acc)

let recursive total (items: List(State)) (sum: Int) (labels: Int) =
    match items with
        | [] -> (sum, labels)
        | State { count = c, label = l } :: rest -> total(rest)(sum + c)(labels + Ashes.Text.byteLength(l))

let recursive firstLabels (items: List(State)) (k: Int) =
    match items with
        | [] -> ""
        | State { label = l } :: rest ->
            if k == 0
            then ""
            else l + ";" + firstLabels(rest)(k - 1)

let recursive reversed (items: List(State)) (acc: List(State)) =
    match items with
        | [] -> acc
        | item :: rest -> reversed(rest)(item :: acc)

let collected = collect(20000)(State(count = 10000, label = "seed"))([])

match total(collected)(0)(0) with
    | (sum, labels) ->
        Ashes.IO.print(Ashes.Text.fromInt(sum) + " " + Ashes.Text.fromInt(labels) + " " + firstLabels(collected)(3) + "|" + firstLabels(reversed(collected)([]))(3))
