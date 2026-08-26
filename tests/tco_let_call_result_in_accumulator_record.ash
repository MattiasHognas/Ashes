// expect: picked;other;picked;other;|2000
import Ashes.Collection.List as L
type Inst =
    | Jump(Str)
    | Other

type Wrapped =
    | instruction: Inst
    | location: Maybe(Int)

type Case =
    | tag: Int
    | label: Str

let taken tag cases defaultLabel =
    match cases with
        | [] -> defaultLabel
        | switchCase :: rest ->
            if switchCase.tag == tag
            then "picked"
            else defaultLabel

let recursive loop n cases acc =
    if n == 0
    then L.reverse(acc)
    else
        let label = taken(n % 2)(cases)("other")
        in loop(n - 1)(cases)(Wrapped(instruction = Jump(label), location = None) :: acc)

let recursive churn n acc =
    if n == 0
    then acc
    else churn(n - 1)("churn" + Ashes.Text.fromInt(n) :: acc)

let recursive render items =
    match items with
        | [] -> ""
        | Wrapped { instruction = Jump(label), location = _ } :: rest -> label + ";" + render(rest)
        | _ :: rest -> "other;" + render(rest)

let cases = [Case(tag = 0, label = "zero"), Case(tag = 1, label = "one")]

let result = loop(4)(cases)([])

let noise = churn(2000)([])

Ashes.IO.print(render(result) + "|" + Ashes.Text.fromInt(L.length(noise)))
