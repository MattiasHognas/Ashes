// A loop hands on a map through a let binding of the call that built it. The binding is that
// owned result itself, so the back edge releases the previous iteration's map as it does when the
// call is written in the argument's place.
// expect: 45
let recursive solve (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> queued
                | _ ->
                    solve(Ashes.Collection.List.reverse(back))([])(queued)(fuel)
        | key :: rest ->
            if fuel <= 0
            then queued
            else
                let dequeued = Ashes.Collection.Map.setStr(key)(false)(queued)
                in solve(rest)(key + "x" :: back)(dequeued)(fuel - 1)

let run (seed: Int) =
    Ashes.Collection.Map.size(solve(["cinder", "ember"])([])(Ashes.Collection.Map.empty)(7)) + seed

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(8))

0
|> rounds(3)
|> Ashes.IO.print
