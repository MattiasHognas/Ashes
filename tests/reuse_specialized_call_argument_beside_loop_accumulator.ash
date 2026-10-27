// expect: 72
// A worklist loop dequeues a key by updating its map through a reuse specialization, then hands
// the map to a helper returning a tuple. The specialization copies every argument but its
// accumulator, so the key is passed borrowed: a key handed over instead was released only at the
// loop's exit, one string per iteration.

let recursive enqueue (pending: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) =
    match pending with
        | [] -> (back, queued)
        | key :: rest -> enqueue(rest)(key :: back)(queued)

let recursive solve (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
    match front with
        | [] ->
            if fuel <= 0
            then Ashes.Collection.Map.size(queued)
            else
                solve(Ashes.Collection.List.reverse(back))([])(queued)(fuel - 1)
        | key :: rest ->
            match queued
            |> Ashes.Collection.Map.setStr(key)(false)
            |> enqueue([key + "x"])(back) with
                | (nextBack, nextQueued) -> solve(rest)(nextBack)(nextQueued)(fuel)

let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(keys)(n - 1)(total + solve(keys)([])(Ashes.Collection.Map.empty)(3))

0
|> rounds(["a", "b", "c", "d", "e", "f"])(3)
|> Ashes.IO.print
