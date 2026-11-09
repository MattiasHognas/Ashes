// expect: 15
type Stage =
    | copied: Bool
    | general: Bool
    | flag: Int
    | normalized: Bool

let makeGuard (stage: Stage) (n: Int) =
    if n > 100
    then (true, -1)
    else
        if stage.general
        then (n > 1, -1)
        else (n > 2, stage.flag)

let copiedSevers (stage: Stage) (guard: (Bool, Int)) =
    match guard with
        | (false, _flagTemp) ->
            if stage.copied
            then
                (true, if stage.general
                then -1
                else stage.flag)
            else guard
        | _ -> guard

let normalizedSevers (stage: Stage) (guard: (Bool, Int)) =
    if stage.normalized
    then (true, -1)
    else guard

let probe (n: Int) =
    (let stage = Stage(copied = n > 5, general = n > 3, flag = n, normalized = false)
    in
        match n
        |> makeGuard(stage)
        |> copiedSevers(stage)
        |> normalizedSevers(stage) with
            | (severs, flag) ->
                if severs
                then flag + 2
                else flag)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.IO.print
