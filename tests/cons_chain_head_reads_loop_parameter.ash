// expect: c,b,a|b,c,a,b,x,a|5
let recursive interleave (separator: Str) (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | first :: rest -> interleave(separator)(first :: separator :: acc)(rest)

let recursive pairs (previous: Str) (acc: List(Str)) (ps: List(Str)) =
    match ps with
        | [] -> acc
        | first :: rest -> pairs(first)(previous :: first :: acc)(rest)

let recursive drop (acc: List(Str)) =
    match acc with
        | [] -> []
        | _ :: rest -> rest

let parts = ["a", "b", "c"]

let interleaved =
    parts
    |> interleave(",")([])
    |> Ashes.Collection.List.reverse
    |> drop

Ashes.IO.print(Ashes.Text.join("")(Ashes.Collection.List.reverse(interleaved)) + "|" + Ashes.Text.join(",")(pairs("x")([])(parts)) + "|" + Ashes.Text.fromInt(Ashes.Collection.List.length(interleaved)))
