// expect: 18
let recursive enqueue (pending: List(Str)) (back: List(Str)) =
    match pending with
        | [] -> back
        | key :: rest -> enqueue(rest)(key :: back)

let next (key: Str) =
    if key == "cinder"
    then Some("smoke")
    else
        if key == "smoke"
        then Some("soot")
        else
            if key == "soot"
            then Some("cinder")
            else None

let recursive solve (count: Int) (front: List(Str)) (back: List(Str)) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> count
                | _ ->
                    solve(count)(Ashes.Collection.List.reverse(back))([])(fuel)
        | key :: rest ->
            if fuel <= 0
            then count
            else
                match next(key) with
                    | None -> solve(count)(rest)(back)(fuel - 1)
                    | Some(target) ->
                        solve(count + 1)(rest)(enqueue([target])(back))(fuel - 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + solve(0)(["cinder", "ember"])([])(7))

0
|> rounds(3)
|> Ashes.IO.print
