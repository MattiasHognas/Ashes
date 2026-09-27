// expect: 60
type Seen =
    | Leaf
    | Node(Seen, Str, Seen)

let recursive insert (key: Str) (seen: Seen) =
    match seen with
        | Leaf -> Node(Leaf)(key)(Leaf)
        | Node(left, current, right) ->
            Node(insert(key)(left))(current)(right)

let recursive countSeen : Seen -> Int =
    given (seen: Seen) ->
        match seen with
            | Leaf -> 0
            | Node(left, _current, right) -> countSeen(left) + 1 + countSeen(right)

let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
    match values with
        | [] -> acc
        | value :: rest -> reverseAll(rest)(value :: acc)

let recursive countAll (values: List(Str)) (total: Int) =
    match values with
        | [] -> total
        | _value :: rest -> countAll(rest)(total + 1)

let enqueue (key: Str) (back: List(Str)) (seen: Seen) = (key :: back, insert(key)(seen))

let recursive solve (front: List(Str)) (back: List(Str)) (seen: Seen) (visited: List(Str)) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> countSeen(seen) + countAll(visited)(0)
                | _ ->
                    solve(reverseAll(back)([]))([])(seen)(visited)(fuel)
        | key :: rest ->
            if fuel <= 0
            then countSeen(seen) + countAll(visited)(0)
            else
                match enqueue(key + "x")(back)(seen) with
                    | (nextBack, nextSeen) -> solve(rest)(nextBack)(nextSeen)(key + "!" :: visited)(fuel - 1)

let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(keys)(n - 1)(total + solve(keys)([])(Leaf)([])(10))

0
|> rounds(["a", "b", "c"])(3)
|> Ashes.IO.print
