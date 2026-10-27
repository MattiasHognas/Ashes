// expect: 200
let recursive appendList left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendList(tail)(right)

let recursive parentsFor parts =
    match parts with
        | [] -> []
        | _head :: tail -> ".." :: parentsFor(tail)

let rel (left: List(Str)) (right: List(Str)) =
    appendList(parentsFor(left))(right)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + Ashes.Collection.List.length(rel(["a", "b"])(["x", "d" + Ashes.Text.fromInt(n)])))

0
|> rounds(50)
|> Ashes.IO.print
