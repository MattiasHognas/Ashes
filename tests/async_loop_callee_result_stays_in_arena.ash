// expect: 171
// A loop inside an async body awaits a task built from a callee's result. The callee may run
// inside a coroutine, so it keeps its values in the arena as the coroutine body does, and nothing
// the loop hands across an await is a reference-counted value left for the function's exit.
let echo (s: Str) = async(s)

let recursive fill (n: Int) (acc: Str) =
    if n == 0
    then acc
    else fill(n - 1)(acc + "abcdefgh")

let compute =
    async(let recursive loop (n: Int) (total: Int) =
        if n == 0
        then total
        else
            match await echo(n
            |> Ashes.Text.fromInt
            |> fill(2)) with
                | Error(_message) -> -1
                | Ok(kept) -> loop(n - 1)(total + Ashes.Text.byteLength(kept))
    in loop(10)(0))

match Ashes.Task.run(compute) with
    | Ok(n) ->
        n
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    | Error(_e) -> Ashes.IO.print("err")
