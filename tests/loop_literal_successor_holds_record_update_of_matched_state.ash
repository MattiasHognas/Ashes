// expect: 2120
type State =
    | instructions: List(Int)
    | names: List(Str)

type Step =
    | state: State
    | count: Int

let recursive consOnto (n: Int) (step: Step) =
    match step with
        | Step { state = state, count = count } ->
            if n == 0
            then step
            else consOnto(n - 1)(Step(state = (state with instructions = n :: state.instructions), count = count + 1))

let recursive rebindNames (n: Int) (step: Step) =
    match step with
        | Step { state = state, count = count } ->
            if n == 0
            then step
            else
                let names = state.names
                in rebindNames(n - 1)(Step(state = (state with names = names, instructions = n :: state.instructions), count = count + 1))

let measure (step: Step) =
    match step with
        | Step { state = State { instructions = instructions, names = names }, count = count } -> count + Ashes.Collection.List.length(instructions) + Ashes.Collection.List.length(names)

let run (seed: Int) =
    (let start = Step(state = State(instructions = [seed], names = ["a", "b"]), count = 0)
    in
        measure(consOnto(20)(start)) + measure(rebindNames(30)(start)))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

0
|> rounds(20)
|> Ashes.IO.print
