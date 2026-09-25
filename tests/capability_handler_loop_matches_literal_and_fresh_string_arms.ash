// expect: 8000

type FuzzMaybe(a) =
    | FuzzNone
    | FuzzSome(a)

capability FuzzCapability(a) =
    | get : Unit -> a

let recursive loop : Int -> Int -> Int needs e =
    given (n: Int) ->
        given (acc: Int) ->
            if n == 0
            then acc
            else
                let observed =
                    match let r : FuzzMaybe(Int) =
                        handle perform FuzzCapability.get(Unit) with
                            | FuzzCapability.get(_) -> resume(FuzzNone)
                            | return(x) -> x
                    in r with
                        | FuzzNone -> "FuzzNone"
                        | FuzzSome(v) -> "FuzzSome(" + Ashes.Text.fromInt(v) + ")"
                in loop(n - 1)(acc + Ashes.Text.byteLength(observed))
in
    0
    |> loop(1000)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
