// expect: 500
type St =
    | layouts: List(Str)
    | supply: Int

let register (name: Str) (state: St) =
    match state with
        | St { supply = supply } -> Ok((state with layouts = [name], supply = supply + 1))

let recursive rounds (n: Int) (state: St) =
    if n == 0
    then state.supply
    else
        match register("T" + Ashes.Text.fromInt(n))(state) with
            | Ok(next) -> rounds(n - 1)(next)
            | Error(_e) -> 0

St(layouts = [], supply = 0)
|> rounds(500)
|> Ashes.IO.print
