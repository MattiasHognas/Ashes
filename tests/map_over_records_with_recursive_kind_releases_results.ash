// expect: 1920
// A lowering-shaped fold whose instruction kind is recursive (a block holds a list of kinds) maps
// its functions through a record update. The arena deep copy cannot reproduce a value reaching the
// recursive kind, so the call takes the ordinary path, whose list holds each mapped function as an
// owned head, instead of a reuse specialization keeping every result in an arena cell. Built with
// the leak check, the program ends with one live cell.
type Location =
    | filePath: Str
    | line: Int
    | column: Int

type Kind =
    | LoadConst(Int, Int)
    | Call(Int, Str, Int)
    | Label(Str)
    | Block(List(Kind), Maybe(Str))

type Instruction =
    | kind: Kind
    | location: Maybe(Location)

type Function =
    | label: Str
    | instructions: List(Instruction)
    | localCount: Int

type ProgramGroup =
    | functions: List(Function)
    | context: Maybe(Str)

type OwnerGroup =
    | retired: List(Int)
    | names: List(Str)

type State =
    | reversed: List(Instruction)
    | programState: ProgramGroup
    | ownerState: OwnerGroup
    | span: Int

let recursive renumber (retired: List(Int)) (instructions: List(Instruction)) =
    match instructions with
        | [] -> []
        | Instruction { kind = LoadConst(target, value), location = location } :: rest -> Instruction(kind = LoadConst(target + Ashes.Collection.List.length(retired))(value), location = location) :: renumber(retired)(rest)
        | instruction :: rest -> instruction :: renumber(retired)(rest)

let locationOf (span: Int) (context: Maybe(Str)) =
    match context with
        | Some(path) ->
            if span % 5 == 0
            then None
            else Some(Location(filePath = path + Ashes.Text.fromInt(span % 3), line = span, column = span % 7))
        | None -> None

let emit (kind: Kind) (state: State) =
    match state with
        | State { reversed = instructions, span = span, programState = ProgramGroup { context = context } } ->
            let wrapped = Instruction(kind = kind, location = locationOf(span)(context))
            in state with reversed = wrapped :: instructions

let finishFunction (label: Str) (state: State) =
    match state with
        | State { reversed = instructions, programState = ProgramGroup { functions = functions }, ownerState = OwnerGroup { retired = retired } } ->
            let function =
                Function(
                    label = label,
                    instructions = instructions
                    |> Ashes.Collection.List.reverse
                    |> renumber(retired),
                    localCount = 3
                )
            in
                state
                |> (given (current: State) -> current with reversed = [])
                |> (given (current: State) ->
                    let group = current.programState
                    in current with programState = (group with functions = function :: functions))

let recursive lowerBody (n: Int) (state: State) =
    if n == 0
    then state
    else
        state
        |> (given (s: State) -> s with span = s.span + 1)
        |> emit(LoadConst(n)(n * 2))
        |> emit(Call(n)("callee_" + Ashes.Text.fromInt(n))(1))
        |> lowerBody(n - 1)

let recursive lowerFunctions (count: Int) (state: State) =
    if count == 0
    then state
    else
        state
        |> lowerBody(6)
        |> finishFunction("lambda_" + Ashes.Text.fromInt(count))
        |> lowerFunctions(count - 1)

let recursive relabel (instructions: List(Instruction)) =
    match instructions with
        | [] -> []
        | (Instruction { kind = Call(target, callee, arity) } as instruction) :: rest -> (instruction with kind = Call(target)(callee + "_o")(arity)) :: relabel(rest)
        | instruction :: rest -> instruction :: relabel(rest)

let optimizeFunction (function: Function) = function with instructions = relabel(function.instructions)

let recursive countLocated (instructions: List(Instruction)) (total: Int) =
    match instructions with
        | [] -> total
        | Instruction { location = Some(Location { line = line }) } :: rest -> countLocated(rest)(total + line % 3)
        | _ :: rest -> countLocated(rest)(total)

let recursive sizes (functions: List(Function)) (total: Int) =
    match functions with
        | [] -> total
        | Function { instructions = instructions } :: rest ->
            total
            |> countLocated(instructions)
            |> sizes(rest)

let lowerProgram (count: Int) =
    match lowerFunctions(count)(State(reversed = [], programState = ProgramGroup(functions = [], context = Some("lib/Module/Path/")), ownerState = OwnerGroup(retired = [1, 2], names = []), span = 0)) with
        | State { programState = ProgramGroup { functions = functions } } -> functions

let optimized =
    200
    |> lowerProgram
    |> Ashes.Collection.List.map(optimizeFunction)

0
|> sizes(optimized)
|> Ashes.Text.fromInt
|> Ashes.IO.print
