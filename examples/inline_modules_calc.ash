import Ashes.IO

module Lex =
    let digit = given (c) -> c - 48
    let recursive scan = given (cs) ->
        match cs with
            | [] -> []
            | c :: rest -> digit(c) :: scan(rest)

module Calc =
    let recursive sum = given (ns) ->
        match ns with
            | [] -> 0
            | n :: rest -> n + sum(rest)
    let recursive product = given (ns) ->
        match ns with
            | [] -> 1
            | n :: rest -> n * product(rest)

let digits = Lex.scan([49, 50, 51, 52])

Ashes.IO.print(Ashes.Text.fromInt(Calc.sum(digits)) + " " + Ashes.Text.fromInt(Calc.product(digits)))
