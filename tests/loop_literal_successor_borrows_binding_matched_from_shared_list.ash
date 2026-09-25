// A record literal successor holds `Some(span)`, where `span` is matched out of a list the loop
// walks in place and every round shares: the literal only borrows it, so the back edge releases the
// fields the literal retained and leaves the span to the list that owns it.
// expect: 240
type Span =
    | start: Int
    | length: Int

type Item =
    | At(Span, Item)
    | Decl(Int)
    | Let(Int)

type FunctionGroup =
    | declarationSpan: Maybe(Span)
    | names: List(Str)

type State =
    | instructions: List(Int)
    | currentSpan: Maybe(Span)
    | functionGroup: FunctionGroup
    | counter: Int

let recursive countInts (items: List(Int)) (total: Int) =
    match items with
        | [] -> total
        | _ :: tail -> countInts(tail)(total + 1)

let recursive lowerItems (items: List(Item)) (body: List(Str)) (seen: List(Str)) (state: State) =
    match items with
        | [] -> state.counter + countInts(state.instructions)(0)
        | At(span, inner) :: rest -> State(instructions = state.instructions, currentSpan = Some(span), functionGroup = state.functionGroup, counter = state.counter) |> lowerItems(inner :: rest)(body)(seen)
        | Decl(_value) :: rest -> lowerItems(rest)(body)(seen)(state)
        | Let(value) :: rest -> lowerItems(rest)(body)(seen)((state with instructions = value :: state.instructions, counter = state.counter + 1))

let recursive items (n: Int) (built: List(Item)) =
    if n == 0
    then built
    else items(n - 1)(At(Span(start = n, length = 1))(Decl(n)) :: At(Span(start = n, length = 2))(Let(n)) :: built)

let program = items(40)([])

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + lowerItems(program)(["body"])(["seen"])(State(instructions = [], currentSpan = None, functionGroup = FunctionGroup(declarationSpan = None, names = ["a"]), counter = 0)))

0
|> rounds(3)
|> Ashes.IO.print
