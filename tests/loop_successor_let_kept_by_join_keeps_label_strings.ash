// expect: s1,s0 jif 5 s0|jump s1 1:s0 1000:s999 10000:s9999
// A loop builds a record holding a label string a `let` owns, conses it onto its accumulator in
// another `let`, and passes that on through a join (`let next = if c then acc2 else acc2`). The
// label is released at the back edge while the arena keeps the record, so the record keeps an
// arena copy of it, at 1, 1,000 and 10,000 rounds.
type Wrapped =
    | name: Str
    | location: Maybe(Int)

type Kind =
    | Jump(Str)
    | JumpIfFalse(Int, Str)
    | Label(Str)

type Instruction =
    | instruction: Kind
    | location: Maybe(Int)

let recursive listGet (index: Int) list =
    match list with
        | [] -> None
        | head :: tail ->
            if index == 0
            then Some(head)
            else listGet(index - 1)(tail)

let recursive labels (count: Int) (index: Int) =
    if index >= count
    then []
    else "s" + Ashes.Text.fromInt(index) :: labels(count)(index + 1)

let recursive wrap names (i: Int) (count: Int) acc =
    if i >= count
    then acc
    else
        let current =
            match listGet(i)(names) with
                | Some(label) -> label
                | None -> "none"
        in
            let grown = Wrapped(name = current, location = None) :: acc
            in
                let next =
                    if i + 1 < count
                    then grown
                    else grown
                in wrap(names)(i + 1)(count)(next)

let recursive dispatch names (i: Int) (count: Int) (temp: Int) acc =
    if i >= count
    then Ashes.Collection.List.reverse(acc)
    else
        let falseTarget =
            if i + 1 < count
            then "d" + Ashes.Text.fromInt(i + 1)
            else
                match listGet(0)(names) with
                    | Some(label) -> label
                    | None -> "none"
        in
            let jumpFalse = Instruction(instruction = JumpIfFalse(temp)(falseTarget), location = None)
            in
                let currentLabel =
                    match listGet(i)(names) with
                        | Some(label) -> label
                        | None -> "s" + Ashes.Text.fromInt(i)
                in
                    let jumpState = Instruction(instruction = Jump(currentLabel), location = None)
                    in
                        let grown = jumpState :: jumpFalse :: acc
                        in
                            let next =
                                if i + 1 < count
                                then Instruction(instruction = Label("d" + Ashes.Text.fromInt(i + 1)), location = None) :: grown
                                else grown
                            in dispatch(names)(i + 1)(count)(temp + 2)(next)

let show (wrapped: Instruction) =
    match wrapped.instruction with
        | Jump(label) -> "jump " + label
        | JumpIfFalse(condition, label) -> "jif " + Ashes.Text.fromInt(condition) + " " + label
        | Label(label) -> "label " + label

let nameOf (wrapped: Wrapped) = wrapped.name

let firstName (count: Int) =
    match wrap(labels(count)(0))(0)(count)([]) with
        | [] -> "none"
        | first :: _ -> first.name

let dispatched =
    dispatch(labels(2)(0))(1)(2)(5)([])

[
    []
    |> wrap(labels(2)(0))(0)(2)
    |> Ashes.Collection.List.map(nameOf)
    |> Ashes.Text.join(","),
    dispatched
    |> Ashes.Collection.List.map(show)
    |> Ashes.Text.join("|"),
    "1:" + firstName(1),
    "1000:" + firstName(1000),
    "10000:" + firstName(10000)
]
|> Ashes.Text.join(" ")
|> Ashes.IO.print
