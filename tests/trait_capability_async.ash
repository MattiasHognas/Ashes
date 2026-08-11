// expect: 42|true
import Ashes.Trait
capability Offset =
    | current : Unit -> Int

trait Adjust(a) =
    | adjust : a -> Int needs {Offset}

implement Adjust(Int) =
    | adjust =
        given (value) -> value + perform Offset.current(Unit)

let adjusted : a -> Int needs {Offset} requires {Adjust(a)} =
    given (value) -> Adjust.adjust(value)

let delayedSame : a -> Task(Str, Bool) requires {Eq(a)} =
    given (value) ->
        async(match await async(value) with
            | Ok(resumed) -> value == resumed
            | Error(_) -> false)

let adjustedValue =
    handle adjusted(40) with
        | Offset.current(_) -> resume(2)
        | return(value) -> value

let sameValue =
    match Ashes.Task.run(delayedSame([1, 2])) with
        | Ok(value) -> value
        | Error(_) -> false

Ashes.IO.print(Show.show(adjustedValue) + "|" + Show.show(sameValue))
