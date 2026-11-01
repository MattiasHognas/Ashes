// expect: 502500

type Ty =
    | TyInt
    | TyList(Ty)

type St =
    | instructions: List(Int)
    | names: List(Str)
    | next: Int
    | known: List(Ty)

type Err =
    | Bad(Str)

type Res =
    | state: St
    | error: Maybe(Err)

type Pat =
    | PVar(Str)
    | PTuple(List(Pat))

let emit (item: Int) (state: St) = state with instructions = item :: state.instructions

let fresh (state: St) = ((state with next = state.next + 1), state.next)

let lowerVar (name: Str) (temp: Int) (state: St) =
    if name == ""
    then Res(state = state, error = Some(Bad(name)))
    else Res(state = emit(temp)(state), error = None)

let lowerLoaded pat lower loaded =
    match loaded with
        | (state, temp) -> lower(pat)(temp)(state)

let loadField (index: Int) pat lower (state: St) =
    state
    |> fresh
    |> (given (pair) ->
        match pair with
            | (loadState, temp) -> (emit(temp + index)(loadState), temp))
    |> lowerLoaded(pat)(lower)

let recursive lowerFields (pats: List(Pat)) (index: Int) lower (result: Res) =
    match result with
        | Res { error = Some(_error) } -> result
        | Res { state = state } ->
            match pats with
                | [] -> result
                | pat :: rest ->
                    state
                    |> loadField(index)(pat)(lower)
                    |> lowerFields(rest)(index + 1)(lower)

let recursive lowerPat (pat: Pat) (temp: Int) (state: St) =
    match pat with
        | PVar(name) -> lowerVar(name)(temp)(state)
        | PTuple(parts) -> lowerFields(parts)(0)(lowerPat)(Res(state = state, error = None))

let run (seed: Int) =
    (let start = St(instructions = [seed], names = ["a", "b"], next = seed, known = [TyList(TyInt)])
    in
        match lowerPat(PTuple([PVar("left"), PVar("right")]))(0)(start) with
            | Res { state = St { next = next } } -> next)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + run(n))

0
|> rounds(1000)
|> Ashes.IO.print
