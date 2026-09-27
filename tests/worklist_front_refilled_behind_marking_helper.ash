// expect: 30
type Seen =
    | Leaf
    | Node(Seen, Str, Seen)

let recursive insert (key: Str) (seen: Seen) =
    match seen with
        | Leaf -> Node(Leaf)(key)(Leaf)
        | Node(left, current, right) ->
            Node(insert(key)(left))(current)(right)

let recursive countSeen (seen: Seen) =
    match seen with
        | Leaf -> 0
        | Node(left, _current, right) -> countSeen(left) + 1 + countSeen(right)

let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
    match values with
        | [] -> acc
        | value :: rest -> reverseAll(rest)(value :: acc)

let mark (key: Str) = key + "x"

let enqueue (key: Str) (back: List(Str)) (seen: Seen) = (key :: back, insert(key)(seen))

let recursive solve (front: List(Str)) (back: List(Str)) (seen: Seen) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> countSeen(seen)
                | _ ->
                    solve(reverseAll(back)([]))([])(seen)(fuel)
        | key :: rest ->
            if fuel <= 0
            then countSeen(seen)
            else
                match enqueue(mark(key))(back)(seen) with
                    | (nextBack, nextSeen) -> solve(rest)(nextBack)(nextSeen)(fuel - 1)

let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(keys)(n - 1)(total + solve(keys)([])(Leaf)(10))

0
|> rounds(["a", "b", "c"])(3)
|> Ashes.IO.print
