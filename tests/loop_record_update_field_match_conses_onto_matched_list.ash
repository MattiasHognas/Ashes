// expect: 165
type State =
    | instructions: List(Int)
    | names: List(Str)
    | next: Int

type Step =
    | state: State
    | error: Maybe(Str)

let recursive alone (n: Int) (state: State) =
    if n == 0
    then state
    else
        alone(n - 1)((state with instructions = (match state with
            | State { instructions = is } -> n :: is)))

let recursive nested (n: Int) (extra: List(Str)) (step: Step) =
    match step with
        | Step { state = state } ->
            if n == 0
            then step
            else
                nested(n - 1)(extra)(Step(state = (state with names = extra, instructions = (match step with
                    | Step { state = State { instructions = is } } -> n :: is)), error = Some("x")))

let total (seed: Int) =
    (let a =
        match alone(3)(State(instructions = [seed], names = ["a"], next = seed)) with
            | State { instructions = is, next = next } -> next + Ashes.Collection.List.length(is)
    in
        let b =
            match nested(4)(["x", "y"])(Step(state = State(instructions = [seed], names = ["a", "b"], next = seed), error = None)) with
                | Step { state = State { instructions = is, names = names } } -> Ashes.Collection.List.length(is) + Ashes.Collection.List.length(names)
        in a + b)

let recursive rounds (n: Int) (sum: Int) =
    if n == 0
    then sum
    else rounds(n - 1)(sum + total(n))

0
|> rounds(10)
|> Ashes.IO.print
