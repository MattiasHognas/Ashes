// expect: 40
// A record of flags is folded over a program's instructions beside the instruction list, which the
// loop cannot place on the reference-counted heap. The record holds no part of the list, so it is
// placed anyway and each iteration's record is released once the next one replaces it.
type Kind =
    | PrintInt(Int)
    | PrintStr(Int)
    | PrintBool(Int)
    | ConcatStr(Int, Int, Int, Int)
    | Other(Int)

type Instruction =
    | instruction: Kind
    | location: Maybe(Int)

type Function =
    | label: Str
    | instructions: List(Instruction)

type Uses =
    | printInt: Bool
    | printStr: Bool
    | printBool: Bool
    | concatStr: Bool

let emptyUses =
    Uses(
        printInt = false,
        printStr = false,
        printBool = false,
        concatStr = false
    )

let includeInstruction operation (uses: Uses) =
    match operation with
        | PrintInt(_) -> uses with printInt = true
        | PrintStr(_) -> uses with printStr = true
        | PrintBool(_) -> uses with printBool = true
        | ConcatStr(_, _, _, _) -> uses with concatStr = true
        | _ -> uses

let recursive collectInstructionUses instructions uses =
    match instructions with
        | [] -> uses
        | Instruction { instruction = operation } :: rest ->
            uses
            |> includeInstruction(operation)
            |> collectInstructionUses(rest)

let recursive collectFunctionUses functions uses =
    match functions with
        | [] -> uses
        | Function { instructions = instructions } :: rest ->
            uses
            |> collectInstructionUses(instructions)
            |> collectFunctionUses(rest)

let functionOf (n: Int) = Function(label = "f" + Ashes.Text.fromInt(n), instructions = [Instruction(instruction = PrintInt(n), location = None), Instruction(instruction = Other(1), location = Some(n)), Instruction(instruction = PrintInt(2), location = None)])

let probe (n: Int) =
    match emptyUses
    |> collectInstructionUses([Instruction(instruction = PrintInt(3), location = None)])
    |> collectFunctionUses([functionOf(n), functionOf(n + 1)]) with
        | Uses { printInt = printInt, concatStr = concatStr } ->
            if printInt && !concatStr
            then 1
            else 0

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(40)
|> Ashes.IO.print
