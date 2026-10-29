// A record state threaded through a loop by a helper returning `(value, nextState)` is returned
// beside the reversed accumulator: the result tuple holds the one reference to the final state.
// expect: 4 2 ember 28
type ThreadState =
    | names: List(Str)
    | counter: Int

type Item =
    | value: Int
    | tag: Str

let stepOne (state: ThreadState) (x: Int) =
    if x % 2 == 0
    then (x + 1, (state with names = Ashes.Text.fromInt(x) + "ember" :: state.names, counter = state.counter + 1))
    else (x, state)

let recursive threadLoop (state: ThreadState) remaining acc =
    match remaining with
        | [] -> (Ashes.Collection.List.reverse(acc), state)
        | x :: tail ->
            match stepOne(state)(x) with
                | (y, nextState) -> threadLoop(nextState)(tail)(Item(value = y, tag = "a") :: acc)

match threadLoop(ThreadState(names = [], counter = 0))([1, 2, 4, 5])([]) with
    | (built, ThreadState { names = last :: _, counter = counter }) ->
        Ashes.IO.print(Ashes.Text.fromInt(Ashes.Collection.List.length(built)) + " " + Ashes.Text.fromInt(counter) + " " + Ashes.Text.substring(last)(1)(5) + " " + Ashes.Text.fromInt(Ashes.Collection.List.foldLeft(given (sum) ->
            given (item) -> sum + item.value)(0)(built) * 2))
    | _ -> Ashes.IO.print("none")
