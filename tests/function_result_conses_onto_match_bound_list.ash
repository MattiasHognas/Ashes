// expect: 2,2!,s;x,2!,t 1,1!,s;x,1!,t
// A function returns a cell consed onto a list a match bound from a call's result, through a let
// and through a lambda applied in a pipeline.
type Context =
    | name: Str
    | count: Int

let contextOf (n: Int) = Context(name = Ashes.Text.fromInt(n) + "!", count = n)

let stage (c: Context) (state: List(Str)) = c.name :: state

let viaLet (n: Int) (state: List(Str)) =
    (let c = contextOf(n)
    in
        match stage(c)(state) with
            | applied -> Ashes.Text.fromInt(c.count) :: applied)

let viaLambda (n: Int) (state: List(Str)) =
    n
    |> contextOf
    |> (given (c: Context) ->
        match stage(c)(state) with
            | applied -> "x" :: applied)

let probe (n: Int) =
    Ashes.Text.join(",")(viaLet(n)(["s"])) + ";" + Ashes.Text.join(",")(viaLambda(n)(["t"]))

let recursive loop (n: Int) (acc: Str) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n) + " ")

""
|> loop(2)
|> Ashes.IO.print
