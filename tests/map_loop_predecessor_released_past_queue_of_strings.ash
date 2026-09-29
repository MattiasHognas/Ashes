// A worklist loop whose back queue of strings is handed on unchanged at one self call while the
// map of queued keys is rebuilt. A queue of strings holds nothing of an earlier map but strings,
// and every string it holds it retains, so the back edge releases the earlier map.
// expect: 54
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
                    if fuel % 4 == 0
                    then solve(rest)(back)(dequeued)(fuel - 1)
                    else
                        match enqueue([key + "coal"])(back)(dequeued) with
                            | (nextBack, nextQueued) -> solve(rest)(nextBack)(nextQueued)(fuel - 1)

let run (seed: Int) = solve(["spark", "ember", "cinder", "coal"])([])(Ashes.Collection.Map.empty)(7) + seed

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(8))

0
|> rounds(3)
|> Ashes.IO.print
