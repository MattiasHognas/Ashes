// expect: lit|n1| lit|n2| s|lit|n3|
capability Cap =
    | get : Unit -> Str

let direct (acc: List(Str)) = perform Cap.get(Unit) :: acc

let bound (acc: List(Str)) =
    (let x = perform Cap.get(Unit)
    in x :: acc)

let recursive joined (xs: List(Str)) (sep: Str) =
    match xs with
        | [] -> ""
        | x :: rest -> x + sep + joined(rest)(sep)

let plain (n: Int) =
    joined(handle direct(["n" + Ashes.Text.fromInt(n)]) with
        | Cap.get(u) -> resume("lit"))("|")

let viaLet (n: Int) =
    joined(handle bound(["n" + Ashes.Text.fromInt(n)]) with
        | Cap.get(u) -> resume("lit"))("|")

let posted (n: Int) =
    joined(handle direct(["n" + Ashes.Text.fromInt(n)]) with
        | Cap.get(u) ->
            let x = resume("lit")
            in "s" :: x
        | return(r) -> r)("|")

Ashes.IO.print(plain(1) + " " + viaLet(2) + " " + posted(3))
