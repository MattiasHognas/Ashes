// A worklist whose back queue is grown by a cons of the front's head and emptied when it is
// reversed into the front. The back queue lives on the reference-counted heap, so each step's cells
// are released once the front walks past them.
// expect: 45
let recursive reverseAll (acc: List(Str)) (items: List(Str)) =
    match items with
        | [] -> acc
        | head :: tail -> reverseAll(head :: acc)(tail)

let recursive solve (front: List(Str)) (back: List(Str)) (queued: Int) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> queued
                | _ ->
                    solve(reverseAll([])(back))([])(queued)(fuel)
        | key :: rest ->
            if fuel <= 0
            then queued
            else solve(rest)(key :: back)(queued + 1)(fuel - 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + solve(["cinder", "ember"])([])(0)(7) + 8)

0
|> rounds(3)
|> Ashes.IO.print
