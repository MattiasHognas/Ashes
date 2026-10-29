// expect: 36
// A worklist whose front repeats a key: the solver dequeues each key by rewriting its map through
// a reuse specialization, then enqueues a derived key unless the map already holds it. The map's
// new nodes take copies of their keys, so a key passed borrowed into the specialization is never
// left behind in the map once the loop releases it.
let recursive enqueue (pending: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) =
    match pending with
        | [] -> (back, queued)
        | key :: rest ->
            match Ashes.Collection.Map.getStr(key)(queued) with
                | Some(true) -> enqueue(rest)(back)(queued)
                | _ ->
                    queued
                    |> Ashes.Collection.Map.setStr(key)(true)
                    |> enqueue(rest)(key :: back)

let recursive solve (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> Ashes.Collection.Map.size(queued)
                | _ ->
                    solve(Ashes.Collection.List.reverse(back))([])(queued)(fuel)
        | key :: rest ->
            if fuel <= 0
            then Ashes.Collection.Map.size(queued)
            else
                let dequeued = Ashes.Collection.Map.setStr(key)(false)(queued)
                in
                    if fuel % 3 == 0
                    then solve(rest)(back)(dequeued)(fuel - 1)
                    else
                        match enqueue([key + "cinder"])(back)(dequeued) with
                            | (nextBack, nextQueued) -> solve(rest)(nextBack)(nextQueued)(fuel - 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + solve(["ember", "smoke", "soot", "cinder", "ember"])([])(Ashes.Collection.Map.empty)(12))

0
|> rounds(3)
|> Ashes.IO.print
