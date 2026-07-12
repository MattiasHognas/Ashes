// expect: 42

capability A =
    | getA : Unit -> Int

capability B =
    | getB : Unit -> Int

let work =
    given (u) -> A.getA(Unit) + B.getB(Unit)

let handleB =
    given (w) ->
        handle w(Unit) with
            | B.getB(_) -> resume(2)
            | return(r) -> r

let result =
    handle handleB(work) with
        | A.getA(_) -> resume(40)
        | return(r) -> r

Ashes.IO.print(result)
