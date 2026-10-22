// A curried loop's partial applications capture a record with an owned child, so each closure gets
// a dropper that releases the record through the record's structural dropper. That dropper can be
// synthesized first by the closure dropper itself, and the program must still define it.
// expect: 1000
type State =
    | names: List(Str)
    | index: Int

let recursive rounds (state: State) (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(state)(n - 1)(total + state.index)

let state = State(names = ["a"], index = 1)

0
|> rounds(state)(1000)
|> Ashes.IO.print
