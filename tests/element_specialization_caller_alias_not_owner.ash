// expect: 30
let recursive parseParams reversed (tokens: List(Str)) =
    match tokens with
        | [] -> (Ashes.Collection.List.reverse(reversed), 0)
        | parameter :: rest -> parseParams((parameter, Some(parameter + "!")) :: reversed)(rest)

let recursive parameterNames parameters =
    match parameters with
        | [] -> []
        | (name, _annotation) :: tail -> name :: parameterNames(tail)

let probe (n: Int) =
    (let header =
        if n < 0
        then ([], [n])
        else
            match parseParams([])(["seen" + Ashes.Text.fromInt(n), "h"]) with
                | (parameters, after) -> (parameters, [after])
    in
        match header with
            | (parameters, requirements) ->
                Ashes.Collection.List.length(parameterNames(parameters)) + Ashes.Collection.List.length(requirements))

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))

0
|> loop(10)
|> Ashes.IO.print
