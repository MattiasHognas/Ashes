// expect: 1010

capability Clock =
    | now : Unit -> Int

provide Clock =
    | now =
        given (_) -> 1

type Shape =
    | Fn(Shape, Shape)
    | Named(Int, Str, List(Shape))
    | Var(Int)

let recursive resultType (shape: Shape) =
    match shape with
        | Fn(_parameter, result) -> resultType(result)
        | other -> other

let isFree (body: Shape) =
    match resultType(body) with
        | Named(_id, _name, []) -> 100
        | _ -> 0

let recursive rounds (item: Shape) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(item)(n - 1)(total + isFree(item) + perform Clock.now(Unit))

0
|> rounds([]
|> Named(0)("Maybe")
|> Fn(Var(1)))(10)
|> Ashes.IO.print
