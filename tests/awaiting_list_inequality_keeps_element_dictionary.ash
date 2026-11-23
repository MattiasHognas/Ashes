// expect: no yes
type Key =
    | Key(Int)

implement Eq(Key) =
    | equal =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Key(x), Key(y)) -> x % 2 == y % 2

let job : List(a) -> List(a) -> Task(Str, Bool) requires {Eq(a)} =
    given (left) ->
        given (right) ->
            async(let recursive step n =
                if n == 0
                then left != right
                else
                    match await Ashes.Task.sleep(1) with
                        | Ok(_) -> step(n - 1)
                        | Error(_) -> false
            in step(1))

let render operation =
    match Ashes.Task.run(operation) with
        | Ok(value) ->
            if value
            then "yes"
            else "no"
        | Error(message) -> message

Ashes.IO.print(render(job([Key(2)])([Key(4)])) + " " + render(job([Key(2)])([Key(3)])))
