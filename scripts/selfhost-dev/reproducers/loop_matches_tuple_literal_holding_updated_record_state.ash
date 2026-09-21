type PassState =
    | cache: List((Int, Int))
    | seen: Int

let emptyState = PassState(cache = [], seen = 0)

let step (n: Int) (state: PassState) = ((state with cache = (n, n + 1) :: state.cache), n)

let recursive pass (n: Int) (state: PassState) (total: Int) =
    if n == 0
    then total + Ashes.Collection.List.length(state.cache)
    else
        match ((state with cache = (n, n + 1) :: state.cache), n) with
            | (nextState, value) -> pass(n - 1)(nextState)(total + value)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + pass(100)(emptyState)(0))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
