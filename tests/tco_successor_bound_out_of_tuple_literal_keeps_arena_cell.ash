// A loop whose successor state is bound out of a tuple literal built in the same iteration: the
// back edge copies that arena state onto the reference-counted heap and releases what the
// dying source held, and its list field still starts with an arena cell, which has no reference
// count to release. Releasing it read the pair's own integer as a count and, when it was 1, put
// arena memory on the free list.
// expect: 1545000
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
|> rounds(300)
|> Ashes.IO.print
