type Tree =
    | Leaf(Str)
    | Nested(Tree)
    | Many(List(Tree))

type Job =
    | value: Tree
    | path: List(Tree)

type WalkResult =
    | ok: Bool
    | known: List(Tree)

let resolve value environment =
    if environment == 0
    then value
    else Nested(value)

let recursive prepend values path pending =
    match values with
        | [] -> pending
        | value :: rest -> Job(value = value, path = path) :: prepend(rest)(path)(pending)

let recursive walk (pending: List(Job)) (visited: List(Tree)) (known: List(Tree)) (environment: Int) (flag: Bool) =
    match pending with
        | [] -> WalkResult(ok = true, known = known)
        | Job { value = value, path = path } :: rest ->
            let resolved = resolve(value)(environment)
            in
                match resolved with
                    | Leaf(_text) -> walk(rest)(visited)(known)(environment)(flag)
                    | Nested(child) -> walk(Job(value = child, path = path) :: rest)(visited)(known)(environment)(flag)
                    | Many(children) ->
                        walk(prepend(children)(resolved :: path)(rest))(visited)(known)(environment)(flag)

let recursive run n =
    if n == 0
    then Ashes.IO.print("done")
    else
        let result =
            walk([Job(value = Many([Many([n
            |> Ashes.Text.fromInt
            |> Leaf
            |> Nested])]), path = [])])([])([])(0)(false)
        in
            if result.ok
            then run(n - 1)
            else Ashes.IO.panic("bad result")

match Ashes.IO.args with
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("mode")
