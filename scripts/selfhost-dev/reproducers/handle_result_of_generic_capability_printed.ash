capability Source(a) =
    | get : Unit -> a

let result =
    handle perform Source.get(Unit) with
        | Source.get(_) -> resume(8)
        | return(handled) -> handled

Ashes.IO.print(result)
