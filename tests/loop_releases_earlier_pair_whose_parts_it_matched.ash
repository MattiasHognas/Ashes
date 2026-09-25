// expect: 2400
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

let recursive countItems (values: List(Str)) (total: Int) =
    match values with
        | [] -> total
        | _ :: rest -> countItems(rest)(total + 1)

let enqueue (key: Str) (back: List(Str)) (seen: Seen) = (key :: back, insert(key)(seen))

let recursive solve (front: List(Str)) (back: List(Str)) (seen: Seen) =
    match front with
        | [] -> countSeen(seen) + countItems(back)(0)
        | key :: rest ->
            match enqueue(key + "x")(back)(seen) with
                | (nextBack, nextSeen) -> solve(rest)(nextBack)(nextSeen)

let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(keys)(n - 1)(total + solve(keys)([])(Leaf))

0
|> rounds(["a", "b", "c", "d", "e", "f"])(200)
|> Ashes.IO.print
