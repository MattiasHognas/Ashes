import Support
export (
    value run,
)

type Context =
    | zero: Int
    | name: Str

let lookupTemp (cx: Context) key tempEnv =
    match findIndexed(key)(tempEnv) with
        | Some(value) -> value
        | None ->
            match cx.zero with
                | zero -> zero

let recursive sum cx keys env total =
    match keys with
        | [] -> total
        | key :: rest -> sum(cx)(rest)(env)(total + lookupTemp(cx)(key)(env))

let run unit = sum(Context(zero = 0, name = "n"))([1, 2, 3])([(1, 10), (2, 20)])(0)
