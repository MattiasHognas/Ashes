// expect: 12
external probe_abs(Int) -> Int = "abs@libc.so.6"

let recursive lookupIndexed key env =
    match env with
        | [] -> Ashes.IO.panic("missing")
        | (k, v) :: rest ->
            if k == key
            then v
            else lookupIndexed(key)(rest)

let recursive resolveAll keys env =
    match keys with
        | [] -> []
        | key :: rest -> lookupIndexed(key)(env) :: resolveAll(rest)(env)

let recursive sumAbs values =
    match values with
        | [] -> 0
        | v :: rest -> probe_abs(v) + sumAbs(rest)

let emit keys env = sumAbs(resolveAll(keys)(env))

let recursive sumAbsOf keys env =
    match keys with
        | [] -> 0
        | key :: rest -> probe_abs(lookupIndexed(key)(env)) + sumAbsOf(rest)(env)

Ashes.IO.print(if emit([1, 2])([(1, -5), (2, 7)]) == sumAbsOf([1, 2])([(1, -5), (2, 7)])
then emit([1, 2])([(1, -5), (2, 7)])
else 0)
