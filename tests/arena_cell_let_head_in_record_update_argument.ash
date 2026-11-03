// expect: U3,U3,U3
// A record update passed down a pipeline conses a let-bound string onto an arena list. The cell
// stores an arena copy and drops its retain at once, and the call releases it no second time.
type Body =
    | inProgress: List(Str)
    | next: Int

let mark (key: Str) (body: Body) = body with next = body.next + Ashes.Text.byteLength(key)

let finish (body: Body) = body with next = body.next + 1

let label (body: Body) = Body(inProgress = ["U" + Ashes.Text.fromInt(body.next)], next = body.next)

let fmt (n: Int) = "T" + Ashes.Text.fromInt(n)

let synth (n: Int) (body: Body) =
    (let key = fmt(n)
    in
        match mark(key)(body) with
            | registered ->
                (registered with inProgress = key :: registered.inProgress)
                |> finish
                |> label)

let recursive loop (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else
        match synth(n)(Body(inProgress = ["a"], next = 0)) with
            | out ->
                acc
                |> Ashes.Collection.List.append(out.inProgress)
                |> loop(n - 1)

3
|> loop
|> (given (start) -> start([]))
|> Ashes.Text.join(",")
|> Ashes.IO.print
