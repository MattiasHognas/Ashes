// expect: 3000

type State =
    | slot: Maybe(Int)
    | names: List(Str)

let headSlotOf (slots: List(Int)) =
    match slots with
        | slot :: _rest -> Some(slot)
        | [] -> None

let restSlotsOf (slots: List(Int)) =
    match slots with
        | _slot :: rest -> rest
        | [] -> []

let recursive loop (items: List(Int)) (slots: List(Int)) (state: State) =
    match items with
        | [] ->
            match state.slot with
                | Some(s) -> s
                | None -> 0
        | _ :: rest ->
            loop(rest)(restSlotsOf(slots))((state with slot = headSlotOf(slots)))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + loop([1, 2, 3, 4, 5, 6])([1, 2, 3, 4, 5, 6])(State(slot = None, names = ["a"])))

0
|> rounds(500)
|> Ashes.IO.print
