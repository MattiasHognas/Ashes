// expect: 41337792
// A fresh string consumed by a callee whose result is a task awaited inside an async body: the
// callee's result reach is unknown through the await, and the argument is neither released
// while the result still holds it nor read back from reused memory. The scratch string
// allocated after each await would take over a freed cell.

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
            |> fill(64)) with
                | Error(_message) -> -1
                | Ok(kept) ->
                    let scratch =
                        n + 1
                        |> Ashes.Text.fromInt
                        |> fill(64)
                    in loop(n - 1)(total + Ashes.Text.byteLength(kept) + Ashes.Text.byteLength(scratch))
    in loop(40000)(0))

match Ashes.Task.run(compute) with
    | Ok(n) ->
        n
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    | Error(_e) -> Ashes.IO.print("err")
