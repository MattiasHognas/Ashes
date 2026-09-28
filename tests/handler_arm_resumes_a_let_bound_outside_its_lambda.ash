// A handler arm inside a lambda resumes with a value let-bound outside the lambda: the lambda
// captures it, since the arm's own parameters and `resume` are the only names the arm binds.
// expect: 42
capability Source(a) =
    | get : Unit -> a

let result =
    (let captured = 8
    in
        ((given (unit: Unit) ->
            handle perform Source.get(Unit) with
                | Source.get(_) -> resume(captured)
                | return(handled) -> handled + 34))(Unit))

Ashes.IO.print(result)
