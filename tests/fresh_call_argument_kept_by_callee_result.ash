// expect: 22
import Ashes.Collection.List
type Span =
    | start: Int
    | end: Int

type Node =
    | Leaf(Int)
    | Branch(Str, Node, List(Str), Int)
    | Wrapped(Span, Node)

let names count =
    (let recursive go acc n =
        if n == 0
        then acc
        else go("p" + Ashes.Text.fromInt(n) :: acc)(n - 1)
    in go([])(count))

let build start flag name value params tag =
    (let node =
        if flag
        then Branch(name)(value)(params)(tag)
        else Wrapped(start)(value)
    in Wrapped(start)(node))

let spanOf node =
    match node with
        | Wrapped(span, _) -> span
        | _ -> Span(start = 0, end = 0)

let runtimeCount = 2 + Ashes.Collection.List.length(Ashes.IO.args)

let built = build(Span(start = 10, end = 32))(true)("go")(Leaf(7))(names(runtimeCount))(3)

let outer = spanOf(built)

let again = spanOf(Wrapped(Span(start = 1, end = 32))(Leaf(0)))

match built with
    | Wrapped(_, Branch(_, _, params, _)) -> Ashes.IO.print(Ashes.Collection.List.length(params) * 10 + outer.end - again.end + 2)
    | _ -> Ashes.IO.print(-1)
