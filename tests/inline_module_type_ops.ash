// expect: 2
module Stack =
    type Stack =
        | Empty
        | Push(Int, Stack)
    let empty = Empty
    let push = given (x) -> given (s) -> Push(x, s)
    let recursive depth = given (s) ->
        match s with
            | Empty -> 0
            | Push(_x, rest) -> 1 + depth(rest)

let s = Stack.push(1)(Stack.push(2)(Stack.empty))

Ashes.IO.print(Stack.depth(s))
