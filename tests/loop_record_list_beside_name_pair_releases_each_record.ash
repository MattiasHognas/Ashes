// expect: 880
// A loop over declarations pushes a restore record per recursive declaration onto a list of
// pending closes, beside a pair naming the last declaration. The pair can hold the record's
// strings, but a string a successor keeps holds a reference of its own, so each iteration's record
// is released at the back edge once the normalized list holds its own reference.
type Binding =
    | name: Str
    | scheme: List(Int)

type State =
    | bindings: List(Binding)
    | total: Int

type Close =
    | closeOwnerSlot: Int
    | closeSpan: Maybe(Int)
    | closeOuterBindings: List(Binding)

type Item =
    | ItemLet(Str)
    | ItemRec(Str, Maybe(Int))
    | ItemAt(Maybe(Int), Item)
    | ItemHook(Int -> Int)

let makeRestore (owner: Int) (span: Maybe(Int)) (outerBindings: List(Binding)) = Close(closeOwnerSlot = owner, closeSpan = span, closeOuterBindings = outerBindings)

let recursive closePending (pending: List(Close)) (state: State) =
    match pending with
        | [] -> state.total + Ashes.Collection.List.length(state.bindings)
        | entry :: rest -> closePending(rest)((state with bindings = entry.closeOuterBindings, total = state.total + entry.closeOwnerSlot))

let recursive lowerItems restore (items: List(Item)) (seen: (Str, Int)) (pending: List(Close)) (state: State) =
    match items with
        | [] -> closePending(pending)(state)
        | ItemRec(name, span) :: rest ->
            match state with
                | State { bindings = outerBindings } ->
                    lowerItems(restore)(rest)(match seen with
                        | (_last, count) -> (name, count + 1))(restore(span)(outerBindings) :: pending)((state with bindings = Binding(name = name, scheme = [1, 2, 3]) :: outerBindings))
        | ItemLet(_name) :: rest -> lowerItems(restore)(rest)(seen)(pending)(state)
        | ItemAt(_span, inner) :: rest -> lowerItems(restore)(inner :: rest)(seen)(pending)(state)
        | ItemHook(_hook) :: rest -> lowerItems(restore)(rest)(seen)(pending)(state)

let probe (n: Int) =
    lowerItems(makeRestore(n % 2))([ItemRec("a" + Ashes.Text.fromInt(n))(Some(n)), ItemLet("b"), Some(2)
    |> ItemRec("c")
    |> ItemAt(Some(9)), ItemRec("d")(None)])(("", 0))([])(State(bindings = [], total = n))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(40)
|> Ashes.IO.print
