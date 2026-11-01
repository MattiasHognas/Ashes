// expect: 22
capability Tick =
    | get : Unit -> Int

let probe (n: Int) =
    handle Ashes.Text.fromInt(perform Tick.get(Unit)) with
        | Tick.get(_) ->
            let k = n + 1
            in
                let x = resume(k)
                in x + "s"
        | return(r) -> r

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else
        loop(n - 1)(acc + Ashes.Text.byteLength(probe(n)))

0
|> loop(10)
|> Ashes.IO.print
