// expect: true|false|false|false|true|true|true|false|true|true|true|true|true|true
// Short-circuit: the right operand's side effect must not run when the left operand already
// determines the result.
let render value =
    if value
    then "true"
    else "false"

let sideEffect (n: Int) =
    n
    |> Ashes.IO.print
    |> (given (_) -> true)

// Truth tables.
let andTT = render(true && true) + "|" + render(true && false) + "|" + render(false && true) + "|" + render(false && false)

let orTT = render(true || true) + "|" + render(true || false) + "|" + render(false || true) + "|" + render(false || false)

let andShortCircuits =
    render(if false && sideEffect(1)
    then false
    else true)

let orShortCircuits = render((true || sideEffect(2)) && true)

// Precedence: && binds tighter than ||, both left-associative, mixing cleanly with comparisons.
let a = true

let b = false

let c = true

let mixed1 = render(a && b || c)

let mixed2 = render(a || b && c)

let mixed3 = render((a || b) && c)

let comparisons = render(1 > 0 && 2 > 1)

let result = andTT + "|" + orTT + "|" + andShortCircuits + "|" + orShortCircuits + "|" + mixed1 + "|" + mixed2 + "|" + mixed3 + "|" + comparisons

Ashes.IO.print(result)
