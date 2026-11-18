// Field access reads a field of a binding; a read through a field of a field is not field access,
// so `state.supply.nextId` is a qualified name whose qualifier names no module.
// expect-compile-error: Unknown module 'state.supply'.
type Supply =
    | nextId: Int

type State =
    | supply: Supply
    | name: Str

let next (state: State) = state.supply.nextId

State(supply = Supply(nextId = 7), name = "s")
|> next
|> Ashes.IO.print
