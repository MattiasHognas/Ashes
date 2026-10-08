// A loop over items threads a list of pending closes and a state record. One arm conses a helper's
// result onto the closes, another replaces them with a part matched out of a helper's result: both
// are accumulator edges, so the closes live on the reference-counted heap and each round's cells
// are released.
// expect: 81
type Item =
    | At(Int, Item)
    | Plain(Str)
    | Grouped(Str)

type Binding =
    | name: Str
    | slot: Int

type State =
    | bindings: List(Binding)
    | labels: List(Str)
    | counter: Int

type Close =
    | closeName: Maybe(Str)
    | closeBindings: List(Binding)

type Opened =
    | openedState: State
    | openedPending: List(Close)

let withSpan (span: Int) (state: State) = state with counter = state.counter + span

let open (name: Str) (pending: List(Close)) (state: State) = Opened(openedState = (state with bindings = Binding(name = name, slot = state.counter) :: state.bindings), openedPending = Close(closeName = Some(name), closeBindings = state.bindings) :: pending)

let openGroup (name: Str) (state: State) = state with labels = name :: state.labels

let restore (bindings: List(Binding)) = Close(closeName = None, closeBindings = bindings)

let recursive countCloses (closes: List(Close)) (total: Int) =
    match closes with
        | [] -> total
        | _ :: rest -> countCloses(rest)(total + 1)

let recursive lowerItems (items: List(Item)) (pending: List(Close)) (state: State) =
    match items with
        | [] -> state.counter + countCloses(pending)(0)
        | At(span, inner) :: rest ->
            state
            |> withSpan(span)
            |> lowerItems(inner :: rest)(pending)
        | Plain(name) :: rest ->
            match open(name)(pending)(state) with
                | Opened { openedState = opened, openedPending = nextPending } -> lowerItems(rest)(nextPending)(opened)
        | Grouped(name) :: rest ->
            state
            |> openGroup(name)
            |> lowerItems(rest)(restore(state.bindings) :: pending)

let program = [At(1)(Plain("a")), At(2)(Grouped("b")), At(3)(Plain("c")), At(4)(Plain("d")), At(5)(Grouped("e")), At(6)(Plain("f"))]

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + lowerItems(program)([])(State(bindings = [], labels = [], counter = 0)))

0
|> rounds(3)
|> Ashes.IO.print
