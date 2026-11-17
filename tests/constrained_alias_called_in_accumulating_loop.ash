// expect: 21
// An alias of a function with three Eq requirements is called from a loop that
// accumulates a list. The alias hands each dictionary on to the function it names.
let recursive hasEntry category operation operand entries =
    match entries with
        | [] -> None
        | (entryCategory, entryOperand, entryOperation, _label) :: rest ->
            if entryCategory == category && entryOperation == operation && entryOperand == operand
            then Some(7)
            else hasEntry(category)(operation)(operand)(rest)

let alias = hasEntry

let recursive sum values total =
    match values with
        | [] -> total
        | value :: rest -> sum(rest)(total + value)

let recursive rounds count values =
    if count == 0
    then sum(values)(0)
    else
        let label = "label-" + Ashes.Text.fromInt(count)
        in
            let entries = [("Eq", count, "equal", label)]
            in
                match alias("Eq")("equal")(count)(entries) with
                    | Some(value) -> rounds(count - 1)(value :: values)
                    | _ -> Ashes.IO.panic("alias lookup failed")

[]
|> rounds(3)
|> Ashes.Text.fromInt
|> Ashes.IO.print
