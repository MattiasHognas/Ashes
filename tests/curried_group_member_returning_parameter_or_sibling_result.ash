// expect: n:Z
import Ashes.IO
import Ashes.Collection.List
type Ex =
    | ELet(Ex)
    | EMatch(Str)
    | ENum(Int)

let recursive collect (expression: Ex) (found: List((Str, Str))) =
    match expression with
        | ELet(body) -> collect(body)(found)
        | EMatch(name) -> recordMatched(name)(found)
        | _ -> found
and recordMatched (name: Str) (found: List((Str, Str))) = (name, "Z") :: found

[]
|> collect(ELet(EMatch("n")))
|> Ashes.Collection.List.map(given (p: (Str, Str)) ->
    match p with
        | (a, b) -> a + ":" + b)
|> Ashes.Text.join(",")
|> Ashes.IO.print
